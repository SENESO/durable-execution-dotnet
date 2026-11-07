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
    private bool _startedSeen;
    private bool _workflowCompletedSeen;

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
