using System.Text.Json.Nodes;
using DurableExecution.History;
using DurableExecution.PostgreSql;
using DurableExecution.Sqlite;
using DurableExecution.SqlServer;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DurableExecution.Dashboard;

/// <summary>Where the dashboard reads histories from: one provider, configured by environment.</summary>
public sealed record DashboardConfig(string Provider, string StateRoot, string? SqlServerConnection, string? PostgresConnection)
{
    /// <summary>
    /// The deployment-shaped configuration: <c>DE_DASHBOARD_PROVIDER</c>
    /// (file | sqlite | sqlserver | postgresql; default file),
    /// <c>DE_DASHBOARD_STATE_ROOT</c> (the directory whose subdirectories
    /// are per-instance state directories, for the file-shaped providers),
    /// and the same connection variables the test suites use
    /// (<c>DE_SQLSERVER_CONNECTION</c> / <c>DE_POSTGRES_CONNECTION</c>).
    /// Payload encryption, when configured, arrives the usual way —
    /// <c>DE_HISTORY_ENCRYPTION_KEY</c> — and the dashboard's stores open
    /// histories with it; without it, encrypted histories refuse to load
    /// and show as refused, never as plaintext.
    /// </summary>
    public static DashboardConfig FromEnvironment() => new(
        Environment.GetEnvironmentVariable("DE_DASHBOARD_PROVIDER") ?? "file",
        Environment.GetEnvironmentVariable("DE_DASHBOARD_STATE_ROOT") ?? "./dashboard-state",
        Environment.GetEnvironmentVariable("DE_SQLSERVER_CONNECTION"),
        Environment.GetEnvironmentVariable("DE_POSTGRES_CONNECTION"));
}

/// <summary>One instance's row in the list: identity, status, and size — all read from its sealed history.</summary>
public sealed record InstanceSummary(string InstanceId, string Status, string? DefinitionId, int EventCount)
{
    /// <summary>Why the history could not be read, when <see cref="Status"/> is Corrupt or Unreadable.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// One instance's detail: the summary, its full event list when the
/// history validates, or the corruption message when it does not. There
/// is no third state — a history the store refuses is shown refused.
/// </summary>
public sealed record InstanceDetail(
    InstanceSummary Summary, IReadOnlyList<HistoryEvent>? Events, string? Corruption);

/// <summary>
/// The dashboard's data path. Every read goes through a real history
/// store's validating <c>Load()</c> — the same load a resuming host
/// performs — so what the operator sees is exactly what the engine
/// would accept, and a damaged history surfaces as the store's refusal,
/// never as a silently partial view. Every write (a signal) goes
/// through the same append path as any external sender: load, append
/// the <c>SignalReceived</c> record, done; delivery happens when the
/// workflow next waits or resumes, exactly as with the scenario host's
/// send-signal mode.
///
/// Instance discovery is provider-shaped: the file-shaped providers
/// scan the state root's subdirectories (one state directory per
/// instance; the instance's identity is read from its history, not
/// from the directory name); the database providers list the distinct
/// instance ids in the history table.
/// </summary>
public sealed class DashboardData(DashboardConfig config)
{
    private readonly HistoryEncryption? _encryption = HistoryEncryption.FromEnvironment();

    /// <summary>Every instance the provider knows, with its status read from its own history.</summary>
    public IReadOnlyList<InstanceSummary> ListInstances()
    {
        var summaries = new List<InstanceSummary>();
        foreach (string reference in ListReferences())
        {
            var store = OpenStore(reference);
            using var dispose = store as IDisposable;
            try
            {
                var load = store.Load();
                summaries.Add(Summarize(ActualInstanceId(reference, load.Events), load.Events));
            }
            catch (HistoryCorruptionException e)
            {
                summaries.Add(new InstanceSummary(reference, "Corrupt", null, 0) { Note = e.Message });
            }
            catch (Exception e) when (e is InvalidOperationException or IOException)
            {
                summaries.Add(new InstanceSummary(reference, "Unreadable", null, 0) { Note = e.Message });
            }
        }
        return summaries;
    }

    /// <summary>One instance's detail, or null when no such instance exists.</summary>
    public InstanceDetail? GetInstance(string instanceId)
    {
        string? reference = FindReference(instanceId);
        if (reference is null)
            return null;
        var store = OpenStore(reference);
        using var dispose = store as IDisposable;
        try
        {
            var load = store.Load();
            string actualId = ActualInstanceId(reference, load.Events);
            return new InstanceDetail(Summarize(actualId, load.Events), load.Events, null);
        }
        catch (HistoryCorruptionException e)
        {
            return new InstanceDetail(
                new InstanceSummary(instanceId, "Corrupt", null, 0), null, e.Message);
        }
    }

