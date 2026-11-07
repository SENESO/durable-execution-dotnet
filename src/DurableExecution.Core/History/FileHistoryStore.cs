using System.Text;
using System.Text.Json.Nodes;
using DurableExecution.Execution;
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
public sealed class FileHistoryStore : IHistoryStore, IHistorySnapshotStore, IHistoryKeyRetention
{
    /// <summary>Default snapshot cadence: one sealed snapshot per 50 appended events (0 disables snapshots).</summary>
    public const int DefaultSnapshotInterval = 50;

    private readonly string _historyPath;
    private readonly string _snapshotPath;
    private readonly string _registryPath;
    private readonly string _leasePath;
    private readonly string _leaseLockPath;
    private readonly IHistoryFaultInjector? _faultInjector;
    private readonly int _snapshotInterval;
    private readonly HistoryEncryption? _encryption;
    private readonly KeyRetentionPolicy? _keyRetention;
    private readonly FencingOptions? _fencing;
    private readonly string? _fencingOwnerId;
    private HistorySnapshotBuilder _snapshotBuilder = HistorySnapshotCodec.Builder();
    private KeyRegistryState _registry = new();
    private string? _registryInstance;
    private long _nextSequence = 1;
    private bool _loaded;

    public FileHistoryStore(string stateDirectory, IHistoryFaultInjector? faultInjector = null,
        int snapshotInterval = DefaultSnapshotInterval, HistoryEncryption? encryption = null,
        KeyRetentionPolicy? keyRetention = null, FencingOptions? fencing = null)
    {
        if (snapshotInterval < 0)
            throw new ArgumentOutOfRangeException(nameof(snapshotInterval), "snapshot interval cannot be negative");
        Directory.CreateDirectory(stateDirectory);
        _historyPath = Path.Combine(stateDirectory, "history.log");
        _snapshotPath = Path.Combine(stateDirectory, "history.snapshot.json");
        _registryPath = Path.Combine(stateDirectory, "history.keys.json");
        _leasePath = Path.Combine(stateDirectory, "history.lease.json");
        _leaseLockPath = Path.Combine(stateDirectory, "history.lease.lock");
        _faultInjector = faultInjector;
        _snapshotInterval = snapshotInterval;
        _encryption = encryption;
        _keyRetention = keyRetention;
        _fencing = fencing;
        _fencingOwnerId = fencing?.ResolveOwnerId();
    }

    /// <summary>Path of the sealed key registry (§8.5), when one has been written.</summary>
    public string RegistryPath => _registryPath;

    /// <summary>The fencing token this store currently writes under (fencing leases, P3), or null when fencing is off.</summary>
    public long? FencingToken { get; private set; }

    /// <summary>Seals a record or snapshot payload for storage: its envelope when encryption is on, itself when off.</summary>
    private string SealText(string text) => _encryption is null ? text : _encryption.Seal(text);

    /// <summary>Opens a stored record or snapshot payload: the inverse of <see cref="SealText"/>, with the cross-mode refusals of <see cref="HistoryEncryption"/>.</summary>
    private string OpenText(string text) =>
        _encryption is null ? HistoryEncryption.EnsurePlaintext(text) : _encryption.Open(text);

    // ---- P3 fencing: the sealed lease file beside the log ----

    private sealed record LeaseState(string Owner, long Token, DateTimeOffset ExpiresAtUtc);

