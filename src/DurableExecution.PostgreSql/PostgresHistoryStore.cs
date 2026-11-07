using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DurableExecution.Diagnostics;
using DurableExecution.Execution;
using DurableExecution.History;
using Npgsql;

namespace DurableExecution.PostgreSql;

/// <summary>
/// PostgreSQL provider for <see cref="IHistoryStore"/> (foundation §6.2, §6.5):
/// one instance's append-only history as rows of <c>public.history_events</c>
/// in a database the caller provisions. Each row carries the identical sealed
/// JSON payload the file provider frames — same fields, same checksum — and
/// load runs the same structural validation
/// (<see cref="HistoryRecordValidator"/>), so the same damage is refused with
/// the same words on every medium.
///
/// Checkpoint atomicity (§6.2): an append is one serializable transaction —
/// verify the table still ends where this store's counter says, insert,
/// commit. A crash mid-append leaves the whole record or nothing; there is no
/// torn state, so <see cref="HistoryLoadResult.TornTailTruncated"/> is always
/// false here.
///
/// Single-writer (§9, in-database analogue of the host's OS lock): the store
/// holds one dedicated connection and takes a session advisory lock
/// (<c>pg_advisory_lock</c>) at load, keyed by a stable 64-bit hash of the
/// per-instance lock resource. A second store for the same instance in the
/// same database is refused with
/// <see cref="StateOwnedByAnotherHostException"/> after a short grace window
/// covering the server reaping a killed owner's session. Disposal releases
/// the advisory lock and the connection.
///
/// Limits, stated plainly: the advisory lock is cooperative, and its key is a
/// hash — a different resource could theoretically collide on the same 64-bit
/// key, in which case the append transaction's end-of-history check still
/// refuses to append over a history that moved. Nothing here fences writers
/// across databases or machines. Cross-device fencing is P3; this provider
/// does not fake it.
/// </summary>
public sealed class PostgresHistoryStore : IHistoryStore, IHistorySnapshotStore, IHistoryKeyRetention, IDisposable
{
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

    private NpgsqlConnection? _connection;
    private bool _lockHeld;
    private bool _schemaReady;
    private bool _loaded;
    private bool _disposed;
    private long _nextSequence = 1;

