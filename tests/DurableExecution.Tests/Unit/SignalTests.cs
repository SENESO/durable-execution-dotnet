using DurableExecution.Execution;
using DurableExecution.History;

namespace DurableExecution.Tests.Unit;

/// <summary>
/// Signals (P2): a signal is recorded in history before it is delivered;
/// delivery consumes per name, in recorded order; a signal appended while
/// no host ran is delivered from history on the next run. Written once,
/// run per provider (§6.5).
/// </summary>
public abstract class SignalTests(IStoreFactory factory) : IDisposable
{
    protected IStoreFactory Factory { get; } = factory;

    public void Dispose() => Factory.Dispose();

    private static string FreshDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "p0-unit", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private WorkflowHost OpenHost(string dir) => new(Factory.CreateStore(dir), dir);

    private List<HistoryEvent> LoadHistory(string dir)
    {
        var store = Factory.CreateStore(dir);
        try { return store.Load().Events.ToList(); }
        finally { (store as IDisposable)?.Dispose(); }
    }

    private void AppendSignalBeforeRun(string dir, string name, object? payload)
    {
        var store = Factory.CreateStore(dir);
        try
        {
            store.Load();
            store.Append(HistoryEvent.SignalReceived("order-1001", name, payload));
        }
        finally { (store as IDisposable)?.Dispose(); }
    }

    [Fact]
    public async Task LiveSignal_RecordedBeforeDelivery_EffectUsesThePayload()
    {
        string dir = FreshDir();
        int chargeExecutions = 0;
        int chargedAmount = 0;
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        WorkflowRunOutcome outcome;
        using (var host = OpenHost(dir))
        {
            var run = host.RunAsync("signal/v1", "order-1001", new { amount = 1000 }, async ctx =>
            {
                waiting.TrySetResult();
                int approved = await ctx.WaitForSignalAsync<int>("approve");
                await ctx.CallActivityAsync("ChargeCard", new { amount = approved }, actx =>
                {
                    chargeExecutions++;
                    chargedAmount = approved;
                    return Task.FromResult("txn-1");
                });
                return approved;
            });

            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(100); // the workflow is parked in the wait now
            Assert.False(run.IsCompleted);
            Assert.Equal(0, chargeExecutions);

            host.SendSignal("approve", 500);
            outcome = await run.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(WorkflowRunOutcome.Completed, outcome);
        Assert.Equal(1, chargeExecutions);
        Assert.Equal(500, chargedAmount);

        var history = LoadHistory(dir);
        int signalAt = history.FindIndex(e => e.Type == HistoryEventTypes.SignalReceived);
        int scheduledAt = history.FindIndex(e => e.Type == HistoryEventTypes.ActivityScheduled);
        Assert.True(signalAt >= 0 && scheduledAt > signalAt,
            "the signal must be recorded before the activity it triggered was scheduled");
    }

    [Fact]
    public async Task SignalSentBeforeTheRun_IsDeliveredFromHistory()
    {
        string dir = FreshDir();
        AppendSignalBeforeRun(dir, "approve", 700);

        using var host = OpenHost(dir);
        var outcome = await host.RunAsync("signal/v1", "order-1001", new { amount = 1000 }, async ctx =>
        {
            int approved = await ctx.WaitForSignalAsync<int>("approve");
            return approved;
        });
        Assert.Equal(WorkflowRunOutcome.Completed, outcome);
    }

    [Fact]
    public async Task Signals_AreConsumedPerName_InRecordedOrder()
    {
        string dir = FreshDir();
        AppendSignalBeforeRun(dir, "other", 1);
        AppendSignalBeforeRun(dir, "note", "first");
        AppendSignalBeforeRun(dir, "note", "second");

        var seen = new List<object?>();
        using var host = OpenHost(dir);
        var outcome = await host.RunAsync("signal/v1", "order-1001", new { amount = 1000 }, async ctx =>
        {
            seen.Add(await ctx.WaitForSignalAsync<string>("note"));
            seen.Add(await ctx.WaitForSignalAsync<string>("note"));
            seen.Add(await ctx.WaitForSignalAsync<int>("other"));
            return null;
        });
        Assert.Equal(WorkflowRunOutcome.Completed, outcome);
        Assert.Equal(new object?[] { "first", "second", 1 }, seen);
    }

    [Fact]
    public void SendSignal_WithNoActiveRun_Throws()
    {
        string dir = FreshDir();
        using var host = OpenHost(dir);
        Assert.Throws<InvalidOperationException>(() => host.SendSignal("approve", 1));
    }
}

/// <summary>Signal tests against the file provider.</summary>
[Trait("Provider", "File")]
public sealed class SignalTestsFile() : SignalTests(new FileStoreFactory());

/// <summary>Signal tests against the SQL Server provider.</summary>
[Trait("Provider", "SqlServer")]
public sealed class SignalTestsSqlServer() : SignalTests(new SqlStoreFactory());

/// <summary>Signal tests against the PostgreSQL provider.</summary>
[Trait("Provider", "PostgreSql")]
public sealed class SignalTestsPostgreSql() : SignalTests(new PostgresStoreFactory());

/// <summary>Signal tests against the SQLite provider.</summary>
[Trait("Provider", "Sqlite")]
public sealed class SignalTestsSqlite() : SignalTests(new SqliteStoreFactory());
