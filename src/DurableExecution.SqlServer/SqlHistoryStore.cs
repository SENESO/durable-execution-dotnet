using System.Data;
using System.Text.Json.Nodes;
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
public sealed class SqlHistoryStore : IHistoryStore, IDisposable
{
    /// <summary>How long load waits for a previous owner's lock to clear before refusing (a killed owner's session is reaped asynchronously by the server).</summary>
    private static readonly TimeSpan LockGraceWindow = TimeSpan.FromSeconds(2);

    private readonly string _connectionString;
    private readonly string _instanceId;
    private readonly IHistoryFaultInjector? _faultInjector;
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
    public SqlHistoryStore(string connectionString, string instanceId, IHistoryFaultInjector? faultInjector = null)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _instanceId = string.IsNullOrEmpty(instanceId)
            ? throw new ArgumentException("instance id must be a non-empty string", nameof(instanceId))
            : instanceId;
        _faultInjector = faultInjector;
    }

    public HistoryLoadResult Load()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureSession();

            var events = new List<HistoryEvent>();
            var validator = new HistoryRecordValidator();
            using var cmd = new SqlCommand(
                "SELECT Payload FROM dbo.HistoryEvents WHERE InstanceId = @instance ORDER BY N",
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
            // A database history is transactional: no torn tail can exist.
            return new HistoryLoadResult(events, false, 0);
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
        // lock outlasts the window and this store is refused.
        var deadline = DateTime.UtcNow + LockGraceWindow;
        while (true)
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
