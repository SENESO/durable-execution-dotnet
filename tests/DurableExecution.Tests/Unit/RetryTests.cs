using DurableExecution.Execution;
using DurableExecution.History;

namespace DurableExecution.Tests.Unit;

/// <summary>
/// Retry policies (P2): failed attempts are recorded in history, backoff
/// waits go through the engine clock, the idempotency key is identical on
/// every attempt, and exhaustion is durable — a resume re-throws without
/// executing again. Written once, run per provider (§6.5).
/// </summary>
public abstract class RetryTests(IStoreFactory factory) : IDisposable
{
    protected IStoreFactory Factory { get; } = factory;

    public void Dispose() => Factory.Dispose();

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
    public async Task FlakyActivity_SucceedsOnThirdAttempt_FailuresRecorded_BackoffApplied_KeyStable()
    {
        string dir = FreshDir();
        var clock = new FakeClock();
        int executions = 0;
        var keys = new List<string>();

        using (var host = OpenHost(dir, clock))
        {
            var outcome = await host.RunAsync("retry/v1", "order-1001", new { amount = 1000 }, async ctx =>
            {
                string txn = await ctx.CallActivityAsync<string>("ChargeCard", new { amount = 1000 }, actx =>
                {
                    executions++;
                    keys.Add(actx.IdempotencyKey);
                    if (actx.Attempt < 3)
                        throw new InvalidOperationException($"transient fault {actx.Attempt}");
                    return Task.FromResult("txn-9");
                }, new RetryPolicy(3, TimeSpan.FromMilliseconds(100)));
                return txn;
            });
            Assert.Equal(WorkflowRunOutcome.Completed, outcome);
        }

        Assert.Equal(3, executions);
        Assert.Single(keys.Distinct()); // one idempotency key, seen by all three attempts
        Assert.Equal(
            new[] { TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200) },
            clock.Delays);

        var history = LoadHistory(dir);
        var failed = history.Where(e => e.Type == HistoryEventTypes.ActivityAttemptFailed).ToList();
        Assert.Equal(new long[] { 1, 2 }, failed.Select(e => e.GetInt64("attempt")!.Value).ToArray());
        Assert.All(failed, e => Assert.Contains("transient fault", e.GetString("error")));
        Assert.Single(history, e => e.Type == HistoryEventTypes.ActivityCompleted);
        Assert.Equal(3, history.Count(e => e.Type == HistoryEventTypes.ActivityAttemptStarted));
    }

    [Fact]
    public async Task ExhaustedPolicy_ThrowsDurably_ResumeDoesNotExecuteAgain()
    {
        string dir = FreshDir();
        var clock = new FakeClock();
        int executions = 0;
        var policy = new RetryPolicy(2, TimeSpan.FromMilliseconds(10));

        Func<WorkflowContext, Task<object?>> body = async ctx =>
        {
            await ctx.CallActivityAsync<string>("ChargeCard", new { amount = 1000 }, actx =>
            {
                executions++;
                throw new InvalidOperationException("permanent fault");
            }, policy);
            return null;
        };

        using (var host = OpenHost(dir, clock))
        {
            var ex = await Assert.ThrowsAsync<ActivityFailedException>(
                () => host.RunAsync("retry/v1", "order-1001", new { amount = 1000 }, body));
            Assert.Equal(2, ex.FailedAttempts);
        }
        Assert.Equal(2, executions);

        // Resume: the recorded failures exhaust the policy before any
        // execution — the body must not run again.
        using (var host = OpenHost(dir, clock))
        {
            var ex = await Assert.ThrowsAsync<ActivityFailedException>(
                () => host.RunAsync("retry/v1", "order-1001", new { amount = 1000 }, body));
            Assert.Equal(2, ex.FailedAttempts);
        }
        Assert.Equal(2, executions);

        var history = LoadHistory(dir);
        Assert.Equal(2, history.Count(e => e.Type == HistoryEventTypes.ActivityAttemptFailed));
        Assert.DoesNotContain(history, e => e.Type == HistoryEventTypes.WorkflowCompleted);
    }

    [Fact]
    public async Task NoPolicy_ThrowPropagatesAndRecordsNoFailure_P0BehaviourUnchanged()
    {
        string dir = FreshDir();
        var clock = new FakeClock();
        int executions = 0;

        Func<WorkflowContext, Task<object?>> body = async ctx =>
        {
            string txn = await ctx.CallActivityAsync<string>("ChargeCard", new { amount = 1000 }, actx =>
            {
                executions++;
                if (executions == 1)
                    throw new InvalidOperationException("boom");
                return Task.FromResult("txn-2");
            });
            return txn;
        };

        using (var host = OpenHost(dir, clock))
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => host.RunAsync("retry/v1", "order-1001", new { amount = 1000 }, body));

        var history = LoadHistory(dir);
        Assert.DoesNotContain(history, e => e.Type == HistoryEventTypes.ActivityAttemptFailed);
        Assert.Empty(clock.Delays);

        // The interrupted attempt re-executes on resume, as in P0.
        using (var host = OpenHost(dir, clock))
            Assert.Equal(WorkflowRunOutcome.Completed,
                await host.RunAsync("retry/v1", "order-1001", new { amount = 1000 }, body));
        Assert.Equal(2, executions);
    }

    [Fact]
    public void Policy_BackoffScheduleIsPureAndCapped()
    {
        var policy = new RetryPolicy(5, TimeSpan.FromMilliseconds(100), 3.0, TimeSpan.FromMilliseconds(250));
        Assert.Equal(TimeSpan.FromMilliseconds(100), policy.DelayAfterFailedAttempt(1));
        Assert.Equal(TimeSpan.FromMilliseconds(250), policy.DelayAfterFailedAttempt(2)); // 300 capped
        Assert.Equal(TimeSpan.FromMilliseconds(250), policy.DelayAfterFailedAttempt(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(0, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(1, TimeSpan.Zero, 0.5));
    }
}

/// <summary>Retry tests against the file provider.</summary>
[Trait("Provider", "File")]
public sealed class RetryTestsFile() : RetryTests(new FileStoreFactory());

/// <summary>Retry tests against the SQL Server provider.</summary>
[Trait("Provider", "SqlServer")]
public sealed class RetryTestsSqlServer() : RetryTests(new SqlStoreFactory());

/// <summary>Retry tests against the PostgreSQL provider.</summary>
[Trait("Provider", "PostgreSql")]
public sealed class RetryTestsPostgreSql() : RetryTests(new PostgresStoreFactory());

/// <summary>Retry tests against the SQLite provider.</summary>
[Trait("Provider", "Sqlite")]
public sealed class RetryTestsSqlite() : RetryTests(new SqliteStoreFactory());
