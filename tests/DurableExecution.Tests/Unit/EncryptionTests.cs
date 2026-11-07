using System.Text.Json.Nodes;
using DurableExecution.History;
using DurableExecution.PostgreSql;
using DurableExecution.Sqlite;
using DurableExecution.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace DurableExecution.Tests.Unit;

/// <summary>
/// Raw access to one provider's stored records for the encryption tests:
/// the tests act as the disk — reading what is actually at rest and
/// rewriting it — exactly as the corruption conformance fixtures do.
/// </summary>
public interface IEncryptionFixture : IDisposable
{
    /// <summary>A new store over this fixture's history (supersedes and disposes the previous one).</summary>
    IHistoryStore CreateStore(HistoryEncryption? key, int snapshotInterval = 0);

    /// <summary>Disposes the fixture's current store, releasing any lock it holds.</summary>
    void ReleaseStores();

    /// <summary>The stored record texts, in event order.</summary>
    List<string> ReadRawRecords();

    /// <summary>Replaces the stored records outright with the given texts, in order.</summary>
    void WriteRawRecords(IReadOnlyList<string> records);

    /// <summary>The stored snapshot payload text, or "" when no snapshot exists.</summary>
    string ReadRawSnapshot();
}

/// <summary>
/// Payload encryption at rest, per provider: records (and snapshots) are
/// AES-256-GCM envelopes on the medium, the sealed record model survives
/// the roundtrip untouched, and every cross-mode or tampered open is a
/// loud <see cref="HistoryCorruptionException"/> — wrong key, missing
/// key, plaintext under an encrypted store, flipped ciphertext (caught
/// by the envelope seal, and by the GCM tag when the seal is recomputed).
/// Written once, run per provider.
/// </summary>
public abstract class EncryptionTests(IEncryptionFixture fixture) : IDisposable
{
    protected IEncryptionFixture Fixture { get; } = fixture;

    public void Dispose() => Fixture.Dispose();

    private void AppendSample(IHistoryStore store)
    {
        store.Append(HistoryEvent.WorkflowStarted("inst-1", "order/v1",
            DurableExecution.Execution.Fingerprints.InputFingerprint(null)));
        store.Append(HistoryEvent.ActivityScheduled("inst-1", 1, "ChargeCard", new string('b', 64), new string('c', 64)));
        store.Append(HistoryEvent.ActivityAttemptStarted("inst-1", 1, 1));
        store.Append(HistoryEvent.ActivityCompleted("inst-1", 1, "ChargeCard", "txn-secret-123"));
    }

    private IHistoryStore LoadedEncrypted(int snapshotInterval = 0)
    {
        var store = Fixture.CreateStore(TestEncryption.KeyA, snapshotInterval);
        store.Load();
        return store;
    }

    [Fact]
    public void Roundtrip_PreservesEvents_AndRawAtRestIsCiphertext()
    {
        AppendSample(LoadedEncrypted());

        string raw = string.Join("\n", Fixture.ReadRawRecords());
        Assert.Contains("\"enc\":\"aes-256-gcm\"", raw);
        Assert.Contains($"\"kid\":\"{TestEncryption.KeyA.KeyId}\"", raw);
        Assert.DoesNotContain("ChargeCard", raw);
        Assert.DoesNotContain("txn-secret-123", raw);
        Assert.DoesNotContain("order/v1", raw);

        var reloaded = Fixture.CreateStore(TestEncryption.KeyA).Load();
        Assert.Equal(4, reloaded.Events.Count);
        Assert.Equal("ChargeCard", reloaded.Events[1].GetString("activity"));
        Assert.Equal("txn-secret-123", reloaded.Events[3].GetResult<string>());
    }

    [Fact]
    public void WrongKey_IsRefused_ByKeyId()
    {
        AppendSample(LoadedEncrypted());

        var ex = Assert.Throws<HistoryCorruptionException>(
            () => Fixture.CreateStore(TestEncryption.KeyB).Load());
        Assert.Contains("key id mismatch", ex.Message);
    }

    [Fact]
    public void MissingKey_IsRefused()
    {
        AppendSample(LoadedEncrypted());

        var ex = Assert.Throws<HistoryCorruptionException>(
            () => Fixture.CreateStore(null).Load());
        Assert.Contains("no encryption key configured", ex.Message);
    }

    [Fact]
    public void PlaintextHistory_OpenedWithEncryption_IsRefused_NotMigrated()
    {
        var plain = Fixture.CreateStore(null);
        plain.Load();
        AppendSample(plain);

        var ex = Assert.Throws<HistoryCorruptionException>(
            () => Fixture.CreateStore(TestEncryption.KeyA).Load());
        Assert.Contains("not encrypted", ex.Message);
    }

