using System.Text.Json;
using System.Text.Json.Nodes;
using DurableExecution.Execution;

namespace DurableExecution.History;

/// <summary>
/// Validates a sequence of sealed history records, one at a time, in order —
/// the structural rules of §6.1/ADR-014 that every provider enforces on load:
/// event types are a closed set with required typed fields; the event
/// sequence is gapless; <c>WorkflowStarted</c> is the unique first event; a
/// command is scheduled exactly once; attempt numbers are sequential per
/// command; a completion must match its scheduled command's activity;
/// <c>WorkflowCompleted</c> is unique and last. A record that fails parsing,
/// checksum, schema, or structure is corruption and is refused with
/// <see cref="HistoryCorruptionException"/> — never deleted, never skipped.
///
/// This validator is deliberately medium-agnostic: the file provider feeds
/// it newline-framed lines, a database provider feeds it row payloads, and
/// both refuse the same damage with the same words.
/// </summary>
internal sealed class HistoryRecordValidator
{
    private readonly Dictionary<long, string> _scheduled = new();
    private readonly HashSet<long> _completed = new();
    private readonly Dictionary<long, long> _attempts = new();
    private readonly HashSet<(long Seq, long Attempt)> _failedAttempts = new();
    private readonly Dictionary<long, string> _timers = new();
    private readonly HashSet<long> _timersFired = new();
    private bool _startedSeen;
    private bool _workflowCompletedSeen;

    /// <summary>
    /// Seeds the validator with the state a sealed snapshot stands for, so a
    /// snapshot-aware load can validate only the tail after the snapshot
    /// point under exactly the rules the full prefix was validated under.
    /// The snapshot was itself sealed and parsed by
    /// <see cref="HistorySnapshotCodec"/> before this is called.
    /// </summary>
    internal void SeedFromSnapshot(HistorySnapshot snapshot)
    {
        _startedSeen = true;
        foreach (var kv in snapshot.AttemptCounts)
            _attempts[long.Parse(kv.Key)] = kv.Value!.GetValue<long>();
        foreach (var node in snapshot.Facts)
        {
            var fact = node!.AsObject();
            string type = fact["type"]!.GetValue<string>();
            switch (type)
            {
                case HistoryEventTypes.ActivityScheduled:
                    _scheduled[fact["seq"]!.GetValue<long>()] = fact["activity"]!.GetValue<string>();
                    break;
                case HistoryEventTypes.ActivityCompleted:
                    _completed.Add(fact["seq"]!.GetValue<long>());
                    break;
                case HistoryEventTypes.ActivityAttemptFailed:
                    long failedSeq = fact["seq"]!.GetValue<long>();
                    long failedAttempt = fact["attempt"]!.GetValue<long>();
                    if (failedAttempt < 1 || failedAttempt > _attempts.GetValueOrDefault(failedSeq))
                        throw new HistoryCorruptionException(
                            $"snapshot: attempt failure recorded for attempt {failedAttempt} of command {failedSeq}, which the snapshot's attempt counts do not cover");
                    _failedAttempts.Add((failedSeq, failedAttempt));
                    break;
                case HistoryEventTypes.TimerScheduled:
                    _timers[fact["seq"]!.GetValue<long>()] = fact["dueAt"]!.GetValue<string>();
                    break;
                case HistoryEventTypes.TimerFired:
                    _timersFired.Add(fact["seq"]!.GetValue<long>());
                    break;
                case HistoryEventTypes.WorkflowCompleted:
                    _workflowCompletedSeen = true;
                    break;
            }
        }
    }

