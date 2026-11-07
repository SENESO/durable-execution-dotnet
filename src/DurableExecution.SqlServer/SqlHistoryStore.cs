using System.Data;
using System.Text.Json.Nodes;
using DurableExecution.Diagnostics;
using DurableExecution.Execution;
using DurableExecution.History;
using Microsoft.Data.SqlClient;

namespace DurableExecution.SqlServer;

/// <summary>
/// SQL Server provider for <see cref="IHistoryStore"/> (foundation §6.2, §6.5):
/// one instance's append-only history as rows of <c>dbo.HistoryEvents</c> in a
/// database the caller provisions. Each row carries the identical sealed JSON
/// payload the file provider frames — same fields, same checksum — so a
/// history means the same thing in either medium, and load runs the same
/// structural validation (<see cref="HistoryRecordValidator"/>) and refuses
/// the same damage with the same words.
///
/// Checkpoint atomicity (§6.2): an append is a single serializable
/// transaction — insert the row, verify the table still ends where this
/// store's counter says it must, commit. A crash mid-append leaves either
/// the whole record or nothing; there is no torn state to repair, so
/// <see cref="HistoryLoadResult.TornTailTruncated"/> is always false here.
/// (The file provider's torn tail is a framing property of files, not a
/// contract property; the contract is "unterminated ≠ committed", and a
/// rolled-back transaction is never committed.)
///
/// Single-writer (§9, in-database analogue of the host's OS lock): the store
/// holds one dedicated connection for its lifetime and takes an exclusive
/// session application lock (<c>sp_getapplock</c>, resource per instance) at
/// load. A second store for the same instance in the same database is
/// refused with <see cref="StateOwnedByAnotherHostException"/> once a short
/// grace window passes — the window exists because a killed owner's session
/// lock is released by the server when it reaps the dead session, which
/// trails the process death by a moment; a live owner still holds the lock
/// after the window and is refused. Disposing the store releases the lock.
///
/// Limits, stated plainly: the application lock is cooperative — a writer
/// that ignores it and writes the table directly is caught by the append
/// transaction's end-of-history check, which refuses to append over a
/// history that moved — but nothing here fences writers *across* databases
/// or machines. Cross-device fencing is P3; this provider does not fake it.
/// </summary>
public sealed class SqlHistoryStore : IHistoryStore, IHistorySnapshotStore, IHistoryKeyRetention, IDisposable
{
    /// <summary>How long load waits for a previous owner's lock to clear before refusing (a killed owner's session is reaped asynchronously by the server).</summary>
    private static readonly TimeSpan LockGraceWindow = TimeSpan.FromSeconds(2);

    private readonly string _connectionString;
    private readonly string _instanceId;
    private readonly IHistoryFaultInjector? _faultInjector;
    private readonly int _snapshotInterval;
    private readonly HistoryEncryption? _encryption;
    private readonly KeyRetentionPolicy? _keyRetention;
    private readonly FencingOptions? _fencing;
    private readonly string? _fencingOwnerId;
    private HistorySnapshotBuilder _snapshotBuilder = HistorySnapshotCodec.Builder();
    private KeyRegistryState _registry = new();

    /// <summary>The fencing token this store currently writes under (fencing leases, P3), or null when fencing is off.</summary>
    public long? FencingToken { get; private set; }
    private readonly object _gate = new();

    private SqlConnection? _connection;
    private bool _lockHeld;
    private bool _schemaReady;
    private bool _loaded;
    private bool _disposed;
    private long _nextSequence = 1;

