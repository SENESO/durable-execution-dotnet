using System.Text;
using System.Text.Json.Nodes;
using DurableExecution.IO;

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
/// Structural validation on load is shared with every provider
/// (<see cref="HistoryRecordValidator"/>): event types are a closed set with
/// required typed fields; the event sequence is gapless;
/// <c>WorkflowStarted</c> is the unique first event; a command is scheduled
/// exactly once; attempt numbers are sequential per command; a completion
/// must match its scheduled command's activity; <c>WorkflowCompleted</c> is
/// unique and last.
/// </summary>
public sealed class FileHistoryStore : IHistoryStore, IHistorySnapshotStore
{
    /// <summary>Default snapshot cadence: one sealed snapshot per 50 appended events (0 disables snapshots).</summary>
    public const int DefaultSnapshotInterval = 50;

    private readonly string _historyPath;
    private readonly string _snapshotPath;
    private readonly IHistoryFaultInjector? _faultInjector;
    private readonly int _snapshotInterval;
    private HistorySnapshotBuilder _snapshotBuilder = HistorySnapshotCodec.Builder();
    private long _nextSequence = 1;
    private bool _loaded;

    public FileHistoryStore(string stateDirectory, IHistoryFaultInjector? faultInjector = null,
        int snapshotInterval = DefaultSnapshotInterval)
    {
        if (snapshotInterval < 0)
            throw new ArgumentOutOfRangeException(nameof(snapshotInterval), "snapshot interval cannot be negative");
        Directory.CreateDirectory(stateDirectory);
        _historyPath = Path.Combine(stateDirectory, "history.log");
        _snapshotPath = Path.Combine(stateDirectory, "history.snapshot.json");
        _faultInjector = faultInjector;
        _snapshotInterval = snapshotInterval;
    }

    public string HistoryPath => _historyPath;

    /// <summary>Path of the latest sealed snapshot, when one has been written.</summary>
    public string SnapshotPath => _snapshotPath;

    internal static string Canonical(JsonObject fields) => HistorySealing.Canonical(fields);

    internal static string Checksum(JsonObject fields) => HistorySealing.Checksum(fields);

    public HistoryLoadResult Load()
    {
        var events = new List<HistoryEvent>();
        _loaded = true;
        _nextSequence = 1;
        _snapshotBuilder = HistorySnapshotCodec.Builder();
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

        var validator = new HistoryRecordValidator();
        for (int r = 0; r < records.Count; r++)
        {
            string line = Encoding.UTF8.GetString(bytes, records[r].Start, records[r].Length);
            events.Add(validator.Validate(line, r + 1));
        }

        _nextSequence = events.Count + 1;
        _snapshotBuilder = HistorySnapshotCodec.Builder();
        foreach (var historyEvent in events)
            _snapshotBuilder.Apply(historyEvent);
        return new HistoryLoadResult(events, torn, tornBytes);
    }

    /// <summary>
    /// Snapshot-aware load: validates the sealed snapshot, then parses and
    /// validates only the records after its point (the prefix is represented
    /// by the snapshot's reconstructed state). A corrupt snapshot is refused
    /// loudly — there is no silent fallback to a full replay.
    /// </summary>
    public HistorySnapshotLoadResult LoadFromSnapshot()
    {
        if (!File.Exists(_snapshotPath))
        {
            var full = Load();
            return new HistorySnapshotLoadResult(full, false, 0);
        }

        var snapshot = HistorySnapshotCodec.Parse(File.ReadAllText(_snapshotPath));
        _loaded = true;

        byte[] bytes = File.Exists(_historyPath) ? File.ReadAllBytes(_historyPath) : Array.Empty<byte>();
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
            torn = true;
            tornBytes = bytes.Length - start;
            using var fs = new FileStream(_historyPath, FileMode.Open, FileAccess.Write);
            fs.SetLength(start);
        }
        if (records.Count < snapshot.ThroughN)
            throw new HistoryCorruptionException(
                $"snapshot: snapshot is ahead of history (snapshot through {snapshot.ThroughN}, history has {records.Count} records)");

        var builder = HistorySnapshotCodec.BuilderFrom(snapshot);
        var events = builder.ToEvents();
        var validator = new HistoryRecordValidator();
        validator.SeedFromSnapshot(snapshot);
        for (int r = (int)snapshot.ThroughN; r < records.Count; r++)
        {
            string line = Encoding.UTF8.GetString(bytes, records[r].Start, records[r].Length);
            var tailEvent = validator.Validate(line, r + 1);
            events.Add(tailEvent);
            builder.Apply(tailEvent);
        }

        _snapshotBuilder = builder;
        _nextSequence = records.Count + 1;
        return new HistorySnapshotLoadResult(
            new HistoryLoadResult(events, torn, tornBytes), true, snapshot.ThroughN);
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
        var framedEvent = new HistoryEvent(fields);
        _snapshotBuilder.Apply(framedEvent);
        if (_snapshotInterval > 0 && framedEvent.N % _snapshotInterval == 0 && _snapshotBuilder.CanSnapshot)
        {
            AtomicFile.WriteAllText(_snapshotPath,
                HistorySnapshotCodec.Serialize(_snapshotBuilder.Build()));
        }
        return framedEvent;
    }
}
