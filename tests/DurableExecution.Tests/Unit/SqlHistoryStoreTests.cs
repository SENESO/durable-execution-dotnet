using DurableExecution.Execution;
using DurableExecution.History;
using DurableExecution.SqlServer;
using Microsoft.Data.SqlClient;

namespace DurableExecution.Tests.Unit;

/// <summary>
/// Behaviour that belongs to the SQL Server provider alone (§6.5's
/// provider-specific edge): the session application lock as single-writer
/// ownership inside one database, and instance isolation when several
/// instances share that database. The portable contract lives in the
/// conformance suite; these tests pin what only this provider has.
/// </summary>
[Trait("Provider", "SqlServer")]
public sealed class SqlHistoryStoreTests : IDisposable
{
    private readonly (string Name, string Connection) _database = SqlTestDatabases.CreateDatabase("store");

    public void Dispose()
    {
        try { SqlTestDatabases.DropDatabase(_database.Name); }
        catch (SqlException) { /* best-effort cleanup of a scratch database */ }
    }

    private static string Fingerprint() =>
        DurableExecution.Execution.Fingerprints.InputFingerprint(null);

    [Fact]
    public void SecondStore_SameInstance_IsRefusedWhileFirstStoreHoldsTheLock()
    {
        using var first = new SqlHistoryStore(_database.Connection, "inst-1");
        first.Load();

        using var second = new SqlHistoryStore(_database.Connection, "inst-1");
        Assert.Throws<StateOwnedByAnotherHostException>(() => second.Load());
    }

    [Fact]
    public void Store_AfterOwnerDisposed_CanLoadAndAppend()
    {
        using (var first = new SqlHistoryStore(_database.Connection, "inst-1"))
        {
            first.Load();
            first.Append(HistoryEvent.WorkflowStarted("inst-1", "order/v1", Fingerprint()));
        } // disposing the owner releases the session lock

        using var second = new SqlHistoryStore(_database.Connection, "inst-1");
        var loaded = second.Load();
        Assert.Single(loaded.Events);
        var appended = second.Append(HistoryEvent.WorkflowCompleted("inst-1", null));
        Assert.Equal(2, appended.N);
    }

    [Fact]
    public void InstancesSharingOneDatabase_AreIsolated()
    {
        using var storeA = new SqlHistoryStore(_database.Connection, "inst-a");
        storeA.Load();
        storeA.Append(HistoryEvent.WorkflowStarted("inst-a", "order/v1", Fingerprint()));

        using var storeB = new SqlHistoryStore(_database.Connection, "inst-b");
        var loadedB = storeB.Load();
        Assert.Empty(loadedB.Events); // inst-a's history is not inst-b's

        var appendedB = storeB.Append(HistoryEvent.WorkflowStarted("inst-b", "order/v1", Fingerprint()));
        Assert.Equal(1, appendedB.N); // sequences are per instance
    }

    [Fact]
    public void Append_EventOfAnotherInstance_IsRefused()
    {
        using var store = new SqlHistoryStore(_database.Connection, "inst-1");
        store.Load();
        Assert.Throws<InvalidOperationException>(() =>
            store.Append(HistoryEvent.WorkflowStarted("inst-2", "order/v1", Fingerprint())));
    }
}
