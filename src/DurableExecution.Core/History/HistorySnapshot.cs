using System.Text.Json;
using System.Text.Json.Nodes;

namespace DurableExecution.History;

/// <summary>
/// A sealed snapshot of one workflow instance's replay state at a recorded
/// point in its history (P1 snapshots). The snapshot is not a second history:
/// it is a checkpoint derived from the sealed records up to
/// <see cref="ThroughN"/>. It stores the workflow's start identity, every
/// non-attempt replay fact in order, and the attempt count of each command.
/// Resuming from a snapshot reconstructs the same logical state as replaying
/// those records, then validates and applies only the tail after
/// <see cref="ThroughN"/>.
///
/// Seal semantics are the history's own: the snapshot payload carries a
/// checksum over its canonical content. A snapshot that does not parse, does
/// not match its seal, or is structurally inconsistent is corruption and is
/// refused loudly with <see cref="HistoryCorruptionException"/> — exactly as
/// a damaged history record is refused. There is deliberately no silent
/// fallback to full replay: a torn or tampered checkpoint is evidence about
/// the state of storage, and hiding it would weaken the sealing model.
/// </summary>
public sealed class HistorySnapshot
{
    internal HistorySnapshot(JsonObject fields) => Fields = fields;

    internal JsonObject Fields { get; }

    public string InstanceId => Fields["instance"]!.GetValue<string>();

    /// <summary>The event sequence this snapshot's state is complete through.</summary>
    public long ThroughN => Fields["throughN"]!.GetValue<long>();

    public string DefinitionId => Fields["def"]!.GetValue<string>();

    public string InputFingerprint => Fields["inputHash"]!.GetValue<string>();

    internal JsonArray Facts => (JsonArray)Fields["facts"]!.DeepClone();

    internal JsonObject AttemptCounts => (JsonObject)Fields["attempts"]!.DeepClone();
}

/// <summary>The outcome of a snapshot-aware load.</summary>
public sealed record HistorySnapshotLoadResult(
    HistoryLoadResult Load,
    bool SnapshotUsed,
    long SnapshotThroughN);

/// <summary>
/// Optional snapshot capability of a history store. Providers that implement
/// it periodically persist a sealed <see cref="HistorySnapshot"/> as appends
/// cross their configured interval, and can load by validating a snapshot
/// plus only the history tail after it. Providers that do not implement it
/// are unaffected; the host falls back to a full <see cref="IHistoryStore.Load"/>.
/// </summary>
public interface IHistorySnapshotStore
{
    /// <summary>
    /// Loads the instance's logical history from its latest snapshot plus
    /// the tail after the snapshot point. With no snapshot, this is a full
    /// load and <see cref="HistorySnapshotLoadResult.SnapshotUsed"/> is
    /// false. A corrupt snapshot throws
    /// <see cref="HistoryCorruptionException"/>.
    /// </summary>
    HistorySnapshotLoadResult LoadFromSnapshot();
}

/// <summary>
/// Builds, seals, parses, and reconstructs snapshot state. The codec is a
/// property of the record model, not of a storage medium — every snapshot
/// provider persists the identical payload this codec produces, just as
/// every history provider persists the identical sealed history record.
/// </summary>
internal static class HistorySnapshotCodec
{
    internal static HistorySnapshotBuilder Builder() => new();

    internal static HistorySnapshotBuilder BuilderFrom(HistorySnapshot snapshot) =>
        HistorySnapshotBuilder.FromSnapshot(snapshot);

    internal static string Serialize(HistorySnapshot snapshot) => snapshot.Fields.ToJsonString();