    /// <summary>
    /// Appends a signal to the instance's history through its store —
    /// the same append any external sender makes. The payload is taken
    /// as JSON when it parses, as a plain string otherwise.
    /// </summary>
    public void SendSignal(string instanceId, string name, string payloadText)
    {
        string reference = FindReference(instanceId)
            ?? throw new InvalidOperationException($"no such instance: '{instanceId}'");
        var store = OpenStore(reference);
        using var dispose = store as IDisposable;
        var load = store.Load();
        if (load.Events.Any(e => e.Type == HistoryEventTypes.WorkflowCompleted))
            throw new InvalidOperationException(
                $"instance '{instanceId}' is completed; a completed workflow cannot receive signals " +
                "(nothing may be appended after WorkflowCompleted — the load validator refuses it)");
        object? payload = null;
        if (!string.IsNullOrWhiteSpace(payloadText))
        {
            try { payload = JsonNode.Parse(payloadText); }
            catch { payload = payloadText; }
        }
        store.Append(HistoryEvent.SignalReceived(
            ActualInstanceId(reference, load.Events), name, payload));
    }

    private static string ActualInstanceId(string reference, IReadOnlyList<HistoryEvent> events) =>
        events.Count > 0 ? events[0].InstanceId : reference;

    private static InstanceSummary Summarize(string instanceId, IReadOnlyList<HistoryEvent> events)
    {
        var started = events.FirstOrDefault(e => e.Type == HistoryEventTypes.WorkflowStarted);
        string status = events.Any(e => e.Type == HistoryEventTypes.WorkflowCompleted) ? "Completed" : "Active";
        return new InstanceSummary(instanceId, status, started?.GetString("def"), events.Count);
    }

    // ---- provider plumbing ----

    private IReadOnlyList<string> ListReferences() => config.Provider switch
    {
        "sqlserver" => ListDatabaseInstances(sqlServer: true),
        "postgresql" => ListDatabaseInstances(sqlServer: false),
        _ => ListStateDirectories(),
    };

    private IReadOnlyList<string> ListStateDirectories()
    {
        if (!Directory.Exists(config.StateRoot))
            return Array.Empty<string>();
        string marker = config.Provider == "sqlite" ? "history.db" : "history.log";
        return Directory.GetDirectories(config.StateRoot)
            .Where(dir => File.Exists(Path.Combine(dir, marker)))
            .Select(dir => new DirectoryInfo(dir).Name)
            .OrderBy(name => name)
            .ToList();
    }

    private IReadOnlyList<string> ListDatabaseInstances(bool sqlServer)
    {
        var ids = new List<string>();
        try
        {
            if (sqlServer)
            {
                using var connection = new SqlConnection(config.SqlServerConnection);
                connection.Open();
                using var cmd = new SqlCommand(
                    "SELECT DISTINCT InstanceId FROM dbo.HistoryEvents ORDER BY InstanceId", connection);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    ids.Add(reader.GetString(0));
            }
            else
            {
                using var connection = new NpgsqlConnection(config.PostgresConnection);
                connection.Open();
                using var cmd = new NpgsqlCommand(
                    "SELECT DISTINCT instance_id FROM public.history_events ORDER BY instance_id", connection);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    ids.Add(reader.GetString(0));
            }
        }
        catch (Exception e) when (e is SqlException or NpgsqlException)
        {
            // No history table yet means no instances. Other database
            // errors are the operator's to see, so they propagate.
            if (e.Message.Contains("Invalid object name") || e.Message.Contains("does not exist"))
                return ids;
            throw;
        }
        return ids;
    }

    /// <summary>
    /// Maps a URL instance id to its provider reference. Database
    /// providers address instances by id directly; file-shaped
    /// providers scan their state directories and match the id recorded
    /// in each history (falling back to the directory name for an empty
    /// history).
    /// </summary>
    private string? FindReference(string instanceId)
    {
        if (config.Provider is "sqlserver" or "postgresql")
            return ListReferences().Contains(instanceId) ? instanceId : null;

        foreach (string reference in ListReferences())
        {
            if (reference == instanceId)
                return reference;
            try
            {
                var store = OpenStore(reference);
                using var dispose = store as IDisposable;
                var load = store.Load();
                if (load.Events.Count > 0 && load.Events[0].InstanceId == instanceId)
                    return reference;
            }
            catch (HistoryCorruptionException)
            {
                // A corrupt history cannot confirm its identity here; it
                // stays addressable by its directory name (handled above).
            }
        }
        return null;
    }

    private IHistoryStore OpenStore(string reference) => config.Provider switch
    {
        "sqlserver" => new SqlHistoryStore(config.SqlServerConnection!, reference, encryption: _encryption),
        "postgresql" => new PostgresHistoryStore(config.PostgresConnection!, reference, encryption: _encryption),
        "sqlite" => new SqliteHistoryStore(Path.Combine(config.StateRoot, reference), encryption: _encryption),
        _ => new FileHistoryStore(Path.Combine(config.StateRoot, reference), encryption: _encryption),
    };
}
