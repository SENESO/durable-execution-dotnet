using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace DurableExecution.Tests;

/// <summary>The deliberate damage the corruption scenarios (F1–F3) inflict on a completed history.</summary>
public enum HistoryDamage
{
    /// <summary>One committed record's content becomes invalid JSON (F1).</summary>
    InvalidJsonRecord,

    /// <summary>One committed record is removed, opening a sequence gap (F2).</summary>
    DeleteSecondRecord,

    /// <summary>A recorded result is rewritten in place, still valid JSON (F3).</summary>
    TamperRecordedResult,
}

/// <summary>
/// One persistence provider's side of the scenario suite (§6.5): provisioning
/// a scenario's state, the environment the scenario host runs under, reading
/// the recorded history back for assertions, cloning and damaging histories
/// for the corruption scenarios, and evidencing the discarded write of the
/// mid-write kill (scenario D). The scenario bodies are written once against
/// this surface and run per provider, so a green scenario claims its
/// behaviour for every provider that ran it — and only those.
/// </summary>
public abstract class ScenarioProvider : IDisposable
{
    /// <summary>The instance id every scenario runs under (the scenario host's default).</summary>
    protected const string ScenarioInstance = "order-1001";

    public abstract string FreshState(string scenario);

    public HostRun Run(
        string state,
        string crashMode = "",
        string gatewayMode = "idempotent",
        string variant = "default",
        IReadOnlyDictionary<string, string>? extraEnv = null,
        params string[] args) =>
        HostProcess.Run(state, crashMode, gatewayMode, variant, Merge(ProviderEnv(state), extraEnv), args);

    public Process Start(
        string state,
        string crashMode = "",
        string gatewayMode = "idempotent",
        string variant = "default",
        IReadOnlyDictionary<string, string>? extraEnv = null,
        params string[] args) =>
        HostProcess.Start(state, crashMode, gatewayMode, variant, Merge(ProviderEnv(state), extraEnv), args);

    /// <summary>Environment the provider adds to every scenario-host run over this state.</summary>
    protected abstract IReadOnlyDictionary<string, string>? ProviderEnv(string state);

    /// <summary>The recorded history as parsed events, in order.</summary>
    public abstract List<JsonElement> History(string state);

    /// <summary>The recorded history as one text (record per line), for content assertions.</summary>
    public abstract string HistoryText(string state);

    /// <summary>A copy of the state (auxiliary files and history) under a sibling directory.</summary>
    public abstract string CloneState(string state, string suffix);

    public abstract void DamageHistory(string state, HistoryDamage damage);

    /// <summary>
    /// Evidence, taken right after the mid-write kill of scenario D, that the
    /// interrupted append is not part of the committed history.
    /// </summary>
    public abstract void AssertUncommittedWriteDiscarded(string state);

    /// <summary>
    /// Evidence, taken after the resume run of scenario D, that the discard
    /// was surfaced the way this provider surfaces it — and that the history
    /// now loads whole.
    /// </summary>
    public abstract void AssertDiscardSurfaced(string state, HostRun resumeRun);

    public virtual void Dispose() { }

    private static IReadOnlyDictionary<string, string>? Merge(
        IReadOnlyDictionary<string, string>? providerEnv,
        IReadOnlyDictionary<string, string>? extraEnv)
    {
        if (providerEnv is null) return extraEnv;
        if (extraEnv is null) return providerEnv;
        var merged = new Dictionary<string, string>(providerEnv);
        foreach (var kv in extraEnv)
            merged[kv.Key] = kv.Value;
        return merged;
    }

    protected static string CopyStateDir(string state, string suffix)
    {
        string copy = state + suffix;
        Directory.CreateDirectory(copy);
        foreach (string file in Directory.GetFiles(state))
            File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
        return copy;
    }
}

/// <summary>The file provider's scenario side: history is the state's <c>history.log</c>.</summary>
public sealed class FileScenarioProvider : ScenarioProvider
{
    public override string FreshState(string scenario) => HostProcess.FreshState(scenario);

    protected override IReadOnlyDictionary<string, string>? ProviderEnv(string state) => null;

