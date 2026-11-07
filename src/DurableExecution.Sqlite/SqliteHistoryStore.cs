using System.Data;
using System.Text.Json.Nodes;
using DurableExecution.Diagnostics;
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
public sealed class SqliteHistoryStore : IHistoryStore, IHistorySnapshotStore, IHistoryKeyRetention, IDisposable
{
    private readonly string _databasePath;
    private readonly string _lockPath;
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

    private SqliteConnection? _connection;
    private FileStream? _ownershipLock;
    private bool _loaded;
    private bool _disposed;
    private long _nextSequence = 1;

    /// <param name="stateDirectory">The instance's state directory; the database file lives there.</param>
    /// <param name="faultInjector">Crash-injection seam for the fault suite; production hosts attach none.</param>
    public SqliteHistoryStore(string stateDirectory, IHistoryFaultInjector? faultInjector = null,
        int snapshotInterval = 50 /* FileHistoryStore.DefaultSnapshotInterval */, HistoryEncryption? encryption = null,
        KeyRetentionPolicy? keyRetention = null, FencingOptions? fencing = null)
    {
        if (snapshotInterval < 0)
            throw new ArgumentOutOfRangeException(nameof(snapshotInterval), "snapshot interval cannot be negative");
        _snapshotInterval = snapshotInterval;
        Directory.CreateDirectory(stateDirectory);
        _databasePath = Path.Combine(stateDirectory, "history.db");
        _lockPath = Path.Combine(stateDirectory, "history.db.lock");
        _faultInjector = faultInjector;
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

    // ---- P3 fencing: the lease row inside history.db ----

    /// <summary>
    /// Acquires (or renews) this store's lease at load, in one immediate
    /// transaction: a free or expired lease is taken with the token
    /// incremented (a lapsed lease of my own reissues at token+1 too);
    /// my unexpired lease renews at the same token; another owner's
    /// unexpired lease refuses the load. SQLite is single-machine, so
    /// the clock is the machine clock.
    /// </summary>
    private void AcquireLease()
    {
        EnsureSession();
        using var tx = _connection!.BeginTransaction();
        string? owner = null;
        long token = 0;
        DateTimeOffset expires = default;
        bool found = false;
        using (var select = new SqliteCommand(
            "SELECT OwnerId, FencingToken, ExpiresAtUtc FROM HistoryLeases WHERE Id = 1", _connection, tx))
        using (var reader = select.ExecuteReader())
            if (reader.Read())
            {
                found = true;
                owner = reader.GetString(0);
                token = reader.GetInt64(1);
                expires = DateTimeOffset.Parse(reader.GetString(2));
            }
        var now = DateTimeOffset.UtcNow;
        string newExpiry = (now + _fencing!.LeaseDuration).ToString("o");

        if (!found)
        {
            using var insert = new SqliteCommand(
                "INSERT INTO HistoryLeases (Id, OwnerId, FencingToken, ExpiresAtUtc) VALUES (1, @owner, 1, @expiry)",
                _connection, tx);
            insert.Parameters.AddWithValue("@owner", _fencingOwnerId!);
            insert.Parameters.AddWithValue("@expiry", newExpiry);
            insert.ExecuteNonQuery();
            FencingToken = 1;
        }
        else if (expires <= now)
        {
            using var update = new SqliteCommand(
                "UPDATE HistoryLeases SET OwnerId = @owner, FencingToken = @token, ExpiresAtUtc = @expiry WHERE Id = 1",
                _connection, tx);
            update.Parameters.AddWithValue("@owner", _fencingOwnerId!);
            update.Parameters.AddWithValue("@token", token + 1);
            update.Parameters.AddWithValue("@expiry", newExpiry);
            update.ExecuteNonQuery();
            FencingToken = token + 1;
        }
        else if (owner == _fencingOwnerId)
        {
            using var renew = new SqliteCommand(
                "UPDATE HistoryLeases SET ExpiresAtUtc = @expiry WHERE Id = 1", _connection, tx);
            renew.Parameters.AddWithValue("@expiry", newExpiry);
            renew.ExecuteNonQuery();
            FencingToken = token;
        }
        else
        {
            tx.Rollback();
            throw new StateOwnedByAnotherHostException(
                $"history in '{_databasePath}' is fenced by '{owner}' until {expires:o} — refusing to load");
        }
        tx.Commit();
    }

    /// <summary>
    /// The append's fencing check, inside the append's own transaction:
    /// the lease row must still name this owner with this token and be
    /// unexpired, or the append is refused stale.
    /// </summary>
    private void VerifyLeaseForAppend(SqliteTransaction tx)
    {
        if (_fencing is null)
            return;
        string? owner = null;
        long token = 0;
        DateTimeOffset expires = default;
        bool found = false;
        using (var select = new SqliteCommand(
            "SELECT OwnerId, FencingToken, ExpiresAtUtc FROM HistoryLeases WHERE Id = 1", _connection, tx))
        using (var reader = select.ExecuteReader())
            if (reader.Read())
            {
                found = true;
                owner = reader.GetString(0);
                token = reader.GetInt64(1);
                expires = DateTimeOffset.Parse(reader.GetString(2));
            }
        if (found && owner == _fencingOwnerId && token == FencingToken!.Value && expires > DateTimeOffset.UtcNow)
            return;
        string currentState = found
            ? $"the current lease names owner '{owner}' with token {token} until {expires:o}"
            : "the lease row is gone";
        throw new StaleFencingTokenException(_databasePath, _fencingOwnerId!, FencingToken!.Value, currentState);
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
        EnsureSession();
        var keys = new Dictionary<long, string>();
        DateTimeOffset? completedAt = null;
        bool purged = false;
        using (var cmd = new SqliteCommand(
            "SELECT Seq, IdempotencyKey FROM HistoryKeys ORDER BY Seq", _connection))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                keys[reader.GetInt64(0)] = reader.GetString(1);
        using (var cmd = new SqliteCommand(
            "SELECT CompletedAtUtc, KeysPurged FROM HistoryInstanceState WHERE Id = 1", _connection))
        using (var reader = cmd.ExecuteReader())
            if (reader.Read())
            {
                completedAt = reader.IsDBNull(0) ? null : DateTimeOffset.Parse(reader.GetString(0));
                purged = reader.GetInt64(1) != 0;
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
    private void WriteRegistryState(SqliteTransaction tx)
    {
        foreach (var kv in _registry.Keys)
        {
            using var upsert = new SqliteCommand(
                "INSERT INTO HistoryKeys (Seq, IdempotencyKey) VALUES (@seq, @key) " +
                "ON CONFLICT(Seq) DO UPDATE SET IdempotencyKey = excluded.IdempotencyKey",
                _connection, tx);
            upsert.Parameters.AddWithValue("@seq", kv.Key);
            upsert.Parameters.AddWithValue("@key", kv.Value);
            upsert.ExecuteNonQuery();
        }
        using var upsertState = new SqliteCommand(
            "INSERT INTO HistoryInstanceState (Id, CompletedAtUtc, KeysPurged) VALUES (1, @completed, @purged) " +
            "ON CONFLICT(Id) DO UPDATE SET CompletedAtUtc = excluded.CompletedAtUtc, KeysPurged = excluded.KeysPurged",
            _connection, tx);
        upsertState.Parameters.AddWithValue("@completed",
            _registry.CompletedAtUtc is null ? DBNull.Value : _registry.CompletedAtUtc.Value.ToString("o"));
        upsertState.Parameters.AddWithValue("@purged", _registry.KeysPurged ? 1L : 0L);
        upsertState.ExecuteNonQuery();
    }

    /// <summary>
    /// The registry side of one append, inside the append's own
    /// transaction: a scheduled command's key and a completion's
    /// timestamp become registry rows in the same commit as the record.
    /// </summary>
    private void WriteRegistryForAppend(SqliteTransaction tx, HistoryEvent framed)
    {
        if (framed.Type == HistoryEventTypes.ActivityScheduled)
        {
            using var upsert = new SqliteCommand(
                "INSERT INTO HistoryKeys (Seq, IdempotencyKey) VALUES (@seq, @key) " +
                "ON CONFLICT(Seq) DO UPDATE SET IdempotencyKey = excluded.IdempotencyKey",
                _connection, tx);
            upsert.Parameters.AddWithValue("@seq", framed.CommandSequence!.Value);
            upsert.Parameters.AddWithValue("@key", framed.GetString("idempotencyKey")!);
            upsert.ExecuteNonQuery();
        }
        else if (framed.Type == HistoryEventTypes.WorkflowCompleted)
        {
            using var upsertState = new SqliteCommand(
                "INSERT INTO HistoryInstanceState (Id, CompletedAtUtc, KeysPurged) VALUES (1, @completed, 0) " +
                "ON CONFLICT(Id) DO UPDATE SET CompletedAtUtc = excluded.CompletedAtUtc",
                _connection, tx);
            upsertState.Parameters.AddWithValue("@completed", framed.GetString("ts")!);
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
            using (var delete = new SqliteCommand("DELETE FROM HistoryKeys", _connection, tx))
                deleted = delete.ExecuteNonQuery();
            using (var mark = new SqliteCommand(
                "UPDATE HistoryInstanceState SET KeysPurged = 1 WHERE Id = 1", _connection, tx))
                mark.ExecuteNonQuery();
            tx.Commit();
            _registry.MarkPurged();
            return deleted;
        }
    }

    public string DatabasePath => _databasePath;

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
            using var cmd = new SqliteCommand(
                "SELECT Payload FROM HistoryEvents ORDER BY N", _connection);
            using var reader = cmd.ExecuteReader();
            long ordinal = 0;
            while (reader.Read())
            {
                ordinal++;
                events.Add(validator.Validate(OpenText(reader.GetString(0)), ordinal));
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
            using (var cmd = new SqliteCommand("SELECT Payload FROM HistorySnapshots WHERE Id = 1", _connection))
                snapshotPayload = cmd.ExecuteScalar() as string;
            if (snapshotPayload is null)
            {
                var full = Load();
                return new HistorySnapshotLoadResult(full, false, 0);
            }

            var snapshot = HistorySnapshotCodec.Parse(OpenText(snapshotPayload));
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
            using var appendSpan = EngineDiagnostics.StartStoreAppendSpan(
                "sqlite", historyEvent.Type, historyEvent.InstanceId);

            var fields = (JsonObject)historyEvent.Fields.DeepClone();
            fields["n"] = _nextSequence;
            fields["sum"] = FileHistoryStore.Checksum(fields);
            string payload = SealText(fields.ToJsonString());
            var framed = new HistoryEvent(fields);
            bool tear = _faultInjector?.ShouldTear(framed) == true;

            using var tx = _connection!.BeginTransaction(deferred: false);

            // Fencing first: a stale writer is refused before anything else
            // in the transaction reads or writes history.
            VerifyLeaseForAppend(tx);

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
                    using var upsert = new SqliteCommand(
                        "INSERT INTO HistorySnapshots (Id, ThroughN, Payload) VALUES (1, @through, @payload) " +
                        "ON CONFLICT(Id) DO UPDATE SET ThroughN = excluded.ThroughN, Payload = excluded.Payload",
                        _connection, tx);
                    upsert.Parameters.Add(new SqliteParameter("@through", SqliteType.Integer) { Value = _nextSequence });
                    upsert.Parameters.Add(new SqliteParameter("@payload", SqliteType.Text)
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
            EngineDiagnostics.RecordStoreAppend("sqlite", framed);
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

        // OS file lock = single-writer ownership — skipped when fencing
        // is on: the lease + token are the authority instead.
        if (_fencing is null)
        {
            try
            {
                _ownershipLock = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                throw new StateOwnedByAnotherHostException(
                    "history store is owned by another running store — refusing to load (single-writer)");
            }
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
        using var createKeys = new SqliteCommand(
            "CREATE TABLE IF NOT EXISTS HistoryKeys (" +
            "  Seq INTEGER NOT NULL PRIMARY KEY," +
            "  IdempotencyKey TEXT NOT NULL);",
            _connection);
        createKeys.ExecuteNonQuery();
        using var createState = new SqliteCommand(
            "CREATE TABLE IF NOT EXISTS HistoryInstanceState (" +
            "  Id INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1)," +
            "  CompletedAtUtc TEXT NULL," +
            "  KeysPurged INTEGER NOT NULL DEFAULT 0);",
            _connection);
        createState.ExecuteNonQuery();
        using var createLeases = new SqliteCommand(
            "CREATE TABLE IF NOT EXISTS HistoryLeases (" +
            "  Id INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1)," +
            "  OwnerId TEXT NOT NULL," +
            "  FencingToken INTEGER NOT NULL," +
            "  ExpiresAtUtc TEXT NOT NULL);",
            _connection);
        createLeases.ExecuteNonQuery();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SqliteHistoryStore));
    }
}
