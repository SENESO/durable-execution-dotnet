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
/// A store under conformance test (§6.5): how to obtain stores over one
/// fresh history, and how to replace the history's raw content outright —
/// the move the corruption tests are built from. Creating a store
/// supersedes (disposes) the fixture's previous one, mirroring how the
/// tests use stores: strictly one live store at a time.
/// </summary>
public interface IHistoryStoreFixture : IDisposable
{
    /// <summary>A new store over this fixture's history, not yet loaded.</summary>
    IHistoryStore CreateStore();

    /// <summary>A new store over this fixture's history, loaded.</summary>
    IHistoryStore CreateLoadedStore();

    /// <summary>Replaces the whole history with the given sealed records, in order.</summary>
    void WriteRawRecords(params string[] records);
}

/// <summary>The file provider's fixture: the history is a fresh directory's log.</summary>
public sealed class FileHistoryStoreFixture : ISnapshotStoreFixture
{
    private readonly string _dir;
    private IHistoryStore? _current;

    public FileHistoryStoreFixture()
    {
        _dir = Path.Combine(Path.GetTempPath(), "p0-unit", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public IHistoryStore CreateStore()
    {
        (_current as IDisposable)?.Dispose();
        _current = new FileHistoryStore(_dir, snapshotInterval: 3);
        return _current;
    }

    public IHistoryStore CreateLoadedStore()
    {
        var store = CreateStore();
        store.Load();
        return store;
    }

    public void WriteRawRecords(params string[] records) =>
        File.WriteAllText(
            Path.Combine(_dir, "history.log"),
            records.Length == 0 ? "" : string.Join("\n", records) + "\n");

    public string SnapshotPath => Path.Combine(_dir, "history.snapshot.json");

    public void CorruptSnapshot()
    {
        string path = SnapshotPath;
        File.WriteAllText(path, File.ReadAllText(path).Replace("order/v1", "order/v9"));
    }

    public void Dispose() => (_current as IDisposable)?.Dispose();
}

/// <summary>
/// The SQL Server provider's fixture: the history is one instance's rows in
/// a throwaway database. Raw records are written with a plain connection —
/// the test acts as the disk, not as a store.
/// </summary>
public sealed class SqlHistoryStoreFixture : ISnapshotStoreFixture
{
    private const string Instance = "inst-1";

    private readonly (string Name, string Connection) _database = SqlTestDatabases.CreateDatabase("store");
    private IHistoryStore? _current;

    public IHistoryStore CreateStore()
    {
        (_current as IDisposable)?.Dispose();
        _current = new SqlHistoryStore(_database.Connection, Instance, snapshotInterval: 3);
        return _current;
    }

    public IHistoryStore CreateLoadedStore()
    {
        var store = CreateStore();
        store.Load();
        return store;
    }

    public void WriteRawRecords(params string[] records)
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
        for (int i = 0; i < records.Length; i++)
        {
            using var insert = new SqlCommand(
                "INSERT INTO dbo.HistoryEvents (InstanceId, N, Payload) VALUES (@instance, @n, @payload)", conn);
            insert.Parameters.Add(new SqlParameter("@instance", System.Data.SqlDbType.NVarChar, 128) { Value = Instance });
            insert.Parameters.Add(new SqlParameter("@n", System.Data.SqlDbType.BigInt) { Value = (long)(i + 1) });
            insert.Parameters.Add(new SqlParameter("@payload", System.Data.SqlDbType.NVarChar, -1) { Value = records[i] });
            insert.ExecuteNonQuery();
        }
    }

    public void CorruptSnapshot()
    {
        SqlTestDatabases.Execute(_database.Connection,
            "UPDATE dbo.HistorySnapshots SET Payload = REPLACE(Payload, 'order/v1', 'order/v9') WHERE InstanceId = 'inst-1'");
    }

    public void Dispose()
    {
        (_current as IDisposable)?.Dispose();
        try { SqlTestDatabases.DropDatabase(_database.Name); }
        catch (SqlException) { /* best-effort cleanup of a scratch database */ }
    }
}

/// <summary>
/// The §6.5 provider conformance contract for the history store, as tests:
/// append/load semantics, sequence assignment across store instances, and
/// the corruption rules — torn is not corrupt (framing differs per medium;
/// the torn-tail case itself is file framing and lives in
/// <see cref="HistoryStoreTests"/>), corrupt is never repaired silently.
/// Written once, run against every provider; a provider ships only when
/// its derived class runs green.
/// </summary>
public abstract class HistoryStoreConformanceTests(IHistoryStoreFixture fixture) : IDisposable
{
    protected IHistoryStoreFixture Fixture { get; } = fixture;