    /// <param name="connectionString">Connection string for the database that holds this history. The database must already exist; the store creates its table.</param>
    /// <param name="instanceId">The workflow instance whose history this store owns. Histories of other instances may share the database; they are never read or written here.</param>
    /// <param name="faultInjector">Crash-injection seam for the fault suite; production hosts attach none.</param>
    public SqlHistoryStore(string connectionString, string instanceId, IHistoryFaultInjector? faultInjector = null,
        int snapshotInterval = FileHistoryStore.DefaultSnapshotInterval, HistoryEncryption? encryption = null,
        KeyRetentionPolicy? keyRetention = null, FencingOptions? fencing = null)
    {
        if (snapshotInterval < 0)
            throw new ArgumentOutOfRangeException(nameof(snapshotInterval), "snapshot interval cannot be negative");
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _instanceId = string.IsNullOrEmpty(instanceId)
            ? throw new ArgumentException("instance id must be a non-empty string", nameof(instanceId))
            : instanceId;
        _faultInjector = faultInjector;
        _snapshotInterval = snapshotInterval;
        _encryption = encryption;
        _keyRetention = keyRetention;
        _fencing = fencing;
        _fencingOwnerId = fencing?.ResolveOwnerId();
    }

    /// <summary>Seals a record or snapshot payload for storage: its envelope when encryption is on, itself when off.</summary>
    private string SealText(string text) => _encryption is null ? text : _encryption.Seal(text);

    /// <summary>Opens a stored record or snapshot payload: the inverse of <see cref="SealText"/>, with the cross-mode refusals of <see cref="HistoryEncryption"/>.</summary>
    private string OpenText(string text) =>
        _encryption is null ? HistoryEncryption.EnsurePlaintext(text) : _encryption.Open(text);

    // ---- P3 fencing: leases with a monotonic token (dbo.HistoryLeases) ----

    /// <summary>
    /// Acquires (or renews) this store's lease at load: a free or expired
    /// lease is taken with the token incremented (a lapsed lease of my own
    /// reissues at token+1 too, so a zombie store of mine holding the old
    /// token stays fenced out); my unexpired lease renews at the same
    /// token; another owner's unexpired lease refuses the load. All
    /// timestamps are the server's (SYSUTCDATETIME).
    /// </summary>
    private void AcquireLease()
    {
        using var tx = _connection!.BeginTransaction();
        string? owner = null;
        long token = 0;
        DateTimeOffset expires = default;
        bool found = false;
        using (var select = new SqlCommand(
            "SELECT OwnerId, FencingToken, ExpiresAtUtc FROM dbo.HistoryLeases WITH (UPDLOCK, HOLDLOCK) WHERE InstanceId = @instance",
            _connection, tx))
        {
            select.Parameters.Add(InstanceParam());
            using var reader = select.ExecuteReader();
            if (reader.Read())
            {
                found = true;
                owner = reader.GetString(0);
                token = reader.GetInt64(1);
                expires = reader.GetFieldValue<DateTimeOffset>(2);
            }
        }
        DateTimeOffset now;
        using (var nowCmd = new SqlCommand("SELECT SYSDATETIMEOFFSET()", _connection, tx))
            now = (DateTimeOffset)nowCmd.ExecuteScalar()!;
        long leaseMs = (long)_fencing!.LeaseDuration.TotalMilliseconds;

        if (!found)
        {
            using var insert = new SqlCommand(
                "INSERT INTO dbo.HistoryLeases (InstanceId, OwnerId, FencingToken, ExpiresAtUtc) " +
                "VALUES (@instance, @owner, 1, DATEADD(MILLISECOND, @leaseMs, SYSUTCDATETIME()))", _connection, tx);
            insert.Parameters.Add(InstanceParam());
            insert.Parameters.Add(new SqlParameter("@owner", SqlDbType.NVarChar, 128) { Value = _fencingOwnerId! });
            insert.Parameters.Add(new SqlParameter("@leaseMs", SqlDbType.BigInt) { Value = leaseMs });
            insert.ExecuteNonQuery();
            FencingToken = 1;
        }
        else if (expires <= now)
        {
            using var update = new SqlCommand(
                "UPDATE dbo.HistoryLeases SET OwnerId = @owner, FencingToken = @token, " +
                "ExpiresAtUtc = DATEADD(MILLISECOND, @leaseMs, SYSUTCDATETIME()) WHERE InstanceId = @instance",
                _connection, tx);
            update.Parameters.Add(InstanceParam());
            update.Parameters.Add(new SqlParameter("@owner", SqlDbType.NVarChar, 128) { Value = _fencingOwnerId! });
            update.Parameters.Add(new SqlParameter("@token", SqlDbType.BigInt) { Value = token + 1 });
            update.Parameters.Add(new SqlParameter("@leaseMs", SqlDbType.BigInt) { Value = leaseMs });
            update.ExecuteNonQuery();
            FencingToken = token + 1;
        }
        else if (owner == _fencingOwnerId)
        {
            using var renew = new SqlCommand(
                "UPDATE dbo.HistoryLeases SET ExpiresAtUtc = DATEADD(MILLISECOND, @leaseMs, SYSUTCDATETIME()) " +
                "WHERE InstanceId = @instance", _connection, tx);
            renew.Parameters.Add(InstanceParam());
            renew.Parameters.Add(new SqlParameter("@leaseMs", SqlDbType.BigInt) { Value = leaseMs });
            renew.ExecuteNonQuery();
            FencingToken = token;
        }
        else
        {
            tx.Rollback();
            throw new StateOwnedByAnotherHostException(
                $"history of instance '{_instanceId}' is fenced by '{owner}' until {expires:o} — refusing to load");
        }
        tx.Commit();
    }

