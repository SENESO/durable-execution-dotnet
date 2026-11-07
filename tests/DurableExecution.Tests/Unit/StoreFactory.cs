using DurableExecution.History;
using DurableExecution.SqlServer;
using Microsoft.Data.SqlClient;

namespace DurableExecution.Tests.Unit;

/// <summary>
/// Creates the history store for an in-process test, per provider (§6.5):
/// the same host tests run over the file provider and over a SQL Server
/// provider bound to a throwaway database. The instance id matches the one
/// the in-process tests run under (<c>order-1001</c>).
/// </summary>
public interface IStoreFactory : IDisposable
{
    IHistoryStore CreateStore(string stateDir);
}

/// <summary>The file provider's factory: a store over the test's state directory.</summary>
public sealed class FileStoreFactory : IStoreFactory
{
    public IHistoryStore CreateStore(string stateDir) => new FileHistoryStore(stateDir);

    public void Dispose() { }
}

/// <summary>
/// The SQL Server provider's factory: one scratch database per test class,
/// dropped on dispose. Requires <c>DE_SQLSERVER_CONNECTION</c> (see
/// SqlTestDatabases); the class is trait-excluded when it is absent.
/// </summary>
public sealed class SqlStoreFactory : IStoreFactory
{
    private readonly (string Name, string Connection) _database = SqlTestDatabases.CreateDatabase("unit");

    public IHistoryStore CreateStore(string stateDir) => new SqlHistoryStore(_database.Connection, "order-1001");

    public void Dispose()
    {
        try { SqlTestDatabases.DropDatabase(_database.Name); }
        catch (SqlException) { /* best-effort cleanup of a scratch database */ }
    }
}