    public void Dispose() => Fixture.Dispose();

    /// <summary>Frames one raw record the way the store does (sequence + seal), for crafting histories by hand.</summary>
    protected static string Frame(JsonObject fields, long n)
    {
        fields["n"] = n;
        fields["sum"] = FileHistoryStore.Checksum(fields);
        return fields.ToJsonString();
    }

    protected static JsonObject RawEvent(string type, Action<JsonObject> fill)
    {
        var fields = new JsonObject
        {
            ["v"] = 1,
            ["type"] = type,
            ["instance"] = "inst-1",
            ["ts"] = DateTimeOffset.UtcNow.ToString("o"),
        };
        fill(fields);
        return fields;
    }

    protected static JsonObject StartedEvent() => RawEvent("WorkflowStarted", f =>
    {
        f["def"] = "order/v1";
        f["inputHash"] = new string('a', 64);
    });

    [Fact]
    public void Append_BeforeLoad_ThrowsInvalidOperation()
    {
        var store = Fixture.CreateStore();
        Assert.Throws<InvalidOperationException>(() =>
            store.Append(HistoryEvent.WorkflowStarted("inst-1", "def/v1",
                DurableExecution.Execution.Fingerprints.InputFingerprint(null))));
    }

    [Fact]
    public void AppendAndLoad_RoundtripPreservesEventsAndSequences()
    {
        var store = Fixture.CreateLoadedStore();
        store.Append(HistoryEvent.WorkflowStarted("inst-1", "order/v1", new string('a', 64)));
        store.Append(HistoryEvent.ActivityScheduled("inst-1", 1, "ChargeCard", new string('b', 64), new string('c', 64)));
        store.Append(HistoryEvent.ActivityAttemptStarted("inst-1", 1, 1));

        var reloaded = Fixture.CreateStore().Load();
        Assert.False(reloaded.TornTailTruncated);
        Assert.Equal(3, reloaded.Events.Count);
        Assert.Equal(new long[] { 1, 2, 3 }, reloaded.Events.Select(e => e.N).ToArray());
        Assert.Equal("ChargeCard", reloaded.Events[1].GetString("activity"));
    }

    [Fact]
    public void Load_NeverWritten_ReturnsEmptyHistory()
    {
        var result = Fixture.CreateStore().Load();
        Assert.Empty(result.Events);
        Assert.False(result.TornTailTruncated);
    }

    [Fact]
    public void Append_ContinuesSequenceAcrossStoreInstances()
    {
        Fixture.CreateLoadedStore().Append(
            HistoryEvent.WorkflowStarted("inst-1", "order/v1", new string('a', 64)));

        var second = Fixture.CreateLoadedStore();
        var appended = second.Append(HistoryEvent.WorkflowCompleted("inst-1", null));
        Assert.Equal(2, appended.N);
    }

    [Fact]
    public void Load_TerminatedInvalidJson_IsCorruption_NotTorn()
    {
        Fixture.WriteRawRecords(Frame(StartedEvent(), n: 1), "{this is not valid json");

        var ex = Assert.Throws<HistoryCorruptionException>(() => Fixture.CreateStore().Load());
        Assert.Contains("not valid JSON", ex.Message);
    }