    /// <summary>
    /// Acquires (or renews) this store's lease at load: acquirers
    /// serialize on a short-lived lock file while they read, decide, and
    /// atomically replace the sealed lease file. A free or expired lease
    /// is taken with the token incremented (a lapsed lease of my own
    /// reissues at token+1 too); my unexpired lease renews at the same
    /// token; another owner's unexpired lease refuses the load. The file
    /// provider is single-machine, so the clock is the machine clock.
    /// </summary>
    private void AcquireLease()
    {
        using var guard = new FileStream(_leaseLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var current = ReadLease();
        var now = DateTimeOffset.UtcNow;
        if (current is null)
        {
            WriteLease(new LeaseState(_fencingOwnerId!, 1, now + _fencing!.LeaseDuration));
            FencingToken = 1;
        }
        else if (current.ExpiresAtUtc <= now)
        {
            WriteLease(new LeaseState(_fencingOwnerId!, current.Token + 1, now + _fencing!.LeaseDuration));
            FencingToken = current.Token + 1;
        }
        else if (current.Owner == _fencingOwnerId)
        {
            WriteLease(new LeaseState(current.Owner, current.Token, now + _fencing!.LeaseDuration));
            FencingToken = current.Token;
        }
        else
        {
            throw new StateOwnedByAnotherHostException(
                $"history in '{Path.GetDirectoryName(_historyPath)}' is fenced by '{current.Owner}' " +
                $"until {current.ExpiresAtUtc:o} — refusing to load");
        }
    }

    /// <summary>
    /// The append's fencing check: the lease file must still name this
    /// owner with this token and be unexpired, or the append is refused
    /// stale. (Check and append are two writes on this provider — see
    /// <see cref="FencingOptions"/> for exactly what that does and does
    /// not promise.)
    /// </summary>
    private void VerifyLeaseForAppend()
    {
        if (_fencing is null)
            return;
        var current = ReadLease();
        if (current is not null && current.Owner == _fencingOwnerId &&
            current.Token == FencingToken!.Value && current.ExpiresAtUtc > DateTimeOffset.UtcNow)
            return;
        string currentState = current is null
            ? "the lease file is gone"
            : $"the current lease names owner '{current.Owner}' with token {current.Token} until {current.ExpiresAtUtc:o}";
        throw new StaleFencingTokenException(
            Path.GetDirectoryName(_historyPath)!, _fencingOwnerId!, FencingToken!.Value, currentState);
    }

    private LeaseState? ReadLease()
    {
        if (!File.Exists(_leasePath))
            return null;
        JsonObject fields;
        try
        {
            fields = JsonNode.Parse(OpenText(File.ReadAllText(_leasePath)))?.AsObject()
                ?? throw new System.Text.Json.JsonException("lease is not a JSON object");
        }
        catch (HistoryCorruptionException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new HistoryCorruptionException($"fencing lease: not valid JSON ({e.Message})");
        }
        if (!fields.TryGetPropertyValue("sum", out var sumNode) ||
            sumNode?.GetValue<string>() != HistorySealing.Checksum(fields))
            throw new HistoryCorruptionException(
                "fencing lease: checksum mismatch (content tampered or partially written)");
        return new LeaseState(
            fields["owner"]!.GetValue<string>(),
            fields["token"]!.GetValue<long>(),
            DateTimeOffset.Parse(fields["expiresAtUtc"]!.GetValue<string>()));
    }

    private void WriteLease(LeaseState lease)
    {
        var fields = new JsonObject
        {
            ["v"] = 1,
            ["owner"] = lease.Owner,
            ["token"] = lease.Token,
            ["expiresAtUtc"] = lease.ExpiresAtUtc.ToString("o"),
        };
        fields["sum"] = HistorySealing.Checksum(fields);
        AtomicFile.WriteAllText(_leasePath, SealText(fields.ToJsonString()));
    }

    // ---- §8.5 key registry: sealed derived state beside the log ----

    /// <summary>
    /// Reloads the registry from its file and folds the loaded history
    /// into it, persisting when the fold moved it. The registry file is
    /// sealed like a snapshot: a damaged registry is refused, never
    /// rebuilt silently — rebuilding would lose the purge marker, the
    /// one piece of registry state history cannot re-derive.
    /// </summary>
    private void FoldRegistry(IReadOnlyList<HistoryEvent> events)
    {
        _registry = ReadRegistryState();
        bool changed = false;
        foreach (var historyEvent in events)
        {
            _registryInstance ??= historyEvent.InstanceId;
            changed |= _registry.Apply(historyEvent);
        }
        if (changed || (_registry.Keys.Count > 0 && !File.Exists(_registryPath)))
            PersistRegistry();
    }

    private KeyRegistryState ReadRegistryState()
    {
        var state = new KeyRegistryState();
        if (!File.Exists(_registryPath))
            return state;

        JsonObject fields;
        try
        {
            fields = JsonNode.Parse(OpenText(File.ReadAllText(_registryPath)))?.AsObject()
                ?? throw new System.Text.Json.JsonException("registry is not a JSON object");
        }
        catch (HistoryCorruptionException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new HistoryCorruptionException($"key registry: not valid JSON ({e.Message})");
        }
        if (!fields.TryGetPropertyValue("sum", out var sumNode) ||
            sumNode?.GetValue<string>() != HistorySealing.Checksum(fields))
            throw new HistoryCorruptionException(
                "key registry: checksum mismatch (content tampered or partially written)");

        var keys = new Dictionary<long, string>();
        if (fields.TryGetPropertyValue("keys", out var keysNode) && keysNode is JsonObject keysObject)
            foreach (var kv in keysObject)
                keys[long.Parse(kv.Key)] = kv.Value!.GetValue<string>();
        DateTimeOffset? completedAt = null;
        if (fields.TryGetPropertyValue("completedAtUtc", out var completedNode) && completedNode is not null)
            completedAt = DateTimeOffset.Parse(completedNode.GetValue<string>());
        bool purged = fields.TryGetPropertyValue("keysPurged", out var purgedNode) &&
            purgedNode is not null && purgedNode.GetValue<bool>();
        if (fields.TryGetPropertyValue("instance", out var instanceNode) && instanceNode is not null &&
            !string.IsNullOrEmpty(instanceNode.GetValue<string>()))
            _registryInstance = instanceNode.GetValue<string>();
        state.Restore(keys, completedAt, purged);
        return state;
    }

    private void PersistRegistry()
    {
        var keys = new JsonObject();
        foreach (var kv in _registry.Keys.OrderBy(kv => kv.Key))
            keys[kv.Key.ToString()] = kv.Value;
        var fields = new JsonObject
        {
            ["v"] = 1,
            ["instance"] = _registryInstance ?? "",
            ["keysPurged"] = _registry.KeysPurged,
            ["keys"] = keys,
        };
        if (_registry.CompletedAtUtc is not null)
            fields["completedAtUtc"] = _registry.CompletedAtUtc.Value.ToString("o");
        fields["sum"] = HistorySealing.Checksum(fields);
        AtomicFile.WriteAllText(_registryPath, SealText(fields.ToJsonString()));
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<long, string> RecordedKeys => _registry.Keys;

    /// <inheritdoc/>
    public DateTimeOffset? CompletedAtUtc => _registry.CompletedAtUtc;

    /// <inheritdoc/>
    public bool KeyOnRecord(string idempotencyKey) => _registry.Keys.Values.Contains(idempotencyKey);

    /// <inheritdoc/>
    public int PurgeExpiredKeys(DateTimeOffset utcNow)
    {
        if (_keyRetention is null)
            throw new InvalidOperationException(
                "no key retention policy is configured on this store — PurgeExpiredKeys has no policy to apply");
        if (!_loaded)
            throw new InvalidOperationException("Load must be called before PurgeExpiredKeys.");
        if (!_registry.IsPurgeable(utcNow, _keyRetention))
            return 0;
        int purged = _registry.MarkPurged();
        PersistRegistry();
        return purged;
    }

    public string HistoryPath => _historyPath;

    /// <summary>Path of the latest sealed snapshot, when one has been written.</summary>
    public string SnapshotPath => _snapshotPath;

    internal static string Canonical(JsonObject fields) => HistorySealing.Canonical(fields);

    internal static string Checksum(JsonObject fields) => HistorySealing.Checksum(fields);

    public HistoryLoadResult Load()
    {
        if (_fencing is not null)
            AcquireLease();
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
            events.Add(validator.Validate(OpenText(line), r + 1));
        }

        _nextSequence = events.Count + 1;
        _snapshotBuilder = HistorySnapshotCodec.Builder();
        foreach (var historyEvent in events)
            _snapshotBuilder.Apply(historyEvent);
        FoldRegistry(events);
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
        if (_fencing is not null)
            AcquireLease();
        if (!File.Exists(_snapshotPath))
        {
            var full = Load();
            return new HistorySnapshotLoadResult(full, false, 0);
        }

        var snapshot = HistorySnapshotCodec.Parse(OpenText(File.ReadAllText(_snapshotPath)));
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
            var tailEvent = validator.Validate(OpenText(line), r + 1);
            events.Add(tailEvent);
            builder.Apply(tailEvent);
        }

        _snapshotBuilder = builder;
        _nextSequence = records.Count + 1;
        FoldRegistry(events);
        return new HistorySnapshotLoadResult(
            new HistoryLoadResult(events, torn, tornBytes), true, snapshot.ThroughN);
    }

    public HistoryEvent Append(HistoryEvent historyEvent)
    {
        if (!_loaded)
            throw new InvalidOperationException("Load must be called before Append.");
        VerifyLeaseForAppend();
        using var appendSpan = Diagnostics.EngineDiagnostics.StartStoreAppendSpan(
            "file", historyEvent.Type, historyEvent.InstanceId);

        var fields = (JsonObject)historyEvent.Fields.DeepClone();
        fields["n"] = _nextSequence;
        fields["sum"] = Checksum(fields);
        string line = SealText(fields.ToJsonString());
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
        _registryInstance ??= framedEvent.InstanceId;
        if (_registry.Apply(framedEvent))
            PersistRegistry();
        if (_snapshotInterval > 0 && framedEvent.N % _snapshotInterval == 0 && _snapshotBuilder.CanSnapshot)
        {
            AtomicFile.WriteAllText(_snapshotPath,
                SealText(HistorySnapshotCodec.Serialize(_snapshotBuilder.Build())));
        }
        Diagnostics.EngineDiagnostics.RecordStoreAppend("file", framedEvent);
        return framedEvent;
    }
}
