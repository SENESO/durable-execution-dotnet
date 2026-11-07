using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
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
public sealed class PostgresHistoryStore : IHistoryStore, IHistorySnapshotStore, IDisposable
{
    private static readonly TimeSpan LockGraceWindow = TimeSpan.FromSeconds(2);

    private readonly string _connectionString;
    private readonly string _instanceId;
    private readonly IHistoryFaultInjector? _faultInjector;
    private readonly int _snapshotInterval;
    private HistorySnapshotBuilder _snapshotBuilder = HistorySnapshotCodec.Builder();
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
        int snapshotInterval = 50 /* FileHistoryStore.DefaultSnapshotInterval */)
    {
        if (snapshotInterval < 0)
            throw new ArgumentOutOfRangeException(nameof(snapshotInterval), "snapshot interval cannot be negative");
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _instanceId = string.IsNullOrEmpty(instanceId)
            ? throw new ArgumentException("instance id must be a non-empty string", nameof(instanceId))
            : instanceId;
        _faultInjector = faultInjector;
        _snapshotInterval = snapshotInterval;
    }

    public HistoryLoadResult Load()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureSession();

            var events = new List<HistoryEvent>();
            var validator = new HistoryRecordValidator();
            using var cmd = new NpgsqlCommand(
                "SELECT payload FROM public.history_events WHERE instance_id = @instance ORDER BY n",
                _connection);
            cmd.Parameters.Add(InstanceParam());
            using var reader = cmd.ExecuteReader();
            long ordinal = 0;
            while (reader.Read())
            {
                ordinal++;
                events.Add(validator.Validate(reader.GetString(0), ordinal));
            }

            _nextSequence = events.Count + 1;
            _loaded = true;
            _snapshotBuilder = HistorySnapshotCodec.Builder();
            foreach (var historyEvent in events)
                _snapshotBuilder.Apply(historyEvent);
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

            var snapshot = HistorySnapshotCodec.Parse(snapshotPayload, _instanceId);
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
                    var tailEvent = validator.Validate(reader.GetString(0), ordinal);
                    events.Add(tailEvent);
                    builder.Apply(tailEvent);
                }
            }

            _snapshotBuilder = builder;
            _nextSequence = tableEnd + 1;
            _loaded = true;
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

            var fields = (JsonObject)historyEvent.Fields.DeepClone();
            fields["n"] = _nextSequence;
            fields["sum"] = FileHistoryStore.Checksum(fields);
            string payload = fields.ToJsonString();
            var framed = new HistoryEvent(fields);
            bool tear = _faultInjector?.ShouldTear(framed) == true;

            using var tx = _connection!.BeginTransaction(IsolationLevel.Serializable);

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
                    { Value = HistorySnapshotCodec.Serialize(candidateBuilder.Build()) });
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
            if (candidateBuilder is not null)
                _snapshotBuilder = candidateBuilder;
            else
                _snapshotBuilder.Apply(framed);
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

        var deadline = DateTime.UtcNow + LockGraceWindow;
        while (true)
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
