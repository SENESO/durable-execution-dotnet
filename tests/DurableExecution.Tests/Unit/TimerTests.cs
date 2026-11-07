using DurableExecution.Execution;
using DurableExecution.History;

namespace DurableExecution.Tests.Unit;

/// <summary>
/// Durable timers (P2): the due time is recorded before the wait begins,
/// the timer fires at its recorded due time, and a host that dies mid-wait
/// leaves a resume that waits only for the remainder. Driven on a manual
/// clock — no wall-clock sleeps. Written once, run per provider (§6.5).
/// </summary>
public abstract class TimerTests(IStoreFactory factory) : IDisposable
{
    protected IStoreFactory Factory { get; } = factory;

    public void Dispose() => Factory.Dispose();

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static string FreshDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "p0-unit", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private WorkflowHost OpenHost(string dir, IWorkflowClock clock) =>
        new(Factory.CreateStore(dir), dir, clock: clock);

    private List<HistoryEvent> LoadHistory(string dir)
    {
        var store = Factory.CreateStore(dir);
        try { return store.Load().Events.ToList(); }
        finally { (store as IDisposable)?.Dispose(); }
    }

    [Fact]
    public async Task Timer_FiresAtItsRecordedDueTime_NotBefore()
    {
        string dir = FreshDir();
        var clock = new ManualClock(T0);
        int charges = 0;

        using (var host = OpenHost(dir, clock))
        {
            var run = host.RunAsync("timer/v1", "order-1001", new { amount = 1000 }, async ctx =>
            {
                await ctx.DelayAsync(TimeSpan.FromMinutes(5));
                await ctx.CallActivityAsync("ChargeCard", new { amount = 1000 }, actx =>
                {
                    charges++;
                    return Task.FromResult("txn-1");
                });
                return "done";
            });

            await clock.FirstDelayEntered.WaitAsync(TimeSpan.FromSeconds(10));
            clock.Advance(TimeSpan.FromMinutes(4));
            await Task.Delay(100); // give a wrongful fire every chance to happen
            Assert.False(run.IsCompleted);
            Assert.Equal(0, charges);

            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(WorkflowRunOutcome.Completed, await run.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(1, charges);
        }

        var history = LoadHistory(dir);
        var scheduled = Assert.Single(history, e => e.Type == HistoryEventTypes.TimerScheduled);
        Assert.Equal(T0 + TimeSpan.FromMinutes(5), DateTimeOffset.Parse(scheduled.GetString("dueAt")!));
        Assert.Single(history, e => e.Type == HistoryEventTypes.TimerFired);
    }

    [Fact]
    public async Task Timer_SurvivesHostDeathMidWait_ResumeWaitsOnlyTheRemainder()
    {
        string dir = FreshDir();
        int charges = 0;

        // Host 1: schedules the timer, then dies mid-wait (the body's
        // cancellation stands in for the process going away).
        var clock1 = new ManualClock(T0);
        using (var host = OpenHost(dir, clock1))
        {
            using var death = new CancellationTokenSource();
            var run = host.RunAsync("timer/v1", "order-1001", new { amount = 1000 }, async ctx =>
            {
                await ctx.DelayAsync(TimeSpan.FromMinutes(5), death.Token);
                await ctx.CallActivityAsync("ChargeCard", new { amount = 1000 }, actx =>
                {
                    charges++;
                    return Task.FromResult("txn-1");
                });
                return "done";
            });
            await clock1.FirstDelayEntered.WaitAsync(TimeSpan.FromSeconds(10));
            death.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }

        var afterDeath = LoadHistory(dir);
        Assert.Single(afterDeath, e => e.Type == HistoryEventTypes.TimerScheduled);
        Assert.DoesNotContain(afterDeath, e => e.Type == HistoryEventTypes.TimerFired);
        Assert.Equal(0, charges);

        // Host 2 (a fresh clock at the same wall time): the resume reads
        // the recorded due time — 2 minutes in, it must not fire; at the
        // original due time, it must.
        var clock2 = new ManualClock(T0 + TimeSpan.FromMinutes(2));
        using (var host = OpenHost(dir, clock2))
        {
            var run = host.RunAsync("timer/v1", "order-1001", new { amount = 1000 }, async ctx =>
            {
                await ctx.DelayAsync(TimeSpan.FromMinutes(5));
                await ctx.CallActivityAsync("ChargeCard", new { amount = 1000 }, actx =>
                {
                    charges++;
                    return Task.FromResult("txn-1");
                });
                return "done";
            });
            await clock2.FirstDelayEntered.WaitAsync(TimeSpan.FromSeconds(10));
            clock2.Advance(TimeSpan.FromMinutes(2)); // now T0+4 — still not due
            await Task.Delay(100);
            Assert.False(run.IsCompleted);
            clock2.Advance(TimeSpan.FromMinutes(1)); // now T0+5 — the recorded due time
            Assert.Equal(WorkflowRunOutcome.Completed, await run.WaitAsync(TimeSpan.FromSeconds(10)));
        }

        Assert.Equal(1, charges);
        var history = LoadHistory(dir);
        Assert.Single(history, e => e.Type == HistoryEventTypes.TimerScheduled); // never re-scheduled
        Assert.Single(history, e => e.Type == HistoryEventTypes.TimerFired);
    }

    [Fact]
    public async Task Timer_ChangedDurationAtResume_IsADeterminismViolation()
    {
        string dir = FreshDir();
        var clock1 = new ManualClock(T0);
        using (var host = OpenHost(dir, clock1))
        {
            using var death = new CancellationTokenSource();
            var run = host.RunAsync("timer/v1", "order-1001", new { amount = 1000 }, async ctx =>
            {
                await ctx.DelayAsync(TimeSpan.FromMinutes(5), death.Token);
                return null;
            });
            await clock1.FirstDelayEntered.WaitAsync(TimeSpan.FromSeconds(10));
            death.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }

        var clock2 = new ManualClock(T0);
        using var host2 = OpenHost(dir, clock2);
        await Assert.ThrowsAsync<NonDeterminismViolationException>(() =>
            host2.RunAsync("timer/v1", "order-1001", new { amount = 1000 }, async ctx =>
            {
                await ctx.DelayAsync(TimeSpan.FromMinutes(9));
                return null;
            }));
    }

    [Fact]
    public async Task Timer_ZeroDuration_FiresImmediately()
    {
        string dir = FreshDir();
        var clock = new ManualClock(T0);
        WorkflowRunOutcome outcome;
        using (var host = OpenHost(dir, clock))
        {
            outcome = await host.RunAsync("timer/v1", "order-1001", new { amount = 1000 }, async ctx =>
            {
                await ctx.DelayAsync(TimeSpan.Zero);
                return "done";
            });
        }
        Assert.Equal(WorkflowRunOutcome.Completed, outcome);
        var history = LoadHistory(dir);
        Assert.Single(history, e => e.Type == HistoryEventTypes.TimerScheduled);
        Assert.Single(history, e => e.Type == HistoryEventTypes.TimerFired);
    }
}

/// <summary>Timer tests against the file provider.</summary>
[Trait("Provider", "File")]
public sealed class TimerTestsFile() : TimerTests(new FileStoreFactory());

/// <summary>Timer tests against the SQL Server provider.</summary>
[Trait("Provider", "SqlServer")]
public sealed class TimerTestsSqlServer() : TimerTests(new SqlStoreFactory());

/// <summary>Timer tests against the PostgreSQL provider.</summary>
[Trait("Provider", "PostgreSql")]
public sealed class TimerTestsPostgreSql() : TimerTests(new PostgresStoreFactory());

/// <summary>Timer tests against the SQLite provider.</summary>
[Trait("Provider", "Sqlite")]
public sealed class TimerTestsSqlite() : TimerTests(new SqliteStoreFactory());
