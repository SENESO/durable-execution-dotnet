using DurableExecution.Execution;
using DurableExecution.History;
using DurableExecution.PostgreSql;
using DurableExecution.Sqlite;
using DurableExecution.SqlServer;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DurableExecution.Tests.Unit;

/// <summary>
/// §8.5 idempotency key expiry, proven per provider (§6.5): the key
/// registry records every scheduled command's key and the completion
/// time; keys stay on record (the dedup lookup surface) until the
/// retention policy elapses; the host-driven sweep then purges them —
/// registry only. History stays byte-identical, still loads and
/// validates, and the instance still reports completed; a relaunch
/// does not resurrect purged keys.
/// </summary>
public abstract class KeyRetentionTests(IKeyRetentionFixture fixture) : IDisposable
{
    private static readonly KeyRetentionPolicy Week = new(TimeSpan.FromDays(7));

    protected IKeyRetentionFixture Fixture { get; } = fixture;

    public void Dispose() => Fixture.Dispose();

    private (WorkflowHost Host, IHistoryStore Store) OpenHost(KeyRetentionPolicy? retention)
    {
        var store = Fixture.CreateStore(retention);
        return (new WorkflowHost(store, Fixture.StateDir), store);
    }

    private static async Task RunTwoStepWorkflow(WorkflowHost host)
    {
        Func<WorkflowContext, Task<object?>> body = async ctx =>
        {
            string txn = await ctx.CallActivityAsync("ChargeCard", new { amount = 1000 }, actx =>
                Task.FromResult("txn-1"));
            await ctx.CallActivityAsync("ReserveStock", new { orderId = "order-1001" }, actx =>
                Task.FromResult("reserved"));
            return txn;
        };
        Assert.Equal(WorkflowRunOutcome.Completed,
            await host.RunAsync("order/v1", "order-1001", new { amount = 1000 }, body));
    }

    [Fact]
    public async Task Registry_RecordsEveryScheduledKey_AndTheCompletionTime()
    {
        var (host, store) = OpenHost(Week);
        using (host)
        {
            await RunTwoStepWorkflow(host);

            var retention = (IHistoryKeyRetention)store;
            var history = store.Load().Events;
            var scheduledKeys = history
                .Where(e => e.Type == HistoryEventTypes.ActivityScheduled)
                .ToDictionary(e => e.CommandSequence!.Value, e => e.GetString("idempotencyKey")!);
            Assert.Equal(2, scheduledKeys.Count);
            Assert.Equal(scheduledKeys, retention.RecordedKeys);
            foreach (var key in scheduledKeys.Values)
                Assert.True(retention.KeyOnRecord(key), "a scheduled key must be on record before expiry");
            Assert.False(retention.KeyOnRecord("not-a-real-key"));

            var completedAt = DateTimeOffset.Parse(history.Single(e => e.Type == HistoryEventTypes.WorkflowCompleted)
                .GetString("ts")!);
            Assert.Equal(completedAt, retention.CompletedAtUtc);
        }
    }

    [Fact]
    public async Task Purge_BeforeRetentionElapses_RemovesNothing()
    {
        var (host, store) = OpenHost(Week);
        using (host)
        {
            await RunTwoStepWorkflow(host);
            var retention = (IHistoryKeyRetention)store;
            Assert.Equal(0, host.PurgeExpiredKeys(retention.CompletedAtUtc!.Value.AddDays(1)));
            Assert.Equal(2, retention.RecordedKeys.Count);
        }
    }

    [Fact]
    public async Task Purge_AfterRetention_RemovesKeysOnly_HistoryUntouched_NoResurrection()
    {
        string[] historyBefore;
        DateTimeOffset completedAt;
        var (host, store) = OpenHost(Week);
        using (host)
        {
            await RunTwoStepWorkflow(host);
            var retention = (IHistoryKeyRetention)store;
            historyBefore = store.Load().Events.Select(e => $"{e.N}:{e.Type}").ToArray();
            completedAt = retention.CompletedAtUtc!.Value;
            Assert.Equal(2, host.PurgeExpiredKeys(completedAt.AddDays(8)));
            Assert.Empty(retention.RecordedKeys);
        }

        // A fresh store over the same state: the purge marker held (the
        // fold did not resurrect keys), history is byte-identical in
        // shape, and the instance still reports completed to a new host.
        var (host2, store2) = OpenHost(Week);
        using (host2)
        {
            var retention2 = (IHistoryKeyRetention)store2;
            var load = store2.Load();
            Assert.Equal(historyBefore, load.Events.Select(e => $"{e.N}:{e.Type}").ToArray());
            Assert.Empty(retention2.RecordedKeys);
            Assert.Equal(completedAt, retention2.CompletedAtUtc);
            Assert.Equal(0, host2.PurgeExpiredKeys(completedAt.AddDays(30)));

            Func<WorkflowContext, Task<object?>> body = ctx => Task.FromResult<object?>("unused");
            Assert.Equal(WorkflowRunOutcome.AlreadyCompleted,
                await host2.RunAsync("order/v1", "order-1001", new { amount = 1000 }, body));
        }
    }