    public override List<JsonElement> History(string state) => StateAssert.History(state);

    public override string HistoryText(string state) =>
        File.ReadAllText(Path.Combine(state, "history.log"));

    public override string CloneState(string state, string suffix) => CopyStateDir(state, suffix);

    public override void DamageHistory(string state, HistoryDamage damage)
    {
        string path = Path.Combine(state, "history.log");
        switch (damage)
        {
            case HistoryDamage.InvalidJsonRecord:
                RewriteLines(state, lines => { lines[2] = "{this is not valid json"; return lines; });
                break;
            case HistoryDamage.DeleteSecondRecord:
                RewriteLines(state, lines => { lines.RemoveAt(1); return lines; });
                break;
            case HistoryDamage.TamperRecordedResult:
                File.WriteAllText(path,
                    File.ReadAllText(path).Replace("\"result\":\"reserved\"", "\"result\":\"tampered\""));
                break;
        }
    }

    public override void AssertUncommittedWriteDiscarded(string state)
    {
        // The torn record is physically there: unterminated bytes past the
        // last committed record — present on disk, absent from the history.
        byte[] bytes = File.ReadAllBytes(Path.Combine(state, "history.log"));
        Assert.True(bytes.Length > 0 && bytes[^1] != (byte)'\n',
            "torn tail: the interrupted write left unterminated bytes on disk");
    }

    public override void AssertDiscardSurfaced(string state, HostRun resumeRun)
    {
        Assert.Contains("torn tail", resumeRun.Output); // unterminated != committed, and it is reported
        _ = History(state); // fully parseable after torn-tail repair
    }

    private static void RewriteLines(string state, Func<List<string>, List<string>> edit)
    {
        string path = Path.Combine(state, "history.log");
        var lines = File.ReadAllLines(path).ToList();
        File.WriteAllText(path, string.Join("\n", edit(lines)) + "\n");
    }
}

/// <summary>
/// The SQL Server provider's scenario side: each scenario gets a scratch
/// database (history inside it), while the state directory keeps the
/// auxiliary state (counters, gateway, host lock) exactly as the file runs.
/// </summary>
public sealed class SqlScenarioProvider : ScenarioProvider
{
    private readonly Dictionary<string, (string Name, string Connection)> _databases = new();

    public override string FreshState(string scenario)
    {
        string state = HostProcess.FreshState(scenario);
        _databases[state] = SqlTestDatabases.CreateDatabase(scenario);
        return state;
    }

    protected override IReadOnlyDictionary<string, string>? ProviderEnv(string state) =>
        new Dictionary<string, string>
        {
            ["HISTORY_PROVIDER"] = "sqlserver",
            ["HISTORY_SQLSERVER_CONNECTION"] = _databases[state].Connection,
        };

    public override List<JsonElement> History(string state) =>
        SqlTestDatabases.ReadPayloads(_databases[state].Connection, ScenarioInstance)
            .Select(p => JsonDocument.Parse(p).RootElement.Clone())
            .ToList();

    public override string HistoryText(string state) =>
        string.Join("\n", SqlTestDatabases.ReadPayloads(_databases[state].Connection, ScenarioInstance));

    public override string CloneState(string state, string suffix)
    {
        string copy = CopyStateDir(state, suffix);
        var source = _databases[state];
        var clone = SqlTestDatabases.CreateDatabase("clone");
        _databases[copy] = clone;
        // The clone's history starts empty; create the table, then copy rows.
        SqlTestDatabases.Execute(clone.Connection,
            "IF OBJECT_ID(N'dbo.HistoryEvents', N'U') IS NULL " +
            "CREATE TABLE dbo.HistoryEvents (InstanceId NVARCHAR(128) NOT NULL, N BIGINT NOT NULL, " +
            "Payload NVARCHAR(MAX) NOT NULL, CONSTRAINT PK_HistoryEvents PRIMARY KEY (InstanceId, N));");
        SqlTestDatabases.Execute(SqlTestDatabases.MasterConnection,
            $"INSERT INTO [{clone.Name}].dbo.HistoryEvents SELECT * FROM [{source.Name}].dbo.HistoryEvents;");
        return copy;
    }