    [Fact]
    public void Load_TamperedContent_IsCaughtByChecksum()
    {
        string sealedRecord = Frame(StartedEvent(), n: 1);
        // Still valid JSON, but the content no longer matches its seal.
        Fixture.WriteRawRecords(sealedRecord.Replace("order/v1", "order/v9"));

        var ex = Assert.Throws<HistoryCorruptionException>(() => Fixture.CreateStore().Load());
        Assert.Contains("checksum mismatch", ex.Message);
    }

    [Fact]
    public void Load_SequenceGap_IsCorruption()
    {
        string started = Frame(StartedEvent(), n: 1);
        string completed = Frame(RawEvent("WorkflowCompleted", _ => { }), n: 3); // n=2 was deleted
        Fixture.WriteRawRecords(started, completed);

        var ex = Assert.Throws<HistoryCorruptionException>(() => Fixture.CreateStore().Load());
        Assert.Contains("sequence gap", ex.Message);
    }

    [Fact]
    public void Load_DuplicateScheduled_IsCorruption()
    {
        JsonObject Scheduled() => RawEvent("ActivityScheduled", f =>
        {
            f["seq"] = 1;
            f["activity"] = "ChargeCard";
            f["idempotencyKey"] = new string('b', 64);
            f["inputHash"] = new string('c', 64);
        });
        Fixture.WriteRawRecords(Frame(StartedEvent(), n: 1), Frame(Scheduled(), n: 2), Frame(Scheduled(), n: 3));

        var ex = Assert.Throws<HistoryCorruptionException>(() => Fixture.CreateStore().Load());
        Assert.Contains("duplicate ActivityScheduled", ex.Message);
    }

    [Fact]
    public void Load_CompletionWithoutSchedule_IsCorruption()
    {
        string started = Frame(StartedEvent(), n: 1);
        string completed = Frame(RawEvent("ActivityCompleted", f =>
        {
            f["seq"] = 1;
            f["activity"] = "ChargeCard";
            f["result"] = "txn-1";
        }), n: 2);
        Fixture.WriteRawRecords(started, completed);

        var ex = Assert.Throws<HistoryCorruptionException>(() => Fixture.CreateStore().Load());
        Assert.Contains("without ActivityScheduled", ex.Message);
    }

    [Fact]
    public void Load_EventAfterWorkflowCompleted_IsCorruption()
    {
        string started = Frame(StartedEvent(), n: 1);
        string wfCompleted = Frame(RawEvent("WorkflowCompleted", _ => { }), n: 2);
        string attempt = Frame(RawEvent("ActivityAttemptStarted", f =>
        {
            f["seq"] = 1;
            f["attempt"] = 1;
        }), n: 3);
        Fixture.WriteRawRecords(started, wfCompleted, attempt);

        var ex = Assert.Throws<HistoryCorruptionException>(() => Fixture.CreateStore().Load());
        Assert.Contains("after WorkflowCompleted", ex.Message);
    }
}

/// <summary>The conformance contract against the file provider (the P0 evidence, unchanged).</summary>
[Trait("Provider", "File")]
public sealed class HistoryStoreConformanceTestsFile() : HistoryStoreConformanceTests(new FileHistoryStoreFixture());

/// <summary>The conformance contract against the SQL Server provider (§6.5).</summary>
[Trait("Provider", "SqlServer")]
public sealed class HistoryStoreConformanceTestsSqlServer() : HistoryStoreConformanceTests(new SqlHistoryStoreFixture());


/// <summary>
/// The PostgreSQL provider's fixture: the history is one instance's rows in
/// a throwaway database. Raw records are written with a plain connection.
/// </summary>
public sealed class PostgresHistoryStoreFixture : ISnapshotStoreFixture
{
    private const string Instance = "inst-1";

    private readonly (string Name, string Connection) _database = PostgresTestDatabases.CreateDatabase("store");
    private IHistoryStore? _current;

    public IHistoryStore CreateStore()
    {
        (_current as IDisposable)?.Dispose();
        _current = new PostgresHistoryStore(_database.Connection, Instance, snapshotInterval: 3);
        return _current;
    }