    [Fact]
    public void CiphertextFlip_IsCaughtByTheEnvelopeSeal()
    {
        AppendSample(LoadedEncrypted());
        Fixture.ReleaseStores();

        var records = Fixture.ReadRawRecords();
        records[1] = FlipCiphertext(records[1], reseal: false);
        Fixture.WriteRawRecords(records);

        var ex = Assert.Throws<HistoryCorruptionException>(
            () => Fixture.CreateStore(TestEncryption.KeyA).Load());
        Assert.Contains("checksum mismatch", ex.Message);
    }

    [Fact]
    public void CiphertextFlip_WithRecomputedSeal_IsCaughtByGcmAuthentication()
    {
        AppendSample(LoadedEncrypted());
        Fixture.ReleaseStores();

        var records = Fixture.ReadRawRecords();
        records[1] = FlipCiphertext(records[1], reseal: true);
        Fixture.WriteRawRecords(records);

        var ex = Assert.Throws<HistoryCorruptionException>(
            () => Fixture.CreateStore(TestEncryption.KeyA).Load());
        Assert.Contains("authentication", ex.Message);
    }

    [Fact]
    public void Snapshot_IsEncrypted_LoadsWithKey_AndPlainStoreIsRefused()
    {
        AppendSample(LoadedEncrypted(snapshotInterval: 2));

        string rawSnapshot = Fixture.ReadRawSnapshot();
        Assert.Contains("\"enc\":\"aes-256-gcm\"", rawSnapshot);
        Assert.DoesNotContain("ChargeCard", rawSnapshot);

        var snapshotStore = (IHistorySnapshotStore)Fixture.CreateStore(TestEncryption.KeyA, snapshotInterval: 2);
        var fromSnapshot = snapshotStore.LoadFromSnapshot();
        Assert.True(fromSnapshot.SnapshotUsed);
        Assert.Equal(4, fromSnapshot.Load.Events.Count);
        Assert.Equal("txn-secret-123", fromSnapshot.Load.Events[3].GetResult<string>());

        var plainSnapshotStore = (IHistorySnapshotStore)Fixture.CreateStore(null);
        Assert.Throws<HistoryCorruptionException>(() => plainSnapshotStore.LoadFromSnapshot());
    }

    /// <summary>Flips one base64 character inside the envelope's ciphertext; optionally recomputes the envelope seal afterwards, so the tamper survives the checksum layer and must be caught by GCM.</summary>
    private static string FlipCiphertext(string envelopeText, bool reseal)
    {
        var envelope = JsonNode.Parse(envelopeText)!.AsObject();
        string ct = envelope["ct"]!.GetValue<string>();
        char flipped = ct[4] == 'A' ? 'B' : 'A';
        envelope["ct"] = ct[..4] + flipped + ct[5..];
        if (reseal)
            envelope["sum"] = FileHistoryStore.Checksum(envelope);
        return envelope.ToJsonString();
    }
}

/// <summary>The file provider's encryption fixture: records are the log's lines, the snapshot its file.</summary>
public sealed class FileEncryptionFixture : IEncryptionFixture
{
    private readonly string _dir;