    /// <summary>
    /// Parses and validates the record expected at the 1-based
    /// <paramref name="ordinal"/> position of the history.
    /// </summary>
    public HistoryEvent Validate(string recordText, long ordinal)
    {
        string ctx = $"record {ordinal}";
        if (recordText.Length == 0)
            throw Corrupt($"{ctx}: blank record");

        JsonElement el;
        try { el = JsonDocument.Parse(recordText).RootElement.Clone(); }
        catch { throw Corrupt($"{ctx}: terminated record is not valid JSON"); }
        if (el.ValueKind != JsonValueKind.Object)
            throw Corrupt($"{ctx}: event is not a JSON object");

        string type = ReqString(el, "type", ctx);
        if (!HistoryEventTypes.Known.Contains(type))
            throw Corrupt($"{ctx}: unknown event type '{type}'");
        if (ReqLong(el, "v", ctx) != 1)
            throw Corrupt($"{ctx}: unsupported schema version");
        ReqString(el, "instance", ctx);
        ReqString(el, "ts", ctx);
        long n = ReqLong(el, "n", ctx);
        if (n != ordinal)
            throw Corrupt($"{ctx}: event sequence gap (expected n={ordinal}, found n={n})");

        var asObject = JsonNode.Parse(recordText)!.AsObject();
        if (!asObject.TryGetPropertyValue("sum", out var sumNode) ||
            sumNode?.GetValue<string>() != HistorySealing.Checksum(asObject))
            throw Corrupt($"{ctx}: checksum mismatch (content tampered or partially written)");
        if (_workflowCompletedSeen)
            throw Corrupt($"{ctx}: event recorded after WorkflowCompleted");

        switch (type)
        {
            case HistoryEventTypes.WorkflowStarted:
                if (ordinal != 1 || _startedSeen)
                    throw Corrupt($"{ctx}: WorkflowStarted must be the unique first event");
                _startedSeen = true;
                ReqString(el, "def", ctx);
                if (!Fingerprints.IsHex64(ReqString(el, "inputHash", ctx)))
                    throw Corrupt($"{ctx}: WorkflowStarted.inputHash malformed");
                break;

            case HistoryEventTypes.ActivityScheduled:
            {
                long seq = ReqLong(el, "seq", ctx);
                string activity = ReqString(el, "activity", ctx);
                if (!Fingerprints.IsHex64(ReqString(el, "idempotencyKey", ctx)))
                    throw Corrupt($"{ctx}: idempotencyKey malformed");
                if (!Fingerprints.IsHex64(ReqString(el, "inputHash", ctx)))
                    throw Corrupt($"{ctx}: inputHash malformed");
                if (seq < 1)
                    throw Corrupt($"{ctx}: seq must be >= 1");
                if (_timers.ContainsKey(seq))
                    throw Corrupt($"{ctx}: command position {seq} is already a timer");
                if (!_scheduled.TryAdd(seq, activity))
                    throw Corrupt($"{ctx}: duplicate ActivityScheduled for command {seq}");
                break;
            }

            case HistoryEventTypes.ActivityAttemptStarted:
            {
                long seq = ReqLong(el, "seq", ctx);
                long attempt = ReqLong(el, "attempt", ctx);
                if (!_scheduled.ContainsKey(seq))
                    throw Corrupt($"{ctx}: attempt for unscheduled command {seq}");
                long expected = _attempts.GetValueOrDefault(seq) + 1;
                if (attempt != expected)
                    throw Corrupt($"{ctx}: attempt numbers for command {seq} not sequential (expected {expected}, found {attempt})");
                _attempts[seq] = attempt;
                break;
            }

            case HistoryEventTypes.ActivityAttemptFailed:
            {
                long seq = ReqLong(el, "seq", ctx);
                long attempt = ReqLong(el, "attempt", ctx);
                ReqString(el, "error", ctx);
                if (!_scheduled.ContainsKey(seq))
                    throw Corrupt($"{ctx}: attempt failure for unscheduled command {seq}");
                if (_completed.Contains(seq))
                    throw Corrupt($"{ctx}: attempt failure for already-completed command {seq}");
                if (attempt != _attempts.GetValueOrDefault(seq))
                    throw Corrupt($"{ctx}: attempt failure names attempt {attempt} of command {seq}, but the latest started attempt is {_attempts.GetValueOrDefault(seq)}");
                if (!_failedAttempts.Add((seq, attempt)))
                    throw Corrupt($"{ctx}: duplicate attempt failure for attempt {attempt} of command {seq}");
                break;
            }

            case HistoryEventTypes.ActivityCompleted:
            {
                long seq = ReqLong(el, "seq", ctx);
                string activity = ReqString(el, "activity", ctx);
                if (!_scheduled.TryGetValue(seq, out var scheduledActivity))
                    throw Corrupt($"{ctx}: ActivityCompleted without ActivityScheduled for command {seq}");
                if (scheduledActivity != activity)
                    throw Corrupt($"{ctx}: ActivityCompleted activity '{activity}' does not match scheduled '{scheduledActivity}'");
                if (!_completed.Add(seq))
                    throw Corrupt($"{ctx}: duplicate ActivityCompleted for command {seq}");
                break;
            }

            case HistoryEventTypes.TimerScheduled:
            {
                long seq = ReqLong(el, "seq", ctx);
                string dueAt = ReqString(el, "dueAt", ctx);
                if (!DateTimeOffset.TryParse(dueAt, out _))
                    throw Corrupt($"{ctx}: TimerScheduled.dueAt malformed");
                if (ReqLong(el, "durationMs", ctx) < 0)
                    throw Corrupt($"{ctx}: TimerScheduled.durationMs must be >= 0");
                if (seq < 1)
                    throw Corrupt($"{ctx}: seq must be >= 1");
                if (_scheduled.ContainsKey(seq))
                    throw Corrupt($"{ctx}: command position {seq} is already an activity");
                if (!_timers.TryAdd(seq, dueAt))
                    throw Corrupt($"{ctx}: duplicate TimerScheduled for command {seq}");
                break;
            }

            case HistoryEventTypes.TimerFired:
            {
                long seq = ReqLong(el, "seq", ctx);
                if (!_timers.ContainsKey(seq))
                    throw Corrupt($"{ctx}: TimerFired without TimerScheduled for command {seq}");
                if (!_timersFired.Add(seq))
                    throw Corrupt($"{ctx}: duplicate TimerFired for command {seq}");
                break;
            }

            case HistoryEventTypes.SignalReceived:
                ReqString(el, "name", ctx);
                break;

            case HistoryEventTypes.VersionMarker:
                ReqString(el, "changeId", ctx);
                if (ReqLong(el, "version", ctx) < 0)
                    throw Corrupt($"{ctx}: VersionMarker.version must be >= 0");
                break;

            case HistoryEventTypes.WorkflowCompleted:
                _workflowCompletedSeen = true;
                break;
        }

        return new HistoryEvent(asObject);
    }

    private static HistoryCorruptionException Corrupt(string message) => new(message);

    private static long ReqLong(JsonElement e, string field, string ctx)
    {
        if (!e.TryGetProperty(field, out var v) ||
            v.ValueKind != JsonValueKind.Number ||
            !v.TryGetInt64(out long x))
            throw Corrupt($"{ctx}: field '{field}' missing or not an integer");
        return x;
    }

    private static string ReqString(JsonElement e, string field, string ctx)
    {
        if (!e.TryGetProperty(field, out var v) ||
            v.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(v.GetString()))
            throw Corrupt($"{ctx}: field '{field}' missing or not a non-empty string");
        return v.GetString()!;
    }
}