    public override void DamageHistory(string state, HistoryDamage damage)
    {
        string connection = _databases[state].Connection;
        switch (damage)
        {
            case HistoryDamage.InvalidJsonRecord:
                SqlTestDatabases.Execute(connection,
                    "UPDATE dbo.HistoryEvents SET Payload = '{this is not valid json' WHERE N = 3");
                break;
            case HistoryDamage.DeleteSecondRecord:
                SqlTestDatabases.Execute(connection,
                    "DELETE FROM dbo.HistoryEvents WHERE N = 2");
                break;
            case HistoryDamage.TamperRecordedResult:
                SqlTestDatabases.Execute(connection,
                    "UPDATE dbo.HistoryEvents SET Payload = " +
                    "REPLACE(Payload, '\"result\":\"reserved\"', '\"result\":\"tampered\"')");
                break;
        }
    }

    public override void AssertUncommittedWriteDiscarded(string state)
    {
        // The append died inside its transaction; the server rolled it back.
        // No completion row for command 1 exists — there is nothing to repair.
        var completions = History(state).Where(e =>
            e.GetProperty("type").GetString() == "ActivityCompleted" &&
            e.GetProperty("seq").GetInt64() == 1);
        Assert.Empty(completions);
    }

    public override void AssertDiscardSurfaced(string state, HostRun resumeRun)
    {
        // A transactional provider has no torn tail to report — and must not
        // claim one. The history loads whole, with the re-executed completion
        // committed as a normal record.
        Assert.DoesNotContain("torn tail", resumeRun.Output);
        _ = History(state);
    }

    public override void Dispose()
    {
        foreach (var db in _databases.Values)
        {
            try { SqlTestDatabases.DropDatabase(db.Name); }
            catch (SqlException) { /* best-effort cleanup of a scratch database */ }
        }
        _databases.Clear();
    }
}


/// <summary>
/// The PostgreSQL provider's scenario side: each scenario gets a scratch
/// database (history inside it); the state directory keeps the auxiliary
/// state exactly as the other database runs.
/// </summary>
public sealed class PostgresScenarioProvider : ScenarioProvider
{
    private readonly Dictionary<string, (string Name, string Connection)> _databases = new();

    public override string FreshState(string scenario)
    {
        string state = HostProcess.FreshState(scenario);
        _databases[state] = PostgresTestDatabases.CreateDatabase(scenario);
        return state;
    }

    protected override IReadOnlyDictionary<string, string>? ProviderEnv(string state) =>
        new Dictionary<string, string>
        {
            ["HISTORY_PROVIDER"] = "postgresql",
            ["HISTORY_POSTGRES_CONNECTION"] = _databases[state].Connection,
        };

    public override List<JsonElement> History(string state) =>
        PostgresTestDatabases.ReadPayloads(_databases[state].Connection, ScenarioInstance)
            .Select(p => JsonDocument.Parse(p).RootElement.Clone())
            .ToList();

    public override string HistoryText(string state) =>
        string.Join("\n", PostgresTestDatabases.ReadPayloads(_databases[state].Connection, ScenarioInstance));

    public override string CloneState(string state, string suffix)
    {
        string copy = CopyStateDir(state, suffix);
        var clone = PostgresTestDatabases.CreateDatabase("clone");
        _databases[copy] = clone;
        PostgresTestDatabases.EnsureTable(clone.Connection);
        var payloads = PostgresTestDatabases.ReadPayloads(_databases[state].Connection, ScenarioInstance);
        for (int i = 0; i < payloads.Count; i++)
            PostgresTestDatabases.InsertPayload(clone.Connection, ScenarioInstance, i + 1, payloads[i]);
        return copy;
    }