    /// <summary>
    /// The append's fencing check, inside the append's own transaction:
    /// the lease row must still name this owner with this token and be
    /// unexpired (server clock), or the append is refused stale.
    /// </summary>
    private void VerifyLeaseForAppend(SqlTransaction tx)
    {
        if (_fencing is null)
            return;
        int rows;
        using (var check = new SqlCommand(
            "UPDATE dbo.HistoryLeases SET ExpiresAtUtc = ExpiresAtUtc " +
            "WHERE InstanceId = @instance AND OwnerId = @owner AND FencingToken = @token " +
            "AND ExpiresAtUtc > SYSUTCDATETIME()", _connection, tx))
        {
            check.Parameters.Add(InstanceParam());
            check.Parameters.Add(new SqlParameter("@owner", SqlDbType.NVarChar, 128) { Value = _fencingOwnerId! });
            check.Parameters.Add(new SqlParameter("@token", SqlDbType.BigInt) { Value = FencingToken!.Value });
            rows = check.ExecuteNonQuery();
        }
        if (rows == 1)
            return;

        string currentState = "the lease row is gone";
        using (var select = new SqlCommand(
            "SELECT OwnerId, FencingToken, ExpiresAtUtc FROM dbo.HistoryLeases WHERE InstanceId = @instance",
            _connection, tx))
        {
            select.Parameters.Add(InstanceParam());
            using var reader = select.ExecuteReader();
            if (reader.Read())
                currentState = $"the current lease names owner '{reader.GetString(0)}' with token " +
                    $"{reader.GetInt64(1)} until {reader.GetFieldValue<DateTimeOffset>(2):o}";
        }
        throw new StaleFencingTokenException(_instanceId, _fencingOwnerId!, FencingToken!.Value, currentState);
    }

    // ---- §8.5 key registry: derived state in tables beside the history ----