    internal static HistorySnapshot Parse(string payload, string? expectedInstanceId = null)
    {
        JsonObject fields;
        try
        {
            fields = JsonNode.Parse(payload)?.AsObject()
                ?? throw new JsonException("snapshot is not a JSON object");
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            throw new HistoryCorruptionException($"snapshot: not valid JSON ({e.Message})");
        }

        string ctx = "snapshot";
        if (!fields.TryGetPropertyValue("sum", out var sumNode) ||
            sumNode?.GetValue<string>() != HistorySealing.Checksum(fields))
            throw new HistoryCorruptionException($"{ctx}: checksum mismatch (content tampered or partially written)");
        if (ReqLong(fields, "v", ctx) != 1)
            throw new HistoryCorruptionException($"{ctx}: unsupported schema version");
        if (ReqString(fields, "type", ctx) != "WorkflowSnapshot")
            throw new HistoryCorruptionException($"{ctx}: not a workflow snapshot");
        string instance = ReqString(fields, "instance", ctx);
        if (expectedInstanceId is not null && instance != expectedInstanceId)
            throw new HistoryCorruptionException(
                $"{ctx}: snapshot belongs to instance '{instance}', expected '{expectedInstanceId}'");
        long throughN = ReqLong(fields, "throughN", ctx);
        if (throughN < 1)
            throw new HistoryCorruptionException($"{ctx}: throughN must be >= 1");
        ReqString(fields, "def", ctx);
        if (!Execution.Fingerprints.IsHex64(ReqString(fields, "inputHash", ctx)))
            throw new HistoryCorruptionException($"{ctx}: inputHash malformed");
        if (!fields.TryGetPropertyValue("facts", out var factsNode) || factsNode is not JsonArray facts)
            throw new HistoryCorruptionException($"{ctx}: facts missing or not an array");
        if (!fields.TryGetPropertyValue("attempts", out var attemptsNode) || attemptsNode is not JsonObject attempts)
            throw new HistoryCorruptionException($"{ctx}: attempts missing or not an object");

        foreach (var node in facts)
        {
            if (node is not JsonObject fact)
                throw new HistoryCorruptionException($"{ctx}: fact is not a JSON object");
            string type = ReqString(fact, "type", ctx);
            if (!HistoryEventTypes.Known.Contains(type))
                throw new HistoryCorruptionException($"{ctx}: unknown fact event type '{type}'");
            if (type is HistoryEventTypes.WorkflowStarted or HistoryEventTypes.ActivityAttemptStarted)
                throw new HistoryCorruptionException($"{ctx}: fact type '{type}' is represented outside the facts list");
        }
        foreach (var kv in attempts)
        {
            if (!long.TryParse(kv.Key, out long seq) || seq < 1 ||
                kv.Value is null || kv.Value.GetValueKind() != JsonValueKind.Number ||
                !kv.Value.AsValue().TryGetValue<long>(out long count) || count < 1)
                throw new HistoryCorruptionException($"{ctx}: attempt count for command '{kv.Key}' malformed");
        }

        return new HistorySnapshot(fields);
    }

    private static long ReqLong(JsonObject fields, string field, string ctx)
    {
        if (!fields.TryGetPropertyValue(field, out var v) || v is null ||
            v.GetValueKind() != JsonValueKind.Number || !v.AsValue().TryGetValue<long>(out long x))
            throw new HistoryCorruptionException($"{ctx}: field '{field}' missing or not an integer");
        return x;
    }

    private static string ReqString(JsonObject fields, string field, string ctx)
    {
        if (!fields.TryGetPropertyValue(field, out var v) || v is null ||
            v.GetValueKind() != JsonValueKind.String || string.IsNullOrEmpty(v.GetValue<string>()))
            throw new HistoryCorruptionException($"{ctx}: field '{field}' missing or not a non-empty string");
        return v.GetValue<string>()!;
    }
}

/// <summary>
/// Accumulates snapshot state from validated history events. A store feeds
/// the builder every event it loads and every event it appends; at an
/// interval boundary it seals the builder's state as the latest snapshot.
/// </summary>
internal sealed class HistorySnapshotBuilder
{
    private string? _instanceId;
    private string? _definitionId;
    private string? _inputFingerprint;
    private string? _startedTimestamp;
    private readonly List<JsonObject> _facts = new();
    private readonly Dictionary<long, long> _attemptCounts = new();
    private long _throughN;

    internal static HistorySnapshotBuilder FromSnapshot(HistorySnapshot snapshot)
    {
        var builder = new HistorySnapshotBuilder
        {
            _instanceId = snapshot.InstanceId,
            _definitionId = snapshot.DefinitionId,
            _inputFingerprint = snapshot.InputFingerprint,
            _throughN = snapshot.ThroughN,
        };
        foreach (var node in snapshot.Facts)
            builder._facts.Add((JsonObject)node!.DeepClone());
        foreach (var kv in snapshot.AttemptCounts)
            builder._attemptCounts[long.Parse(kv.Key)] = kv.Value!.GetValue<long>();
        return builder;
    }

