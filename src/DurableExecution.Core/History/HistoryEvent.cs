using System.Text.Json;
using System.Text.Json.Nodes;

namespace DurableExecution.History;

/// <summary>
/// Event type names of schema v1 (foundation §6.1, ADR-014). Each type is
/// emitted by the phase that builds its mechanism (timers and signals in P2,
/// version markers with the P4 version gates); sub-workflow types remain
/// reserved. Events are never updated or deleted.
/// </summary>
public static class HistoryEventTypes
{
    public const string WorkflowStarted = "WorkflowStarted";
    public const string ActivityScheduled = "ActivityScheduled";
    public const string ActivityAttemptStarted = "ActivityAttemptStarted";
    public const string ActivityAttemptFailed = "ActivityAttemptFailed";
    public const string TimerScheduled = "TimerScheduled";
    public const string TimerFired = "TimerFired";
    public const string SignalReceived = "SignalReceived";
    public const string ActivityCompleted = "ActivityCompleted";
    public const string WorkflowCompleted = "WorkflowCompleted";
    public const string VersionMarker = "VersionMarker";

    internal static readonly HashSet<string> Known = new()
    {
        WorkflowStarted, ActivityScheduled, ActivityAttemptStarted,
        ActivityAttemptFailed, ActivityCompleted, WorkflowCompleted,
        TimerScheduled, TimerFired, SignalReceived, VersionMarker,
    };
}

/// <summary>
/// One immutable record in an instance's append-only history. Every event
/// carries the schema version, the instance id, a monotonic per-instance event
/// sequence (<see cref="N"/>), a store timestamp, and — once framed — a SHA-256
/// checksum over its canonical content (§6.1). Command-scoped events also carry
/// the command sequence; together with namespace and instance it forms the
/// <c>DurableCommandId</c> of ADR-012.
/// </summary>
public sealed class HistoryEvent
{
    internal HistoryEvent(JsonObject fields) => Fields = fields;

    /// <summary>The event's fields as a JSON object (the framed form, minus the checksum).</summary>
    public JsonObject Fields { get; }

    public string Type => Fields["type"]!.GetValue<string>();

    /// <summary>Monotonic per-instance event sequence, assigned by the store at append time.</summary>
    public long N => Fields["n"]!.GetValue<long>();

    public string InstanceId => Fields["instance"]!.GetValue<string>();

    /// <summary>Command sequence for command-scoped events, otherwise null.</summary>
    public long? CommandSequence =>
        Fields.TryGetPropertyValue("seq", out var node) ? node!.GetValue<long>() : null;

    public string? GetString(string field) =>
        Fields.TryGetPropertyValue(field, out var node) ? node?.GetValue<string>() : null;

    public long? GetInt64(string field) =>
        Fields.TryGetPropertyValue(field, out var node) ? node?.GetValue<long>() : null;

    public JsonNode? GetNode(string field) =>
        Fields.TryGetPropertyValue(field, out var node) ? node : null;

    public T? GetResult<T>() =>
        Fields.TryGetPropertyValue("result", out var node) && node is not null
            ? JsonSerializer.Deserialize<T>(node.ToJsonString())
            : default;

    internal static HistoryEvent Create(string type, string instanceId, Action<JsonObject> fill)
    {
        var fields = new JsonObject
        {
            ["v"] = 1,
            ["type"] = type,
            ["instance"] = instanceId,
            ["ts"] = DateTimeOffset.UtcNow.ToString("o"),
        };
        fill(fields);
        return new HistoryEvent(fields);
    }

    public static HistoryEvent WorkflowStarted(string instanceId, string definitionId, string inputFingerprint) =>
        Create(HistoryEventTypes.WorkflowStarted, instanceId, f =>
        {
            f["def"] = definitionId;
            f["inputHash"] = inputFingerprint;
        });

    public static HistoryEvent ActivityScheduled(string instanceId, long commandSequence, string activity, string idempotencyKey, string inputFingerprint) =>
        Create(HistoryEventTypes.ActivityScheduled, instanceId, f =>
        {
            f["seq"] = commandSequence;
            f["activity"] = activity;
            f["idempotencyKey"] = idempotencyKey;
            f["inputHash"] = inputFingerprint;
        });

    public static HistoryEvent ActivityAttemptStarted(string instanceId, long commandSequence, long attempt) =>
        Create(HistoryEventTypes.ActivityAttemptStarted, instanceId, f =>
        {
            f["seq"] = commandSequence;
            f["attempt"] = attempt;
        });

    public static HistoryEvent ActivityAttemptFailed(string instanceId, long commandSequence, long attempt, string error) =>
        Create(HistoryEventTypes.ActivityAttemptFailed, instanceId, f =>
        {
            f["seq"] = commandSequence;
            f["attempt"] = attempt;
            f["error"] = error;
        });

    public static HistoryEvent ActivityCompleted(string instanceId, long commandSequence, string activity, object? result) =>
        Create(HistoryEventTypes.ActivityCompleted, instanceId, f =>
        {
            f["seq"] = commandSequence;
            f["activity"] = activity;
            f["result"] = JsonSerializer.SerializeToNode(result);
        });

    public static HistoryEvent TimerScheduled(string instanceId, long commandSequence, DateTimeOffset dueAt, long durationMs) =>
        Create(HistoryEventTypes.TimerScheduled, instanceId, f =>
        {
            f["seq"] = commandSequence;
            f["dueAt"] = dueAt.ToString("o");
            f["durationMs"] = durationMs;
        });

    public static HistoryEvent TimerFired(string instanceId, long commandSequence) =>
        Create(HistoryEventTypes.TimerFired, instanceId, f =>
        {
            f["seq"] = commandSequence;
        });

    public static HistoryEvent SignalReceived(string instanceId, string name, object? payload) =>
        Create(HistoryEventTypes.SignalReceived, instanceId, f =>
        {
            f["name"] = name;
            f["result"] = System.Text.Json.JsonSerializer.SerializeToNode(payload);
        });

    public static HistoryEvent WorkflowCompleted(string instanceId, object? result) =>
        Create(HistoryEventTypes.WorkflowCompleted, instanceId, f =>
        {
            if (result is not null)
                f["result"] = JsonSerializer.SerializeToNode(result);
        });

    /// <summary>
    /// A code-version gate decision (P4): the first live pass of a
    /// <c>GetVersion</c> call records which version of a named change the
    /// workflow took, so every later replay takes the same branch. Markers
    /// carry no command sequence — they are consumed in their own recorded
    /// order, matched by change id.
    /// </summary>
    public static HistoryEvent VersionMarker(string instanceId, string changeId, long version) =>
        Create(HistoryEventTypes.VersionMarker, instanceId, f =>
        {
            f["changeId"] = changeId;
            f["version"] = version;
        });
}
