using System.Text.Json;
using System.Text.Json.Nodes;

namespace DurableExecution.History;

/// <summary>
/// Event type names of schema v1 (foundation §6.1, ADR-014). P0 emits the
/// subset its mechanisms produce; the remaining §6.1 types (timers, signals,
/// retries, sub-workflows, version markers) are reserved and land with the
/// phases that build those mechanisms. Events are never updated or deleted.
/// </summary>
public static class HistoryEventTypes
{
    public const string WorkflowStarted = "WorkflowStarted";
    public const string ActivityScheduled = "ActivityScheduled";
    public const string ActivityAttemptStarted = "ActivityAttemptStarted";
    public const string ActivityCompleted = "ActivityCompleted";
    public const string WorkflowCompleted = "WorkflowCompleted";

    internal static readonly HashSet<string> Known = new()
    {
        WorkflowStarted, ActivityScheduled, ActivityAttemptStarted,
        ActivityCompleted, WorkflowCompleted,
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

    public static HistoryEvent ActivityCompleted(string instanceId, long commandSequence, string activity, object? result) =>
        Create(HistoryEventTypes.ActivityCompleted, instanceId, f =>
        {
            f["seq"] = commandSequence;
            f["activity"] = activity;
            f["result"] = JsonSerializer.SerializeToNode(result);
        });

    public static HistoryEvent WorkflowCompleted(string instanceId, object? result) =>
        Create(HistoryEventTypes.WorkflowCompleted, instanceId, f =>
        {
            if (result is not null)
                f["result"] = JsonSerializer.SerializeToNode(result);
        });
}