    public IHistoryStore CreateLoadedStore()
    {
        var store = CreateStore();
        store.Load();
        return store;
    }

    public void WriteRawRecords(params string[] records)
    {
        PostgresTestDatabases.EnsureTable(_database.Connection);
        PostgresTestDatabases.Execute(_database.Connection,
            "DELETE FROM public.history_events WHERE instance_id = 'inst-1'");
        for (int i = 0; i < records.Length; i++)
            PostgresTestDatabases.InsertPayload(_database.Connection, Instance, i + 1, records[i]);
    }

    public void CorruptSnapshot()
    {
        PostgresTestDatabases.Execute(_database.Connection,
            "UPDATE public.history_snapshots SET payload = REPLACE(payload, 'order/v1', 'order/v9') WHERE instance_id = 'inst-1'");
    }

    public void Dispose()
    {
        (_current as IDisposable)?.Dispose();
        try { PostgresTestDatabases.DropDatabase(_database.Name); }
        catch (NpgsqlException) { /* best-effort cleanup of a scratch database */ }
    }
}

/// <summary>
/// The SQLite provider's fixture: the history is a fresh directory's
/// database file. Raw records replace the table's rows directly.
/// </summary>
public sealed class SqliteHistoryStoreFixture : ISnapshotStoreFixture
{
    private readonly string _dir;
    private IHistoryStore? _current;

    public SqliteHistoryStoreFixture()
    {
        _dir = Path.Combine(Path.GetTempPath(), "p0-unit", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public IHistoryStore CreateStore()
    {
        (_current as IDisposable)?.Dispose();
        _current = new SqliteHistoryStore(_dir, snapshotInterval: 3);
        return _current;
    }

    public IHistoryStore CreateLoadedStore()
    {
        var store = CreateStore();
        store.Load();
        return store;
    }

    public void WriteRawRecords(params string[] records)
    {
        (_current as IDisposable)?.Dispose();
        _current = null;
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_dir, "history.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };
        using var conn = new SqliteConnection(builder.ConnectionString);
        conn.Open();
        using (var create = new SqliteCommand(
            "CREATE TABLE IF NOT EXISTS HistoryEvents (N INTEGER NOT NULL PRIMARY KEY, Payload TEXT NOT NULL);", conn))
            create.ExecuteNonQuery();
        using (var delete = new SqliteCommand("DELETE FROM HistoryEvents", conn))
            delete.ExecuteNonQuery();
        for (int i = 0; i < records.Length; i++)
        {
            using var insert = new SqliteCommand(
                "INSERT INTO HistoryEvents (N, Payload) VALUES (@n, @payload)", conn);
            insert.Parameters.Add(new SqliteParameter("@n", SqliteType.Integer) { Value = (long)(i + 1) });
            insert.Parameters.Add(new SqliteParameter("@payload", SqliteType.Text) { Value = records[i] });
            insert.ExecuteNonQuery();
        }
    }

    public void CorruptSnapshot()
    {
        (_current as IDisposable)?.Dispose();
        _current = null;
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_dir, "history.db"),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        };
        using var conn = new SqliteConnection(builder.ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(
            "UPDATE HistorySnapshots SET Payload = REPLACE(Payload, 'order/v1', 'order/v9') WHERE Id = 1", conn);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => (_current as IDisposable)?.Dispose();
}

/// <summary>The conformance contract against the PostgreSQL provider (§6.5).</summary>
[Trait("Provider", "PostgreSql")]
public sealed class HistoryStoreConformanceTestsPostgreSql() : HistoryStoreConformanceTests(new PostgresHistoryStoreFixture());

/// <summary>The conformance contract against the SQLite provider (§6.5).</summary>
[Trait("Provider", "Sqlite")]
public sealed class HistoryStoreConformanceTestsSqlite() : HistoryStoreConformanceTests(new SqliteHistoryStoreFixture());
