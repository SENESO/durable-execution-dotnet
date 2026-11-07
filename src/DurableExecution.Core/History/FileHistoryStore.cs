using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DurableExecution.Execution;

namespace DurableExecution.History;

/// <summary>
/// Append-only file provider for <see cref="IHistoryStore"/>: one JSON record
/// per line, newline-framed, each record sealed with a SHA-256 checksum over
/// its canonical content.
///
/// Framing rule (the torn/corrupt distinction): a record counts as committed
/// <b>only</b> if it is newline-terminated. An unterminated tail is a torn
/// write from a crash mid-append — it was never committed, so loading
/// truncates it and reports it. A terminated record that fails parsing,
/// checksum, schema, or structural validation is <b>corruption</b> and is
/// refused with <see cref="HistoryCorruptionException"/> — never deleted,
/// never skipped.
///
/// Structural validation on load: event types are a closed set with required
/// typed fields; the event sequence is gapless; <c>WorkflowStarted</c> is the
/// unique first event; a command is scheduled exactly once; attempt numbers
/// are sequential per command; a completion must match its scheduled command's
/// activity; <c>WorkflowCompleted</c> is unique and last.
/// </summary>
public sealed class FileHistoryStore : IHistoryStore
{
    private readonly string _historyPath;
    private readonly IHistoryFaultInjector? _faultInjector;
    private long _nextSequence = 1;
    private bool _loaded;

    public FileHistoryStore(string stateDirectory, IHistoryFaultInjector? faultInjector = null)
    {
        Directory.CreateDirectory(stateDirectory);
        _historyPath = Path.Combine(stateDirectory, "history.log");
        _faultInjector = faultInjector;
    }

    public string HistoryPath => _historyPath;

    internal static string Canonical(JsonObject fields)
    {
        var copy = new JsonObject();
        foreach (var kv in fields)
            if (kv.Key != "sum")
                copy[kv.Key] = kv.Value?.DeepClone();
        return copy.ToJsonString();
    }

    internal static string Checksum(JsonObject fields) =>
        Fingerprints.Sha256Hex(Canonical(fields))[..16];

    public HistoryLoadResult Load()
    {
        var events = new List<HistoryEvent>();
        _loaded = true;
        _nextSequence = 1;
        if (!File.Exists(_historyPath))
            return new HistoryLoadResult(events, false, 0);

        byte[] bytes = File.ReadAllBytes(_historyPath);
        if (bytes.Length == 0)
            return new HistoryLoadResult(events, false, 0);

        // Frame scan on raw bytes: a record is committed only if newline-terminated.
        var records = new List<(int Start, int Length)>();
        int start = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == 0x0A) { records.Add((start, i - start)); start = i + 1; }
        }

        bool torn = false;
        long tornBytes = 0;
        if (start < bytes.Length)
        {
            // Unterminated tail = torn write: never committed, whatever its content.
            torn = true;
            tornBytes = bytes.Length - start;
            using var fs = new FileStream(_historyPath, FileMode.Open, FileAccess.Write);
            fs.SetLength(start);
        }

        var scheduled = new Dictionary<long, string>();
        var completed = new HashSet<long>();
        var attempts = new Dictionary<long, long>();
        bool startedSeen = false, workflowCompletedSeen = false;

        for (int r = 0; r < records.Count; r++)
        {
            string line = Encoding.UTF8.GetString(bytes, records[r].Start, records[r].Length);
            string ctx = $"record {r + 1}";
            if (line.Length == 0)
                throw Corrupt($"{ctx}: blank record");

            JsonElement el;
            try { el = JsonDocument.Parse(line).RootElement.Clone(); }
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
            if (n != r + 1)
                throw Corrupt($"{ctx}: event sequence gap (expected n={r + 1}, found n={n})");

            var asObject = JsonNode.Parse(line)!.AsObject();
            if (!asObject.TryGetPropertyValue("sum", out var sumNode) ||
                sumNode?.GetValue<string>() != Checksum(asObject))
                throw Corrupt($"{ctx}: checksum mismatch (content tampered or partially written)");
            if (workflowCompletedSeen)
                throw Corrupt($"{ctx}: event recorded after WorkflowCompleted");

            switch (type)
            {
                case HistoryEventTypes.WorkflowStarted:
                    if (r != 0 || startedSeen)
                        throw Corrupt($"{ctx}: WorkflowStarted must be the unique first event");
                    startedSeen = true;
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
                    if (!scheduled.TryAdd(seq, activity))
                        throw Corrupt($"{ctx}: duplicate ActivityScheduled for command {seq}");
                    break;
                }

                case HistoryEventTypes.ActivityAttemptStarted:
                {
                    long seq = ReqLong(el, "seq", ctx);
                    long attempt = ReqLong(el, "attempt", ctx);
                    if (!scheduled.ContainsKey(seq))
                        throw Corrupt($"{ctx}: attempt for unscheduled command {seq}");
                    long expected = attempts.GetValueOrDefault(seq) + 1;
                    if (attempt != expected)
                        throw Corrupt($"{ctx}: attempt numbers for command {seq} not sequential (expected {expected}, found {attempt})");
                    attempts[seq] = attempt;
                    break;
                }

                case HistoryEventTypes.ActivityCompleted:
                {
                    long seq = ReqLong(el, "seq", ctx);
                    string activity = ReqString(el, "activity", ctx);
                    if (!scheduled.TryGetValue(seq, out var scheduledActivity))
                        throw Corrupt($"{ctx}: ActivityCompleted without ActivityScheduled for command {seq}");
                    if (scheduledActivity != activity)
                        throw Corrupt($"{ctx}: ActivityCompleted activity '{activity}' does not match scheduled '{scheduledActivity}'");
                    if (!completed.Add(seq))
                        throw Corrupt($"{ctx}: duplicate ActivityCompleted for command {seq}");
                    break;
                }

                case HistoryEventTypes.WorkflowCompleted:
                    workflowCompletedSeen = true;
                    break;
            }

            events.Add(new HistoryEvent(asObject));
        }

        _nextSequence = events.Count + 1;
        return new HistoryLoadResult(events, torn, tornBytes);
    }

    public HistoryEvent Append(HistoryEvent historyEvent)
    {
        if (!_loaded)
            throw new InvalidOperationException("Load must be called before Append.");

        var fields = (JsonObject)historyEvent.Fields.DeepClone();
        fields["n"] = _nextSequence;
        fields["sum"] = Checksum(fields);
        string line = fields.ToJsonString();
        byte[] framed = Encoding.UTF8.GetBytes(line + "\n");

        bool tear = _faultInjector?.ShouldTear(new HistoryEvent(fields)) == true;
        using (var fs = new FileStream(_historyPath, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            if (tear)
            {
                byte[] partial = Encoding.UTF8.GetBytes(line); // no trailing newline
                fs.Write(partial, 0, partial.Length / 2);
                fs.Flush(true);
                _faultInjector!.OnTornWrite();
                throw new InvalidOperationException(
                    "Fault injector tore the write and returned; the record was not committed.");
            }
            fs.Write(framed, 0, framed.Length);
            fs.Flush(true);
        }
        _nextSequence++;
        return new HistoryEvent(fields);
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