    /// <param name="connectionString">Connection string for the database that holds this history. The database must already exist; the store creates its table.</param>
    /// <param name="instanceId">The workflow instance whose history this store owns.</param>
    /// <param name="faultInjector">Crash-injection seam for the fault suite; production hosts attach none.</param>
    public PostgresHistoryStore(string connectionString, string instanceId, IHistoryFaultInjector? faultInjector = null,
        int snapshotInterval = 50 /* FileHistoryStore.DefaultSnapshotInterval */, HistoryEncryption? encryption = null,
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

    // ---- P3 fencing: leases with a monotonic token (public.history_leases) ----

    /// <summary>
    /// Acquires (or renews) this store's lease at load: a free or expired
    /// lease is taken with the token incremented (a lapsed lease of my own
    /// reissues at token+1 too, so a zombie store of mine holding the old
    /// token stays fenced out); my unexpired lease renews at the same
    /// token; another owner's unexpired lease refuses the load. All
    /// timestamps are the server's (now()).
    /// </summary>
    private void AcquireLease()
    {
        using var tx = _connection!.BeginTransaction();
        string? owner = null;
        long token = 0;
        DateTimeOffset expires = default;
        bool found = false;
        using (var select = new NpgsqlCommand(
            "SELECT owner_id, fencing_token, expires_at_utc FROM public.history_leases " +
            "WHERE instance_id = @instance FOR UPDATE", _connection, tx))
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
        using (var nowCmd = new NpgsqlCommand("SELECT now()", _connection, tx))
        using (var nowReader = nowCmd.ExecuteReader())
        {
            nowReader.Read();
            now = nowReader.GetFieldValue<DateTimeOffset>(0);
        }
        var newExpiry = new NpgsqlParameter("@expiry", NpgsqlTypes.NpgsqlDbType.TimestampTz)
        { Value = now + _fencing!.LeaseDuration };

        if (!found)
        {
            using var insert = new NpgsqlCommand(
                "INSERT INTO public.history_leases (instance_id, owner_id, fencing_token, expires_at_utc) " +
                "VALUES (@instance, @owner, 1, @expiry)", _connection, tx);
            insert.Parameters.Add(InstanceParam());
            insert.Parameters.Add(new NpgsqlParameter("@owner", NpgsqlTypes.NpgsqlDbType.Varchar, 128) { Value = _fencingOwnerId! });
            insert.Parameters.Add(newExpiry);
            insert.ExecuteNonQuery();
            FencingToken = 1;
        }
        else if (expires <= now)
        {
            using var update = new NpgsqlCommand(
                "UPDATE public.history_leases SET owner_id = @owner, fencing_token = @token, expires_at_utc = @expiry " +
                "WHERE instance_id = @instance", _connection, tx);
            update.Parameters.Add(InstanceParam());
            update.Parameters.Add(new NpgsqlParameter("@owner", NpgsqlTypes.NpgsqlDbType.Varchar, 128) { Value = _fencingOwnerId! });
            update.Parameters.Add(new NpgsqlParameter("@token", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = token + 1 });
            update.Parameters.Add(newExpiry);
            update.ExecuteNonQuery();
            FencingToken = token + 1;
        }
        else if (owner == _fencingOwnerId)
        {
            using var renew = new NpgsqlCommand(
                "UPDATE public.history_leases SET expires_at_utc = @expiry WHERE instance_id = @instance",
                _connection, tx);
            renew.Parameters.Add(InstanceParam());
            renew.Parameters.Add(newExpiry);
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
    private void VerifyLeaseForAppend(NpgsqlTransaction tx)
    {
        if (_fencing is null)
            return;
        int rows;
        using (var check = new NpgsqlCommand(
            "UPDATE public.history_leases SET expires_at_utc = expires_at_utc " +
            "WHERE instance_id = @instance AND owner_id = @owner AND fencing_token = @token " +
            "AND expires_at_utc > now()", _connection, tx))
        {
            check.Parameters.Add(InstanceParam());
            check.Parameters.Add(new NpgsqlParameter("@owner", NpgsqlTypes.NpgsqlDbType.Varchar, 128) { Value = _fencingOwnerId! });
            check.Parameters.Add(new NpgsqlParameter("@token", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = FencingToken!.Value });
            rows = check.ExecuteNonQuery();
        }
        if (rows == 1)
            return;

        string currentState = "the lease row is gone";
        using (var select = new NpgsqlCommand(
            "SELECT owner_id, fencing_token, expires_at_utc FROM public.history_leases WHERE instance_id = @instance",
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
        using (var cmd = new NpgsqlCommand(
            "SELECT seq, idempotency_key FROM public.history_keys WHERE instance_id = @instance", _connection))
        {
            cmd.Parameters.Add(InstanceParam());
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                keys[reader.GetInt64(0)] = reader.GetString(1);
        }
        using (var cmd = new NpgsqlCommand(
            "SELECT completed_at_utc, keys_purged FROM public.history_instance_state WHERE instance_id = @instance", _connection))
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
    private void WriteRegistryState(NpgsqlTransaction tx)
    {
        foreach (var kv in _registry.Keys)
        {
            using var upsert = new NpgsqlCommand(
                "INSERT INTO public.history_keys (instance_id, seq, idempotency_key) VALUES (@instance, @seq, @key) " +
                "ON CONFLICT (instance_id, seq) DO UPDATE SET idempotency_key = EXCLUDED.idempotency_key",
                _connection, tx);
            upsert.Parameters.Add(InstanceParam());
            upsert.Parameters.Add(new NpgsqlParameter("@seq", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = kv.Key });
            upsert.Parameters.Add(new NpgsqlParameter("@key", NpgsqlTypes.NpgsqlDbType.Varchar, 64) { Value = kv.Value });
            upsert.ExecuteNonQuery();
        }
        using var upsertState = new NpgsqlCommand(
            "INSERT INTO public.history_instance_state (instance_id, completed_at_utc, keys_purged) VALUES (@instance, @completed, @purged) " +
            "ON CONFLICT (instance_id) DO UPDATE SET completed_at_utc = EXCLUDED.completed_at_utc, keys_purged = EXCLUDED.keys_purged",
            _connection, tx);
        upsertState.Parameters.Add(InstanceParam());
        upsertState.Parameters.Add(new NpgsqlParameter("@completed", NpgsqlTypes.NpgsqlDbType.TimestampTz)
        { Value = _registry.CompletedAtUtc is null ? DBNull.Value : _registry.CompletedAtUtc.Value });
        upsertState.Parameters.Add(new NpgsqlParameter("@purged", NpgsqlTypes.NpgsqlDbType.Boolean) { Value = _registry.KeysPurged });
        upsertState.ExecuteNonQuery();
    }

    /// <summary>
    /// The registry side of one append, inside the append's own
    /// transaction: a scheduled command's key and a completion's
    /// timestamp become registry rows in the same commit as the record.
    /// </summary>
    private void WriteRegistryForAppend(NpgsqlTransaction tx, HistoryEvent framed)
    {
        if (framed.Type == HistoryEventTypes.ActivityScheduled)
        {
            using var upsert = new NpgsqlCommand(
                "INSERT INTO public.history_keys (instance_id, seq, idempotency_key) VALUES (@instance, @seq, @key) " +
                "ON CONFLICT (instance_id, seq) DO UPDATE SET idempotency_key = EXCLUDED.idempotency_key",
                _connection, tx);
            upsert.Parameters.Add(InstanceParam());
            upsert.Parameters.Add(new NpgsqlParameter("@seq", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = framed.CommandSequence!.Value });
            upsert.Parameters.Add(new NpgsqlParameter("@key", NpgsqlTypes.NpgsqlDbType.Varchar, 64) { Value = framed.GetString("idempotencyKey")! });
            upsert.ExecuteNonQuery();
        }
        else if (framed.Type == HistoryEventTypes.WorkflowCompleted)
        {
            using var upsertState = new NpgsqlCommand(
                "INSERT INTO public.history_instance_state (instance_id, completed_at_utc, keys_purged) VALUES (@instance, @completed, FALSE) " +
                "ON CONFLICT (instance_id) DO UPDATE SET completed_at_utc = EXCLUDED.completed_at_utc",
                _connection, tx);
            upsertState.Parameters.Add(InstanceParam());
            upsertState.Parameters.Add(new NpgsqlParameter("@completed", NpgsqlTypes.NpgsqlDbType.TimestampTz)
            { Value = DateTimeOffset.Parse(framed.GetString("ts")!) });
            upsertState.ExecuteNonQuery();
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
            using (var delete = new NpgsqlCommand(
                "DELETE FROM public.history_keys WHERE instance_id = @instance", _connection, tx))
            {
                delete.Parameters.Add(InstanceParam());
                deleted = delete.ExecuteNonQuery();
            }
            using (var mark = new NpgsqlCommand(
                "UPDATE public.history_instance_state SET keys_purged = TRUE WHERE instance_id = @instance", _connection, tx))
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
            using var cmd = new NpgsqlCommand(
                "SELECT payload FROM public.history_events WHERE instance_id = @instance ORDER BY n",
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
            using (var cmd = new NpgsqlCommand(
                "SELECT payload FROM public.history_snapshots WHERE instance_id = @instance", _connection))
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
            using (var cmd = new NpgsqlCommand(
                "SELECT COALESCE(MAX(n), 0) FROM public.history_events WHERE instance_id = @instance", _connection))
            {
                cmd.Parameters.Add(InstanceParam());
                tableEnd = (long)cmd.ExecuteScalar()!;
            }
            if (tableEnd < snapshot.ThroughN)
                throw new HistoryCorruptionException(
                    $"snapshot: snapshot is ahead of history (snapshot through {snapshot.ThroughN}, history ends at {tableEnd})");

            var builder = HistorySnapshotCodec.BuilderFrom(snapshot);
            var events = builder.ToEvents();
            var validator = new HistoryRecordValidator();
            validator.SeedFromSnapshot(snapshot);
            using (var cmd = new NpgsqlCommand(
                "SELECT payload FROM public.history_events WHERE instance_id = @instance AND n > @through ORDER BY n",
                _connection))
            {
                cmd.Parameters.Add(InstanceParam());
                cmd.Parameters.Add(new NpgsqlParameter("@through", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = snapshot.ThroughN });
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
                "postgresql", historyEvent.Type, historyEvent.InstanceId);

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

            using (var check = new NpgsqlCommand(
                "SELECT COALESCE(MAX(n), 0) FROM public.history_events WHERE instance_id = @instance",
                _connection, tx))
            {
                check.Parameters.Add(InstanceParam());
                long tableEnd = (long)check.ExecuteScalar()!;
                if (tableEnd != _nextSequence - 1)
                {
                    tx.Rollback();
                    throw new InvalidOperationException(
                        $"history for instance '{_instanceId}' changed under this store " +
                        $"(expected the table to end at event {_nextSequence - 1}, found {tableEnd}) — refusing to append");
                }
            }

            using (var insert = new NpgsqlCommand(
                "INSERT INTO public.history_events (instance_id, n, payload) VALUES (@instance, @n, @payload)",
                _connection, tx))
            {
                insert.Parameters.Add(InstanceParam());
                insert.Parameters.Add(new NpgsqlParameter("@n", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = _nextSequence });
                insert.Parameters.Add(new NpgsqlParameter("@payload", NpgsqlTypes.NpgsqlDbType.Text) { Value = payload });
                insert.ExecuteNonQuery();
            }

            WriteRegistryForAppend(tx, framed);

            HistorySnapshotBuilder? candidateBuilder = null;
            if (_snapshotInterval > 0 && _nextSequence % _snapshotInterval == 0)
            {
                candidateBuilder = _snapshotBuilder.CanSnapshot
                    ? HistorySnapshotCodec.BuilderFrom(_snapshotBuilder.Build())
                    : HistorySnapshotCodec.Builder();
                candidateBuilder.Apply(framed);
                if (candidateBuilder.CanSnapshot)
                {
                    using var upsert = new NpgsqlCommand(
                        "INSERT INTO public.history_snapshots (instance_id, through_n, payload) VALUES (@instance, @through, @payload) " +
                        "ON CONFLICT (instance_id) DO UPDATE SET through_n = EXCLUDED.through_n, payload = EXCLUDED.payload",
                        _connection, tx);
                    upsert.Parameters.Add(InstanceParam());
                    upsert.Parameters.Add(new NpgsqlParameter("@through", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = _nextSequence });
                    upsert.Parameters.Add(new NpgsqlParameter("@payload", NpgsqlTypes.NpgsqlDbType.Text)
                    { Value = SealText(HistorySnapshotCodec.Serialize(candidateBuilder.Build())) });
                    upsert.ExecuteNonQuery();
                }
            }

            if (tear)
            {
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
            EngineDiagnostics.RecordStoreAppend("postgresql", framed);
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
                        using var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", _connection);
                        cmd.Parameters.Add(new NpgsqlParameter("@key", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = LockKey() });
                        cmd.ExecuteNonQuery();
                        _lockHeld = false;
                    }
                }
                catch (NpgsqlException)
                {
                    // The session lock dies with the session either way.
                }
                _connection.Dispose();
                _connection = null;
            }
        }
    }

    private void EnsureSession()
    {
        if (_connection is not null)
            return;

        _connection = new NpgsqlConnection(_connectionString);
        _connection.Open();

        // Session advisory lock = single-writer ownership — skipped when
        // fencing is on: the lease + token are the authority instead.
        var deadline = DateTime.UtcNow + LockGraceWindow;
        while (_fencing is null)
        {
            bool acquired;
            using (var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", _connection))
            {
                cmd.Parameters.Add(new NpgsqlParameter("@key", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = LockKey() });
                acquired = (bool)cmd.ExecuteScalar()!;
            }
            if (acquired)
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
            using var tx = _connection.BeginTransaction();
            using (var guard = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", _connection, tx))
            {
                guard.Parameters.Add(new NpgsqlParameter("@key", NpgsqlTypes.NpgsqlDbType.Bigint)
                {
                    Value = BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes("DurableExecution.History/Schema")), 0),
                });
                guard.ExecuteScalar();
            }
            using (var create = new NpgsqlCommand(
                "CREATE TABLE IF NOT EXISTS public.history_events (" +
                "  instance_id VARCHAR(128) NOT NULL," +
                "  n BIGINT NOT NULL," +
                "  payload TEXT NOT NULL," +
                "  PRIMARY KEY (instance_id, n))",
                _connection, tx))
            {
                create.ExecuteNonQuery();
            }
            using (var createSnapshots = new NpgsqlCommand(
                "CREATE TABLE IF NOT EXISTS public.history_snapshots (" +
                "  instance_id VARCHAR(128) NOT NULL PRIMARY KEY," +
                "  through_n BIGINT NOT NULL," +
                "  payload TEXT NOT NULL)",
                _connection, tx))
            {
                createSnapshots.ExecuteNonQuery();
            }
            using (var createKeys = new NpgsqlCommand(
                "CREATE TABLE IF NOT EXISTS public.history_keys (" +
                "  instance_id VARCHAR(128) NOT NULL," +
                "  seq BIGINT NOT NULL," +
                "  idempotency_key VARCHAR(64) NOT NULL," +
                "  PRIMARY KEY (instance_id, seq))",
                _connection, tx))
            {
                createKeys.ExecuteNonQuery();
            }
            using (var createState = new NpgsqlCommand(
                "CREATE TABLE IF NOT EXISTS public.history_instance_state (" +
                "  instance_id VARCHAR(128) NOT NULL PRIMARY KEY," +
                "  completed_at_utc TIMESTAMPTZ NULL," +
                "  keys_purged BOOLEAN NOT NULL DEFAULT FALSE)",
                _connection, tx))
            {
                createState.ExecuteNonQuery();
            }
            using (var createLeases = new NpgsqlCommand(
                "CREATE TABLE IF NOT EXISTS public.history_leases (" +
                "  instance_id VARCHAR(128) NOT NULL PRIMARY KEY," +
                "  owner_id VARCHAR(128) NOT NULL," +
                "  fencing_token BIGINT NOT NULL," +
                "  expires_at_utc TIMESTAMPTZ NOT NULL)",
                _connection, tx))
            {
                createLeases.ExecuteNonQuery();
            }
            tx.Commit();
            _schemaReady = true;
        }
    }

    private long LockKey() =>
        BitConverter.ToInt64(
            SHA256.HashData(Encoding.UTF8.GetBytes($"DurableExecution.History/{_instanceId}")), 0);

    private NpgsqlParameter InstanceParam() =>
        new("@instance", NpgsqlTypes.NpgsqlDbType.Varchar, 128) { Value = _instanceId };

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PostgresHistoryStore));
    }
}