    public override void DamageHistory(string state, HistoryDamage damage)
    {
        string connection = _databases[state].Connection;
        switch (damage)
        {
            case HistoryDamage.InvalidJsonRecord:
                PostgresTestDatabases.Execute(connection,
                    "UPDATE public.history_events SET payload = '{this is not valid json' WHERE n = 3");
                break;
            case HistoryDamage.DeleteSecondRecord:
                PostgresTestDatabases.Execute(connection,
                    "DELETE FROM public.history_events WHERE n = 2");
                break;
            case HistoryDamage.TamperRecordedResult:
                PostgresTestDatabases.Execute(connection,
                    "UPDATE public.history_events SET payload = " +
                    "REPLACE(payload, '\"result\":\"reserved\"', '\"result\":\"tampered\"')");
                break;
        }
    }

    public override void AssertUncommittedWriteDiscarded(string state)
    {
        var completions = History(state).Where(e =>
            e.GetProperty("type").GetString() == "ActivityCompleted" &&
            e.GetProperty("seq").GetInt64() == 1);
        Assert.Empty(completions);
    }

    public override void AssertDiscardSurfaced(string state, HostRun resumeRun)
    {
        Assert.DoesNotContain("torn tail", resumeRun.Output);
        _ = History(state);
    }

    public override void Dispose()
    {
        foreach (var db in _databases.Values)
        {
            try { PostgresTestDatabases.DropDatabase(db.Name); }
            catch (NpgsqlException) { /* best-effort cleanup of a scratch database */ }
        }
        _databases.Clear();
    }
}

/// <summary>
/// The SQLite provider's scenario side: the history is the database file in
/// the scenario's state directory; everything else is the file layout.
/// </summary>
public sealed class SqliteScenarioProvider : ScenarioProvider
{
    public override string FreshState(string scenario) => HostProcess.FreshState(scenario);

    protected override IReadOnlyDictionary<string, string>? ProviderEnv(string state) =>
        new Dictionary<string, string> { ["HISTORY_PROVIDER"] = "sqlite" };

    private static string DatabasePath(string state) => Path.Combine(state, "history.db");

    private static List<string> ReadPayloads(string state)
    {
        var payloads = new List<string>();
        string path = DatabasePath(state);
        if (!File.Exists(path))
            return payloads;
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        using var conn = new SqliteConnection(builder.ConnectionString);
        conn.Open();
        using var exists = new SqliteCommand(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'HistoryEvents'", conn);
        if ((long)exists.ExecuteScalar()! == 0)
            return payloads;
        using var cmd = new SqliteCommand("SELECT Payload FROM HistoryEvents ORDER BY N", conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            payloads.Add(reader.GetString(0));
        return payloads;
    }

    private static void Execute(string state, string sql)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath(state),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        };
        using var conn = new SqliteConnection(builder.ConnectionString);
        conn.Open();
        using var cmd = new SqliteCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }

    public override List<JsonElement> History(string state) =>
        ReadPayloads(state).Select(p => JsonDocument.Parse(p).RootElement.Clone()).ToList();

    public override string HistoryText(string state) => string.Join("\n", ReadPayloads(state));

    public override string CloneState(string state, string suffix) => CopyStateDir(state, suffix);

    public override void DamageHistory(string state, HistoryDamage damage)
    {
        switch (damage)
        {
            case HistoryDamage.InvalidJsonRecord:
                Execute(state, "UPDATE HistoryEvents SET Payload = '{this is not valid json' WHERE N = 3");
                break;
            case HistoryDamage.DeleteSecondRecord:
                Execute(state, "DELETE FROM HistoryEvents WHERE N = 2");
                break;
            case HistoryDamage.TamperRecordedResult:
                Execute(state,
                    "UPDATE HistoryEvents SET Payload = REPLACE(Payload, '\"result\":\"reserved\"', '\"result\":\"tampered\"')");
                break;
        }
    }

    public override void AssertUncommittedWriteDiscarded(string state)
    {
        var completions = History(state).Where(e =>
            e.GetProperty("type").GetString() == "ActivityCompleted" &&
            e.GetProperty("seq").GetInt64() == 1);
        Assert.Empty(completions);
    }

    public override void AssertDiscardSurfaced(string state, HostRun resumeRun)
    {
        Assert.DoesNotContain("torn tail", resumeRun.Output);
        _ = History(state);
    }
}
