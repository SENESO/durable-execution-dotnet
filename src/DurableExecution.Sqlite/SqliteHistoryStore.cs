using System.Data;
using System.Text.Json.Nodes;
using DurableExecution.Execution;
using DurableExecution.History;
using Microsoft.Data.Sqlite;

namespace DurableExecution.Sqlite;

/// <summary>
/// SQLite provider for <see cref="IHistoryStore"/> (foundation §6.2, §6.5):
/// the embedded development provider. One instance's append-only history is
/// rows of <c>HistoryEvents</c> in a database file inside the instance's state
/// directory. Each row carries the identical sealed JSON payload the file
/// provider frames — same fields, same checksum — and load runs the same
/// structural validation (<see cref="HistoryRecordValidator"/>).
///
/// Checkpoint atomicity (§6.2): an append is one transaction against a
/// database running with <c>synchronous = FULL</c>; a crash mid-append leaves
/// the whole record or nothing, so
/// <see cref="HistoryLoadResult.TornTailTruncated"/> is always false here.
///
/// Single-writer (§9): the store holds an OS lock file beside the database
/// from its first load until disposal — the same mechanism as the host's own
/// ownership lock — so a second live store over the same state directory is
/// refused with <see cref="StateOwnedByAnotherHostException"/>. Disposal
/// releases the lock. This is a local embedded store: it deliberately offers
/// no multi-process or cross-machine story beyond that lock.
/// </summary>
public sealed class SqliteHistoryStore : IHistoryStore, IHistorySnapshotStore, IDisposable
{
    private readonly string _databasePath;
    private readonly string _lockPath;
    private readonly IHistoryFaultInjector? _faultInjector;
    private readonly int _snapshotInterval;
    private HistorySnapshotBuilder _snapshotBuilder = HistorySnapshotCodec.Builder();
    private readonly object _gate = new();

    private SqliteConnection? _connection;
    private FileStream? _ownershipLock;
    private bool _loaded;
    private bool _disposed;
    private long _nextSequence = 1;

    /// <param name="stateDirectory">The instance's state directory; the database file lives there.</param>
    /// <param name="faultInjector">Crash-injection seam for the fault suite; production hosts attach none.</param>
    public SqliteHistoryStore(string stateDirectory, IHistoryFaultInjector? faultInjector = null,
        int snapshotInterval = 50 /* FileHistoryStore.DefaultSnapshotInterval */)
    {
        if (snapshotInterval < 0)
            throw new ArgumentOutOfRangeException(nameof(snapshotInterval), "snapshot interval cannot be negative");
        _snapshotInterval = snapshotInterval;
        Directory.CreateDirectory(stateDirectory);
        _databasePath = Path.Combine(stateDirectory, "history.db");
        _lockPath = Path.Combine(stateDirectory, "history.db.lock");
        _faultInjector = faultInjector;
    }

    public string DatabasePath => _databasePath;

    public HistoryLoadResult Load()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureSession();

            var events = new List<HistoryEvent>();
            var validator = new HistoryRecordValidator();
            using var cmd = new SqliteCommand(
                "SELECT Payload FROM HistoryEvents ORDER BY N", _connection);
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
            using (var cmd = new SqliteCommand("SELECT Payload FROM HistorySnapshots WHERE Id = 1", _connection))
                snapshotPayload = cmd.ExecuteScalar() as string;
            if (snapshotPayload is null)
            {
                var full = Load();
                return new HistorySnapshotLoadResult(full, false, 0);
            }

            var snapshot = HistorySnapshotCodec.Parse(snapshotPayload);
            long tableEnd;
            using (var cmd = new SqliteCommand("SELECT COALESCE(MAX(N), 0) FROM HistoryEvents", _connection))
                tableEnd = (long)cmd.ExecuteScalar()!;
            if (tableEnd < snapshot.ThroughN)
                throw new HistoryCorruptionException(
                    $"snapshot: snapshot is ahead of history (snapshot through {snapshot.ThroughN}, history ends at {tableEnd})");

            var builder = HistorySnapshotCodec.BuilderFrom(snapshot);
            var events = builder.ToEvents();
            var validator = new HistoryRecordValidator();
            validator.SeedFromSnapshot(snapshot);
            using (var cmd = new SqliteCommand(
                "SELECT Payload FROM HistoryEvents WHERE N > @through ORDER BY N", _connection))
            {
                cmd.Parameters.Add(new SqliteParameter("@through", SqliteType.Integer) { Value = snapshot.ThroughN });
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

            var fields = (JsonObject)historyEvent.Fields.DeepClone();
            fields["n"] = _nextSequence;
            fields["sum"] = FileHistoryStore.Checksum(fields);
            string payload = fields.ToJsonString();
            var framed = new HistoryEvent(fields);
            bool tear = _faultInjector?.ShouldTear(framed) == true;

            using var tx = _connection!.BeginTransaction(deferred: false);

            using (var check = new SqliteCommand(
                "SELECT COALESCE(MAX(N), 0) FROM HistoryEvents", _connection, tx))
            {
                long tableEnd = (long)check.ExecuteScalar()!;
                if (tableEnd != _nextSequence - 1)
                {
                    tx.Rollback();
                    throw new InvalidOperationException(
                        $"history changed under this store (expected the table to end at event " +
                        $"{_nextSequence - 1}, found {tableEnd}) — refusing to append");
                }
            }

            using (var insert = new SqliteCommand(
                "INSERT INTO HistoryEvents (N, Payload) VALUES (@n, @payload)", _connection, tx))
            {
                insert.Parameters.Add(new SqliteParameter("@n", SqliteType.Integer) { Value = _nextSequence });
                insert.Parameters.Add(new SqliteParameter("@payload", SqliteType.Text) { Value = payload });
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
                    using var upsert = new SqliteCommand(
                        "INSERT INTO HistorySnapshots (Id, ThroughN, Payload) VALUES (1, @through, @payload) " +
                        "ON CONFLICT(Id) DO UPDATE SET ThroughN = excluded.ThroughN, Payload = excluded.Payload",
                        _connection, tx);
                    upsert.Parameters.Add(new SqliteParameter("@through", SqliteType.Integer) { Value = _nextSequence });
                    upsert.Parameters.Add(new SqliteParameter("@payload", SqliteType.Text)
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
            _connection?.Dispose();
            _connection = null;
            _ownershipLock?.Dispose();
            _ownershipLock = null;
        }
    }

    private void EnsureSession()
    {
        if (_connection is not null)
            return;

        try
        {
            _ownershipLock = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new StateOwnedByAnotherHostException(
                "history store is owned by another running store — refusing to load (single-writer)");
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
        };
        _connection = new SqliteConnection(builder.ConnectionString);
        _connection.Open();

        using (var pragma = new SqliteCommand(
            "PRAGMA synchronous = FULL; PRAGMA foreign_keys = ON;", _connection))
            pragma.ExecuteNonQuery();
        using var create = new SqliteCommand(
            "CREATE TABLE IF NOT EXISTS HistoryEvents (" +
            "  N INTEGER NOT NULL PRIMARY KEY," +
            "  Payload TEXT NOT NULL);",
            _connection);
        create.ExecuteNonQuery();
        using var createSnapshots = new SqliteCommand(
            "CREATE TABLE IF NOT EXISTS HistorySnapshots (" +
            "  Id INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1)," +
            "  ThroughN INTEGER NOT NULL," +
            "  Payload TEXT NOT NULL);",
            _connection);
        createSnapshots.ExecuteNonQuery();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SqliteHistoryStore));
    }
}