    internal void Apply(HistoryEvent historyEvent)
    {
        var fields = historyEvent.Fields;
        switch (historyEvent.Type)
        {
            case HistoryEventTypes.WorkflowStarted:
                _instanceId = historyEvent.InstanceId;
                _definitionId = historyEvent.GetString("def");
                _inputFingerprint = historyEvent.GetString("inputHash");
                _startedTimestamp = historyEvent.GetString("ts");
                break;
            case HistoryEventTypes.ActivityAttemptStarted:
                long seq = historyEvent.CommandSequence!.Value;
                _attemptCounts[seq] = _attemptCounts.GetValueOrDefault(seq) + 1;
                break;
            default:
                var fact = new JsonObject();
                foreach (var kv in fields)
                    if (kv.Key is not "n" and not "sum")
                        fact[kv.Key] = kv.Value?.DeepClone();
                _facts.Add(fact);
                break;
        }
        _throughN = historyEvent.N;
    }

    internal bool CanSnapshot => _definitionId is not null && _throughN >= 1;

    internal HistorySnapshot Build()
    {
        if (!CanSnapshot)
            throw new InvalidOperationException("cannot snapshot before WorkflowStarted has been applied");
        var attempts = new JsonObject();
        foreach (var kv in _attemptCounts.OrderBy(kv => kv.Key))
            attempts[kv.Key.ToString()] = kv.Value;
        var facts = new JsonArray();
        foreach (var fact in _facts)
            facts.Add(fact.DeepClone());
        var fields = new JsonObject
        {
            ["v"] = 1,
            ["type"] = "WorkflowSnapshot",
            ["instance"] = _instanceId,
            ["throughN"] = _throughN,
            ["def"] = _definitionId,
            ["inputHash"] = _inputFingerprint,
            ["facts"] = facts,
            ["attempts"] = attempts,
        };
        fields["sum"] = HistorySealing.Checksum(fields);
        return new HistorySnapshot(fields);
    }

    /// <summary>
    /// Reconstructs the logical event list a snapshot stands for: the start
    /// event, the recorded facts in order (with each command's aggregated
    /// attempt events restored beside its schedule), so a consumer written
    /// against history events sees the same state full replay would give.
    /// </summary>
    internal List<HistoryEvent> ToEvents()
    {
        var events = new List<HistoryEvent>();
        var started = new JsonObject
        {
            ["v"] = 1,
            ["type"] = HistoryEventTypes.WorkflowStarted,
            ["instance"] = _instanceId,
            ["ts"] = _startedTimestamp ?? DateTimeOffset.UtcNow.ToString("o"),
            ["def"] = _definitionId,
            ["inputHash"] = _inputFingerprint,
        };
        events.Add(new HistoryEvent(started));

        var emittedAttempts = new HashSet<long>();
        void EmitAttempts(long seq)
        {
            if (!emittedAttempts.Add(seq))
                return;
            if (!_attemptCounts.TryGetValue(seq, out long count))
                return;
            for (long attempt = 1; attempt <= count; attempt++)
            {
                var attemptFields = new JsonObject
                {
                    ["v"] = 1,
                    ["type"] = HistoryEventTypes.ActivityAttemptStarted,
                    ["instance"] = _instanceId,
                    ["ts"] = _startedTimestamp ?? DateTimeOffset.UtcNow.ToString("o"),
                    ["seq"] = seq,
                    ["attempt"] = attempt,
                };
                events.Add(new HistoryEvent(attemptFields));
            }
        }

        JsonObject? completion = null;
        foreach (var fact in _facts)
        {
            string type = fact["type"]!.GetValue<string>();
            if (type == HistoryEventTypes.WorkflowCompleted)
            {
                completion = fact;
                continue;
            }
            if (fact.TryGetPropertyValue("seq", out var seqNode) && seqNode is not null &&
                seqNode.GetValueKind() == JsonValueKind.Number)
            {
                long seq = seqNode.GetValue<long>();
                if (type != HistoryEventTypes.ActivityScheduled)
                    EmitAttempts(seq);
                events.Add(new HistoryEvent((JsonObject)fact.DeepClone()));
                if (type == HistoryEventTypes.ActivityScheduled)
                    EmitAttempts(seq);
            }
            else
            {
                events.Add(new HistoryEvent((JsonObject)fact.DeepClone()));
            }
        }
        foreach (long seq in _attemptCounts.Keys.OrderBy(k => k))
            EmitAttempts(seq);
        if (completion is not null)
            events.Add(new HistoryEvent((JsonObject)completion.DeepClone()));
        return events;
    }
}