    [Fact]
    public void Purge_InstanceThatNeverCompleted_RemovesNothing()
    {
        var store = Fixture.CreateStore(Week);
        try
        {
            store.Load();
            store.Append(HistoryEvent.WorkflowStarted("order-1001", "order/v1", "{}"));
            store.Append(HistoryEvent.ActivityScheduled(
                "order-1001", 1, "StepA", "key-0001", "{}"));
            var retention = (IHistoryKeyRetention)store;
            Assert.True(retention.KeyOnRecord("key-0001"));
            Assert.Null(retention.CompletedAtUtc);
            Assert.Equal(0, retention.PurgeExpiredKeys(DateTimeOffset.UtcNow.AddYears(1)));
            Assert.True(retention.KeyOnRecord("key-0001"));
        }
        finally
        {
            Fixture.ReleaseStores();
        }
    }

    [Fact]
    public void Purge_WithoutAPolicy_Refuses()
    {
        var store = Fixture.CreateStore(null);
        try
        {
            store.Load();
            var ex = Assert.Throws<InvalidOperationException>(
                () => ((IHistoryKeyRetention)store).PurgeExpiredKeys(DateTimeOffset.UtcNow));
            Assert.Contains("no key retention policy", ex.Message);
        }
        finally
        {
            Fixture.ReleaseStores();
        }
    }
}

/// <summary>A retention-test fixture: builds stores with a chosen retention policy over one scratch state.</summary>
public interface IKeyRetentionFixture : IDisposable
{
    /// <summary>The directory the host's ownership lock lives in (the store's own directory for file-shaped providers).</summary>
    string StateDir { get; }

    IHistoryStore CreateStore(KeyRetentionPolicy? retention);

    /// <summary>Releases stores created outside a host (hosts dispose their own store).</summary>
    void ReleaseStores();
}

internal abstract class KeyRetentionFixtureBase : IKeyRetentionFixture
{
    private readonly List<IHistoryStore> _stores = new();

    public abstract string StateDir { get; }

    protected abstract IHistoryStore BuildStore(KeyRetentionPolicy? retention);

    public IHistoryStore CreateStore(KeyRetentionPolicy? retention)
    {
        var store = BuildStore(retention);
        _stores.Add(store);
        return store;
    }

    public void ReleaseStores()
    {
        foreach (var store in _stores)
            (store as IDisposable)?.Dispose();
        _stores.Clear();
    }

    public virtual void Dispose() => ReleaseStores();
}

internal sealed class FileKeyRetentionFixture : KeyRetentionFixtureBase
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "p0-retention", Guid.NewGuid().ToString("N")[..8]);

    public FileKeyRetentionFixture() => Directory.CreateDirectory(_dir);

    public override string StateDir => _dir;

    protected override IHistoryStore BuildStore(KeyRetentionPolicy? retention) =>
        new FileHistoryStore(_dir, keyRetention: retention);
}

internal sealed class SqliteKeyRetentionFixture : KeyRetentionFixtureBase
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "p0-retention", Guid.NewGuid().ToString("N")[..8]);

    public SqliteKeyRetentionFixture() => Directory.CreateDirectory(_dir);

    public override string StateDir => _dir;

    protected override IHistoryStore BuildStore(KeyRetentionPolicy? retention) =>
        new SqliteHistoryStore(_dir, keyRetention: retention);
}

internal sealed class SqlKeyRetentionFixture : KeyRetentionFixtureBase
{
    private readonly (string Name, string Connection) _database = SqlTestDatabases.CreateDatabase("retention");
    private readonly string _hostDir = Path.Combine(
        Path.GetTempPath(), "p0-retention", Guid.NewGuid().ToString("N")[..8]);

    public SqlKeyRetentionFixture() => Directory.CreateDirectory(_hostDir);

    public override string StateDir => _hostDir;

    protected override IHistoryStore BuildStore(KeyRetentionPolicy? retention) =>
        new SqlHistoryStore(_database.Connection, "order-1001", keyRetention: retention);

    public override void Dispose()
    {
        base.Dispose();
        try { SqlTestDatabases.DropDatabase(_database.Name); }
        catch (SqlException) { /* best-effort cleanup of a scratch database */ }
    }
}

internal sealed class PostgresKeyRetentionFixture : KeyRetentionFixtureBase
{
    private readonly (string Name, string Connection) _database = PostgresTestDatabases.CreateDatabase("retention");
    private readonly string _hostDir = Path.Combine(
        Path.GetTempPath(), "p0-retention", Guid.NewGuid().ToString("N")[..8]);

    public PostgresKeyRetentionFixture() => Directory.CreateDirectory(_hostDir);

    public override string StateDir => _hostDir;

    protected override IHistoryStore BuildStore(KeyRetentionPolicy? retention) =>
        new PostgresHistoryStore(_database.Connection, "order-1001", keyRetention: retention);

    public override void Dispose()
    {
        base.Dispose();
        try { PostgresTestDatabases.DropDatabase(_database.Name); }
        catch (NpgsqlException) { /* best-effort cleanup of a scratch database */ }
    }
}

[Trait("Provider", "File")]
public sealed class FileKeyRetentionTests() : KeyRetentionTests(new FileKeyRetentionFixture());

[Trait("Provider", "Sqlite")]
public sealed class SqliteKeyRetentionTests() : KeyRetentionTests(new SqliteKeyRetentionFixture());

[Trait("Provider", "SqlServer")]
public sealed class SqlKeyRetentionTests() : KeyRetentionTests(new SqlKeyRetentionFixture());

[Trait("Provider", "PostgreSql")]
public sealed class PostgresKeyRetentionTests() : KeyRetentionTests(new PostgresKeyRetentionFixture());