    public FileEncryptionFixture()
    {
        _dir = Path.Combine(Path.GetTempPath(), "p0-enc", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public IHistoryStore CreateStore(HistoryEncryption? key, int snapshotInterval = 0) =>
        new FileHistoryStore(_dir, snapshotInterval: snapshotInterval, encryption: key);

    public void ReleaseStores() { }

    public List<string> ReadRawRecords()
    {
        string path = Path.Combine(_dir, "history.log");
        return File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
    }

    public void WriteRawRecords(IReadOnlyList<string> records) =>
        File.WriteAllText(Path.Combine(_dir, "history.log"),
            records.Count == 0 ? "" : string.Join("\n", records) + "\n");

    public string ReadRawSnapshot()
    {
        string path = Path.Combine(_dir, "history.snapshot.json");
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    public void Dispose() { }
}

/// <summary>The SQL Server encryption fixture: records are rows in a throwaway database.</summary>
public sealed class SqlEncryptionFixture : IEncryptionFixture
{
    private const string Instance = "inst-1";

    private readonly (string Name, string Connection) _database = SqlTestDatabases.CreateDatabase("enc");
    private IHistoryStore? _current;

    public IHistoryStore CreateStore(HistoryEncryption? key, int snapshotInterval = 0)
    {
        (_current as IDisposable)?.Dispose();
        _current = new SqlHistoryStore(_database.Connection, Instance, snapshotInterval: snapshotInterval, encryption: key);
        return _current;
    }

    public void ReleaseStores()
    {
        (_current as IDisposable)?.Dispose();
        _current = null;
    }

    public List<string> ReadRawRecords() => SqlTestDatabases.ReadPayloads(_database.Connection, Instance);

    public void WriteRawRecords(IReadOnlyList<string> records)
    {
        using var conn = new SqlConnection(_database.Connection);
        conn.Open();
        using (var create = new SqlCommand(
            "IF OBJECT_ID(N'dbo.HistoryEvents', N'U') IS NULL " +
            "CREATE TABLE dbo.HistoryEvents (InstanceId NVARCHAR(128) NOT NULL, N BIGINT NOT NULL, " +
            "Payload NVARCHAR(MAX) NOT NULL, CONSTRAINT PK_HistoryEvents PRIMARY KEY (InstanceId, N));", conn))
            create.ExecuteNonQuery();
        using (var delete = new SqlCommand("DELETE FROM dbo.HistoryEvents WHERE InstanceId = @instance", conn))
        {
            delete.Parameters.Add(new SqlParameter("@instance", System.Data.SqlDbType.NVarChar, 128) { Value = Instance });
            delete.ExecuteNonQuery();
        }
        for (int i = 0; i < records.Count; i++)
        {
            using var insert = new SqlCommand(
                "INSERT INTO dbo.HistoryEvents (InstanceId, N, Payload) VALUES (@instance, @n, @payload)", conn);
            insert.Parameters.Add(new SqlParameter("@instance", System.Data.SqlDbType.NVarChar, 128) { Value = Instance });
            insert.Parameters.Add(new SqlParameter("@n", System.Data.SqlDbType.BigInt) { Value = (long)(i + 1) });
            insert.Parameters.Add(new SqlParameter("@payload", System.Data.SqlDbType.NVarChar, -1) { Value = records[i] });
            insert.ExecuteNonQuery();
        }
    }

    public string ReadRawSnapshot()
    {
        using var conn = new SqlConnection(_database.Connection);
        conn.Open();
        using var exists = new SqlCommand(
            "SELECT COUNT(*) FROM sys.tables WHERE name = 'HistorySnapshots'", conn);
        if ((int)exists.ExecuteScalar()! == 0)
            return "";
        using var cmd = new SqlCommand(
            "SELECT Payload FROM dbo.HistorySnapshots WHERE InstanceId = @instance", conn);
        cmd.Parameters.Add(new SqlParameter("@instance", System.Data.SqlDbType.NVarChar, 128) { Value = Instance });
        return cmd.ExecuteScalar() as string ?? "";
    }

    public void Dispose()
    {
        ReleaseStores();
        try { SqlTestDatabases.DropDatabase(_database.Name); }
        catch (SqlException) { /* best-effort cleanup of a scratch database */ }
    }
}

/// <summary>The PostgreSQL encryption fixture: records are rows in a throwaway database.</summary>
public sealed class PostgresEncryptionFixture : IEncryptionFixture
{
    private const string Instance = "inst-1";

    private readonly (string Name, string Connection) _database = PostgresTestDatabases.CreateDatabase("enc");
    private IHistoryStore? _current;

    public IHistoryStore CreateStore(HistoryEncryption? key, int snapshotInterval = 0)
    {
        (_current as IDisposable)?.Dispose();
        _current = new PostgresHistoryStore(_database.Connection, Instance, snapshotInterval: snapshotInterval, encryption: key);
        return _current;
    }

    public void ReleaseStores()
    {
        (_current as IDisposable)?.Dispose();
        _current = null;
    }

    public List<string> ReadRawRecords() => PostgresTestDatabases.ReadPayloads(_database.Connection, Instance);

    public void WriteRawRecords(IReadOnlyList<string> records)
    {
        PostgresTestDatabases.EnsureTable(_database.Connection);
        PostgresTestDatabases.Execute(_database.Connection,
            "DELETE FROM public.history_events WHERE instance_id = 'inst-1'");
        for (int i = 0; i < records.Count; i++)
            PostgresTestDatabases.InsertPayload(_database.Connection, Instance, i + 1, records[i]);
    }

    public string ReadRawSnapshot()
    {
        using var conn = new NpgsqlConnection(_database.Connection);
        conn.Open();
        using var exists = new NpgsqlCommand(
            "SELECT to_regclass('public.history_snapshots') IS NOT NULL", conn);
        if (!(bool)exists.ExecuteScalar()!)
            return "";
        using var cmd = new NpgsqlCommand(
            "SELECT payload FROM public.history_snapshots WHERE instance_id = @instance", conn);
        cmd.Parameters.Add(new NpgsqlParameter("@instance", NpgsqlTypes.NpgsqlDbType.Varchar, 128) { Value = Instance });
        return cmd.ExecuteScalar() as string ?? "";
    }

    public void Dispose()
    {
        ReleaseStores();
        try { PostgresTestDatabases.DropDatabase(_database.Name); }
        catch (NpgsqlException) { /* best-effort cleanup of a scratch database */ }
    }
}

/// <summary>The SQLite encryption fixture: records are rows in the state directory's database file.</summary>
public sealed class SqliteEncryptionFixture : IEncryptionFixture
{
    private readonly string _dir;
    private IHistoryStore? _current;

    public SqliteEncryptionFixture()
    {
        _dir = Path.Combine(Path.GetTempPath(), "p0-enc", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    private string DatabasePath => Path.Combine(_dir, "history.db");

    public IHistoryStore CreateStore(HistoryEncryption? key, int snapshotInterval = 0)
    {
        (_current as IDisposable)?.Dispose();
        _current = new SqliteHistoryStore(_dir, snapshotInterval: snapshotInterval, encryption: key);
        return _current;
    }

    public void ReleaseStores()
    {
        (_current as IDisposable)?.Dispose();
        _current = null;
    }

    private SqliteConnection OpenRaw(SqliteOpenMode mode)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = mode,
            Pooling = false,
        };
        var conn = new SqliteConnection(builder.ConnectionString);
        conn.Open();
        return conn;
    }

    public List<string> ReadRawRecords()
    {
        var records = new List<string>();
        if (!File.Exists(DatabasePath))
            return records;
        using var conn = OpenRaw(SqliteOpenMode.ReadOnly);
        using var exists = new SqliteCommand(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'HistoryEvents'", conn);
        if ((long)exists.ExecuteScalar()! == 0)
            return records;
        using var cmd = new SqliteCommand("SELECT Payload FROM HistoryEvents ORDER BY N", conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            records.Add(reader.GetString(0));
        return records;
    }

    public void WriteRawRecords(IReadOnlyList<string> records)
    {
        ReleaseStores();
        using var conn = OpenRaw(SqliteOpenMode.ReadWriteCreate);
        using (var create = new SqliteCommand(
            "CREATE TABLE IF NOT EXISTS HistoryEvents (N INTEGER NOT NULL PRIMARY KEY, Payload TEXT NOT NULL);", conn))
            create.ExecuteNonQuery();
        using (var delete = new SqliteCommand("DELETE FROM HistoryEvents", conn))
            delete.ExecuteNonQuery();
        for (int i = 0; i < records.Count; i++)
        {
            using var insert = new SqliteCommand(
                "INSERT INTO HistoryEvents (N, Payload) VALUES (@n, @payload)", conn);
            insert.Parameters.Add(new SqliteParameter("@n", SqliteType.Integer) { Value = (long)(i + 1) });
            insert.Parameters.Add(new SqliteParameter("@payload", SqliteType.Text) { Value = records[i] });
            insert.ExecuteNonQuery();
        }
    }

    public string ReadRawSnapshot()
    {
        if (!File.Exists(DatabasePath))
            return "";
        using var conn = OpenRaw(SqliteOpenMode.ReadOnly);
        using var exists = new SqliteCommand(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'HistorySnapshots'", conn);
        if ((long)exists.ExecuteScalar()! == 0)
            return "";
        using var cmd = new SqliteCommand("SELECT Payload FROM HistorySnapshots WHERE Id = 1", conn);
        return cmd.ExecuteScalar() as string ?? "";
    }

    public void Dispose() => ReleaseStores();
}

[Trait("Provider", "File")]
public sealed class EncryptionTestsFile() : EncryptionTests(new FileEncryptionFixture());

[Trait("Provider", "SqlServer")]
public sealed class EncryptionTestsSqlServer() : EncryptionTests(new SqlEncryptionFixture());

[Trait("Provider", "PostgreSql")]
public sealed class EncryptionTestsPostgreSql() : EncryptionTests(new PostgresEncryptionFixture());

[Trait("Provider", "Sqlite")]
public sealed class EncryptionTestsSqlite() : EncryptionTests(new SqliteEncryptionFixture());