    /// <summary>
    /// Reloads the registry tables and folds the loaded history into
    /// them, writing back when the fold moved the state. The fold is
    /// idempotent and history-derived; the purge marker in the state
    /// table is the one fact the fold respects rather than recomputes.
    /// </summary>
    private void FoldRegistry(IReadOnlyList<HistoryEvent> events)
    {
        var keys = new Dictionary<long, string>();
        DateTimeOffset? completedAt = null;
        bool purged = false;
        using (var cmd = new SqlCommand(
            "SELECT Seq, IdempotencyKey FROM dbo.HistoryKeys WHERE InstanceId = @instance", _connection))
        {
            cmd.Parameters.Add(InstanceParam());
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                keys[reader.GetInt64(0)] = reader.GetString(1);
        }
        using (var cmd = new SqlCommand(
            "SELECT CompletedAtUtc, KeysPurged FROM dbo.HistoryInstanceState WHERE InstanceId = @instance", _connection))
        {
            cmd.Parameters.Add(InstanceParam());
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                completedAt = reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0);
                purged = reader.GetBoolean(1);
            }
        }
        _registry.Restore(keys, completedAt, purged);

        bool changed = false;
        foreach (var historyEvent in events)
            changed |= _registry.Apply(historyEvent);
        if (!changed)
            return;

        using var tx = _connection!.BeginTransaction();
        WriteRegistryState(tx);
        tx.Commit();
    }

    /// <summary>Upserts the full in-memory registry state inside the given transaction.</summary>
    private void WriteRegistryState(SqlTransaction tx)
    {
        foreach (var kv in _registry.Keys)
        {
            using var merge = new SqlCommand(
                "MERGE dbo.HistoryKeys AS target " +
                "USING (SELECT @instance AS InstanceId, @seq AS Seq) AS source " +
                "ON target.InstanceId = source.InstanceId AND target.Seq = source.Seq " +
                "WHEN MATCHED THEN UPDATE SET IdempotencyKey = @key " +
                "WHEN NOT MATCHED THEN INSERT (InstanceId, Seq, IdempotencyKey) VALUES (@instance, @seq, @key);",
                _connection, tx);
            merge.Parameters.Add(InstanceParam());
            merge.Parameters.Add(new SqlParameter("@seq", SqlDbType.BigInt) { Value = kv.Key });
            merge.Parameters.Add(new SqlParameter("@key", SqlDbType.NVarChar, 64) { Value = kv.Value });
            merge.ExecuteNonQuery();
        }
        using var mergeState = new SqlCommand(
            "MERGE dbo.HistoryInstanceState AS target " +
            "USING (SELECT @instance AS InstanceId) AS source ON target.InstanceId = source.InstanceId " +
            "WHEN MATCHED THEN UPDATE SET CompletedAtUtc = @completed, KeysPurged = @purged " +
            "WHEN NOT MATCHED THEN INSERT (InstanceId, CompletedAtUtc, KeysPurged) VALUES (@instance, @completed, @purged);",
            _connection, tx);
        mergeState.Parameters.Add(InstanceParam());
        mergeState.Parameters.Add(new SqlParameter("@completed", SqlDbType.DateTimeOffset)
        { Value = _registry.CompletedAtUtc is null ? DBNull.Value : _registry.CompletedAtUtc.Value });
        mergeState.Parameters.Add(new SqlParameter("@purged", SqlDbType.Bit) { Value = _registry.KeysPurged });
        mergeState.ExecuteNonQuery();
    }

    /// <summary>
    /// The registry side of one append, inside the append's own
    /// transaction: a scheduled command's key and a completion's
    /// timestamp become registry rows in the same commit as the record.
    /// </summary>
    private void WriteRegistryForAppend(SqlTransaction tx, HistoryEvent framed)
    {
        if (framed.Type == HistoryEventTypes.ActivityScheduled)
        {
            using var merge = new SqlCommand(
                "MERGE dbo.HistoryKeys AS target " +
                "USING (SELECT @instance AS InstanceId, @seq AS Seq) AS source " +
                "ON target.InstanceId = source.InstanceId AND target.Seq = source.Seq " +
                "WHEN MATCHED THEN UPDATE SET IdempotencyKey = @key " +
                "WHEN NOT MATCHED THEN INSERT (InstanceId, Seq, IdempotencyKey) VALUES (@instance, @seq, @key);",
                _connection, tx);
            merge.Parameters.Add(InstanceParam());
            merge.Parameters.Add(new SqlParameter("@seq", SqlDbType.BigInt) { Value = framed.CommandSequence!.Value });
            merge.Parameters.Add(new SqlParameter("@key", SqlDbType.NVarChar, 64) { Value = framed.GetString("idempotencyKey")! });
            merge.ExecuteNonQuery();
        }
        else if (framed.Type == HistoryEventTypes.WorkflowCompleted)
        {
            using var mergeState = new SqlCommand(
                "MERGE dbo.HistoryInstanceState AS target " +
                "USING (SELECT @instance AS InstanceId) AS source ON target.InstanceId = source.InstanceId " +
                "WHEN MATCHED THEN UPDATE SET CompletedAtUtc = @completed " +
                "WHEN NOT MATCHED THEN INSERT (InstanceId, CompletedAtUtc, KeysPurged) VALUES (@instance, @completed, 0);",
                _connection, tx);
            mergeState.Parameters.Add(InstanceParam());
            mergeState.Parameters.Add(new SqlParameter("@completed", SqlDbType.DateTimeOffset)
            { Value = DateTimeOffset.Parse(framed.GetString("ts")!) });
            mergeState.ExecuteNonQuery();
        }
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
        lock (_gate)
        {
            if (_keyRetention is null)
                throw new InvalidOperationException(
                    "no key retention policy is configured on this store — PurgeExpiredKeys has no policy to apply");
            if (!_loaded)
                throw new InvalidOperationException("Load must be called before PurgeExpiredKeys.");
            ThrowIfDisposed();
            if (!_registry.IsPurgeable(utcNow, _keyRetention))
                return 0;

            using var tx = _connection!.BeginTransaction();
            int deleted;
            using (var delete = new SqlCommand(
                "DELETE FROM dbo.HistoryKeys WHERE InstanceId = @instance", _connection, tx))
            {
                delete.Parameters.Add(InstanceParam());
                deleted = delete.ExecuteNonQuery();
            }
            using (var mark = new SqlCommand(
                "UPDATE dbo.HistoryInstanceState SET KeysPurged = 1 WHERE InstanceId = @instance", _connection, tx))
            {
                mark.Parameters.Add(InstanceParam());
                mark.ExecuteNonQuery();
            }
            tx.Commit();
            _registry.MarkPurged();
            return deleted;
        }
    }

    public HistoryLoadResult Load()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureSession();
            if (_fencing is not null)
                AcquireLease();

            var events = new List<HistoryEvent>();
            var validator = new HistoryRecordValidator();
            using var cmd = new SqlCommand(
                "SELECT Payload FROM dbo.HistoryEvents WHERE InstanceId = @instance ORDER BY N",
                _connection);
            cmd.Parameters.Add(InstanceParam());
            long ordinal = 0;
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    ordinal++;
                    events.Add(validator.Validate(OpenText(reader.GetString(0)), ordinal));
                }
            }

            _nextSequence = events.Count + 1;
            _loaded = true;
            _snapshotBuilder = HistorySnapshotCodec.Builder();
            foreach (var historyEvent in events)
                _snapshotBuilder.Apply(historyEvent);
            FoldRegistry(events);
            // A database history is transactional: no torn tail can exist.
            return new HistoryLoadResult(events, false, 0);
        }
    }

    /// <summary>
    /// Snapshot-aware load: validates the sealed snapshot, then reads and
    /// validates only the rows after its point. A corrupt snapshot is
    /// refused loudly — no silent fallback to a full replay.
    /// </summary>
    public HistorySnapshotLoadResult LoadFromSnapshot()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureSession();
            if (_fencing is not null)
                AcquireLease();

            string? snapshotPayload;
            using (var cmd = new SqlCommand(
                "SELECT Payload FROM dbo.HistorySnapshots WHERE InstanceId = @instance", _connection))
            {
                cmd.Parameters.Add(InstanceParam());
                snapshotPayload = cmd.ExecuteScalar() as string;
            }
            if (snapshotPayload is null)
            {
                var full = Load();
                return new HistorySnapshotLoadResult(full, false, 0);
            }

            var snapshot = HistorySnapshotCodec.Parse(OpenText(snapshotPayload), _instanceId);
            long tableEnd;
            using (var cmd = new SqlCommand(
                "SELECT ISNULL(MAX(N), 0) FROM dbo.HistoryEvents WHERE InstanceId = @instance", _connection))
            {
                cmd.Parameters.Add(InstanceParam());
                tableEnd = (long)cmd.ExecuteScalar();
            }
            if (tableEnd < snapshot.ThroughN)
                throw new HistoryCorruptionException(
                    $"snapshot: snapshot is ahead of history (snapshot through {snapshot.ThroughN}, history ends at {tableEnd})");

            var builder = HistorySnapshotCodec.BuilderFrom(snapshot);
            var events = builder.ToEvents();
            var validator = new HistoryRecordValidator();
            validator.SeedFromSnapshot(snapshot);
            using (var cmd = new SqlCommand(
                "SELECT Payload FROM dbo.HistoryEvents WHERE InstanceId = @instance AND N > @through ORDER BY N",
                _connection))
            {
                cmd.Parameters.Add(InstanceParam());
                cmd.Parameters.Add(new SqlParameter("@through", SqlDbType.BigInt) { Value = snapshot.ThroughN });
                using var reader = cmd.ExecuteReader();
                long ordinal = snapshot.ThroughN;
                while (reader.Read())
                {
                    ordinal++;
                    var tailEvent = validator.Validate(OpenText(reader.GetString(0)), ordinal);
                    events.Add(tailEvent);
                    builder.Apply(tailEvent);
                }
            }

            _snapshotBuilder = builder;
            _nextSequence = tableEnd + 1;
            _loaded = true;
            FoldRegistry(events);
            return new HistorySnapshotLoadResult(
                new HistoryLoadResult(events, false, 0), true, snapshot.ThroughN);
        }
    }

    public HistoryEvent Append(HistoryEvent historyEvent)
    {
        lock (_gate)
        {
            if (!_loaded)
                throw new InvalidOperationException("Load must be called before Append.");
            ThrowIfDisposed();
            if (historyEvent.InstanceId != _instanceId)
                throw new InvalidOperationException(
                    $"event belongs to instance '{historyEvent.InstanceId}', but this store owns instance '{_instanceId}'");
            using var appendSpan = EngineDiagnostics.StartStoreAppendSpan(
                "sqlserver", historyEvent.Type, historyEvent.InstanceId);

            var fields = (JsonObject)historyEvent.Fields.DeepClone();
            fields["n"] = _nextSequence;
            fields["sum"] = FileHistoryStore.Checksum(fields);
            string payload = SealText(fields.ToJsonString());
            var framed = new HistoryEvent(fields);
            bool tear = _faultInjector?.ShouldTear(framed) == true;

            using var tx = _connection!.BeginTransaction(IsolationLevel.Serializable);

            // Fencing first: a stale writer is refused before anything else
            // in the transaction reads or writes history.
            VerifyLeaseForAppend(tx);

            // The table must still end where this store's counter says: a
            // writer that bypassed the application lock and appended anyway
            // is detected here, and this append refuses over its work.
            using (var check = new SqlCommand(
                "SELECT ISNULL(MAX(N), 0) FROM dbo.HistoryEvents WHERE InstanceId = @instance",
                _connection, tx))
            {
                check.Parameters.Add(InstanceParam());
                long tableEnd = (long)check.ExecuteScalar();
                if (tableEnd != _nextSequence - 1)
                {
                    tx.Rollback();
                    throw new InvalidOperationException(
                        $"history for instance '{_instanceId}' changed under this store " +
                        $"(expected the table to end at event {_nextSequence - 1}, found {tableEnd}) — refusing to append");
                }
            }

            using (var insert = new SqlCommand(
                "INSERT INTO dbo.HistoryEvents (InstanceId, N, Payload) VALUES (@instance, @n, @payload)",
                _connection, tx))
            {
                insert.Parameters.Add(InstanceParam());
                insert.Parameters.Add(new SqlParameter("@n", SqlDbType.BigInt) { Value = _nextSequence });
                insert.Parameters.Add(new SqlParameter("@payload", SqlDbType.NVarChar, -1) { Value = payload });
                insert.ExecuteNonQuery();
            }

            WriteRegistryForAppend(tx, framed);

            // A snapshot due at this append is written in the same
            // transaction, so a committed history never points at a
            // snapshot its own append did not commit.
            HistorySnapshotBuilder? candidateBuilder = null;
            if (_snapshotInterval > 0 && _nextSequence % _snapshotInterval == 0)
            {
                candidateBuilder = _snapshotBuilder.CanSnapshot
                    ? HistorySnapshotCodec.BuilderFrom(_snapshotBuilder.Build())
                    : HistorySnapshotCodec.Builder();
                candidateBuilder.Apply(framed);
                if (candidateBuilder.CanSnapshot)
                {
                    using var upsert = new SqlCommand(
                        "MERGE dbo.HistorySnapshots AS target " +
                        "USING (SELECT @instance AS InstanceId) AS source ON target.InstanceId = source.InstanceId " +
                        "WHEN MATCHED THEN UPDATE SET ThroughN = @through, Payload = @payload " +
                        "WHEN NOT MATCHED THEN INSERT (InstanceId, ThroughN, Payload) VALUES (@instance, @through, @payload);",
                        _connection, tx);
                    upsert.Parameters.Add(InstanceParam());
                    upsert.Parameters.Add(new SqlParameter("@through", SqlDbType.BigInt) { Value = _nextSequence });
                    upsert.Parameters.Add(new SqlParameter("@payload", SqlDbType.NVarChar, -1)
                    { Value = SealText(HistorySnapshotCodec.Serialize(candidateBuilder.Build())) });
                    upsert.ExecuteNonQuery();
                }
            }

            if (tear)
            {
                // The harness kills the process inside OnTornWrite; the open
                // transaction dies with the connection and the server rolls
                // it back — the record was never committed, exactly the
                // file provider's torn-write contract. If control ever
                // returns, roll back explicitly and say so.
                _faultInjector!.OnTornWrite();
                tx.Rollback();
                throw new InvalidOperationException(
                    "Fault injector tore the write and returned; the record was not committed.");
            }

            tx.Commit();
            _nextSequence++;
            _registry.Apply(framed);
            if (candidateBuilder is not null)
                _snapshotBuilder = candidateBuilder;
            else
                _snapshotBuilder.Apply(framed);
            EngineDiagnostics.RecordStoreAppend("sqlserver", framed);
            return framed;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_connection is not null)
            {
                try
                {
                    if (_lockHeld)
                    {
                        using var cmd = new SqlCommand(
                            "EXEC sys.sp_releaseapplock @Resource = @resource, @LockOwner = 'Session'",
                            _connection);
                        cmd.Parameters.Add(ResourceParam());
                        cmd.ExecuteNonQuery();
                        _lockHeld = false;
                    }
                }
                catch (SqlException)
                {
                    // The session lock is released by the server when the
                    // connection closes either way; disposal must not throw.
                }
                _connection.Dispose();
                _connection = null;
            }
        }
    }

    /// <summary>
    /// Opens the store's dedicated connection, takes the per-instance session
    /// application lock, and creates the history table if it is missing.
    /// </summary>
    private void EnsureSession()
    {
        if (_connection is not null)
            return;

        _connection = new SqlConnection(_connectionString);
        _connection.Open();

        // Single-writer ownership: an exclusive application lock, held by
        // this session for the store's lifetime. A short grace window covers
        // the server reaping a killed predecessor's session; a live owner's
        // lock outlasts the window and this store is refused. With fencing
        // on, the lease + token are the concurrency authority instead, and
        // no session lock is taken (see FencingOptions).
        var deadline = DateTime.UtcNow + LockGraceWindow;
        while (_fencing is null)
        {
            int rc;
            using (var cmd = new SqlCommand(
                "DECLARE @rc INT; EXEC @rc = sys.sp_getapplock " +
                "@Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @rc;",
                _connection))
            {
                cmd.Parameters.Add(ResourceParam());
                rc = (int)cmd.ExecuteScalar();
            }
            if (rc >= 0)
            {
                _lockHeld = true;
                break;
            }
            if (DateTime.UtcNow >= deadline)
            {
                _connection.Dispose();
                _connection = null;
                throw new StateOwnedByAnotherHostException(
                    $"history of instance '{_instanceId}' is owned by another running store — refusing to load (single-writer)");
            }
            Thread.Sleep(100);
        }

        if (!_schemaReady)
        {
            // Schema creation is guarded by its own transactional lock so
            // two stores for different instances can share one database.
            using var tx = _connection.BeginTransaction();
            using (var guard = new SqlCommand(
                "EXEC sys.sp_getapplock @Resource = 'DurableExecution.History/Schema', " +
                "@LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 30000",
                _connection, tx))
            {
                guard.ExecuteNonQuery();
            }
            using (var create = new SqlCommand(
                "IF OBJECT_ID(N'dbo.HistoryEvents', N'U') IS NULL " +
                "CREATE TABLE dbo.HistoryEvents (" +
                "  InstanceId NVARCHAR(128) NOT NULL," +
                "  N BIGINT NOT NULL," +
                "  Payload NVARCHAR(MAX) NOT NULL," +
                "  CONSTRAINT PK_HistoryEvents PRIMARY KEY (InstanceId, N));",
                _connection, tx))
            {
                create.ExecuteNonQuery();
            }
            using (var createSnapshots = new SqlCommand(
                "IF OBJECT_ID(N'dbo.HistorySnapshots', N'U') IS NULL " +
                "CREATE TABLE dbo.HistorySnapshots (" +
                "  InstanceId NVARCHAR(128) NOT NULL," +
                "  ThroughN BIGINT NOT NULL," +
                "  Payload NVARCHAR(MAX) NOT NULL," +
                "  CONSTRAINT PK_HistorySnapshots PRIMARY KEY (InstanceId));",
                _connection, tx))
            {
                createSnapshots.ExecuteNonQuery();
            }
            using (var createKeys = new SqlCommand(
                "IF OBJECT_ID(N'dbo.HistoryKeys', N'U') IS NULL " +
                "CREATE TABLE dbo.HistoryKeys (" +
                "  InstanceId NVARCHAR(128) NOT NULL," +
                "  Seq BIGINT NOT NULL," +
                "  IdempotencyKey NVARCHAR(64) NOT NULL," +
                "  CONSTRAINT PK_HistoryKeys PRIMARY KEY (InstanceId, Seq));",
                _connection, tx))
            {
                createKeys.ExecuteNonQuery();
            }
            using (var createState = new SqlCommand(
                "IF OBJECT_ID(N'dbo.HistoryInstanceState', N'U') IS NULL " +
                "CREATE TABLE dbo.HistoryInstanceState (" +
                "  InstanceId NVARCHAR(128) NOT NULL," +
                "  CompletedAtUtc DATETIMEOFFSET NULL," +
                "  KeysPurged BIT NOT NULL CONSTRAINT DF_HistoryInstanceState_KeysPurged DEFAULT 0," +
                "  CONSTRAINT PK_HistoryInstanceState PRIMARY KEY (InstanceId));",
                _connection, tx))
            {
                createState.ExecuteNonQuery();
            }
            using (var createLeases = new SqlCommand(
                "IF OBJECT_ID(N'dbo.HistoryLeases', N'U') IS NULL " +
                "CREATE TABLE dbo.HistoryLeases (" +
                "  InstanceId NVARCHAR(128) NOT NULL," +
                "  OwnerId NVARCHAR(128) NOT NULL," +
                "  FencingToken BIGINT NOT NULL," +
                "  ExpiresAtUtc DATETIMEOFFSET NOT NULL," +
                "  CONSTRAINT PK_HistoryLeases PRIMARY KEY (InstanceId));",
                _connection, tx))
            {
                createLeases.ExecuteNonQuery();
            }
            tx.Commit();
            _schemaReady = true;
        }
    }

    private SqlParameter InstanceParam() =>
        new("@instance", SqlDbType.NVarChar, 128) { Value = _instanceId };

    private SqlParameter ResourceParam() =>
        new("@resource", SqlDbType.NVarChar, 255) { Value = $"DurableExecution.History/{_instanceId}" };

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SqlHistoryStore));
    }
}
