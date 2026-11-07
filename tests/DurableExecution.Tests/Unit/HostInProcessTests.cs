using DurableExecution.Execution;
using DurableExecution.History;

namespace DurableExecution.Tests.Unit;

/// <summary>
/// The §14 P0 acceptance shape, exercised in-process: the minimal reference
/// workflow (charge, then reserve stock), crash-and-resume semantics with
/// execution counters as proof, fingerprint enforcement at resume, and
/// single-writer ownership. (The same scenarios run across real process kills
/// in the Scenarios classes; here the host is abandoned mid-flight instead.)
/// </summary>
public class HostInProcessTests
{
    private static string FreshDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "p0-unit", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static WorkflowHost OpenHost(string dir) =>
        new(new FileHistoryStore(dir), dir);

    private sealed class SimulatedCrashException : Exception { }

    [Fact]
    public async Task MinimalWorkflow_Completes_AndReinvocationExecutesNothing()
    {
        string dir = FreshDir();
        int chargeExecutions = 0, reserveExecutions = 0;

        Func<WorkflowContext, Task<object?>> body = async ctx =>
        {
            string txn = await ctx.CallActivityAsync("ChargeCard", new { amount = 1000 }, actx =>
            {
                chargeExecutions++;
                return Task.FromResult("txn-1");
            });
            await ctx.CallActivityAsync("ReserveStock", new { orderId = "order-1001" }, actx =>
            {
                reserveExecutions++;
                return Task.FromResult("reserved");
            });
            return txn;
        };

        using (var host = OpenHost(dir))
            Assert.Equal(WorkflowRunOutcome.Completed,
                await host.RunAsync("order/v1", "order-1001", new { amount = 1000 }, body));

        using (var host = OpenHost(dir))
            Assert.Equal(WorkflowRunOutcome.AlreadyCompleted,
                await host.RunAsync("order/v1", "order-1001", new { amount = 1000 }, body));

        Assert.Equal(1, chargeExecutions);  // proven by counters, not by inspection
        Assert.Equal(1, reserveExecutions);
    }

    [Fact]
    public async Task ResumeAfterCrash_CompletedActivityIsNotReExecuted_PendingOneIs()
    {
        string dir = FreshDir();
        int chargeExecutions = 0, reserveExecutions = 0;
        bool crashed = false;

        Func<WorkflowContext, Task<object?>> body = async ctx =>
        {
            await ctx.CallActivityAsync("ChargeCard", new { amount = 1000 }, actx =>
            {
                chargeExecutions++;
                return Task.FromResult("txn-1");
            });
            if (!crashed)
            {
                // The host dies here, after command 1's completion is durable,
                // before command 2 is ever scheduled.
                crashed = true;
                throw new SimulatedCrashException();
            }
            await ctx.CallActivityAsync("ReserveStock", new { orderId = "order-1001" }, actx =>
            {
                reserveExecutions++;
                return Task.FromResult("reserved");
            });
            return null;
        };

        using (var host = OpenHost(dir))
            await Assert.ThrowsAsync<SimulatedCrashException>(() =>
                host.RunAsync("order/v1", "order-1001", new { amount = 1000 }, body));

        using (var host = OpenHost(dir))
            Assert.Equal(WorkflowRunOutcome.Completed,
                await host.RunAsync("order/v1", "order-1001", new { amount = 1000 }, body));

        Assert.Equal(1, chargeExecutions); // command 1's result came from history
        Assert.Equal(1, reserveExecutions); // command 2 executed exactly once, after the crash
    }

    [Fact]
    public async Task Resume_ActivityReceivesTheSameRecordedKeyOnEveryAttempt()
    {
        string dir = FreshDir();
        var keysSeen = new List<string>();
        int executions = 0;

        Func<WorkflowContext, Task<object?>> body = async ctx =>
        {
            await ctx.CallActivityAsync("ChargeCard", new { amount = 1000 }, actx =>
            {
                keysSeen.Add(actx.IdempotencyKey);
                executions++;
                if (executions == 1)
                    throw new SimulatedCrashException(); // effect happened; result never recorded
                return Task.FromResult("txn-1");
            });
            return null;
        };

        using (var host = OpenHost(dir))
            await Assert.ThrowsAsync<SimulatedCrashException>(() =>
                host.RunAsync("order/v1", "order-1001", new { amount = 1000 }, body));

        using (var host = OpenHost(dir))
            await host.RunAsync("order/v1", "order-1001", new { amount = 1000 }, body);

        Assert.Equal(2, keysSeen.Count);
        Assert.Equal(keysSeen[0], keysSeen[1]); // the recorded key, verbatim
        Assert.Equal(
            Fingerprints.DeriveIdempotencyKey("default", "order-1001", 1, "ChargeCard"),
            keysSeen[0]);
    }

    [Fact]
    public async Task ResumeWithChangedWorkflowInput_ThrowsNonDeterminismViolation()
    {
        string dir = FreshDir();
        Func<WorkflowContext, Task<object?>> body = ctx => Task.FromResult<object?>(null);

        using (var host = OpenHost(dir))
            await Assert.ThrowsAsync<SimulatedCrashException>(() =>
                host.RunAsync("order/v1", "order-1001", new { amount = 1000 }, async ctx =>
                {
                    await body(ctx);
                    throw new SimulatedCrashException();
                }));

        using var resumed = OpenHost(dir);
        await Assert.ThrowsAsync<NonDeterminismViolationException>(() =>
            resumed.RunAsync("order/v1", "order-1001", new { amount = 5000 }, body));
    }

    [Fact]
    public async Task ResumeWithChangedDefinition_ThrowsNonDeterminismViolation()
    {
        string dir = FreshDir();
        using (var host = OpenHost(dir))
            await Assert.ThrowsAsync<SimulatedCrashException>(() =>
                host.RunAsync("order/v1", "order-1001", new { amount = 1000 }, ctx =>
                    throw new SimulatedCrashException()));

        using var resumed = OpenHost(dir);
        await Assert.ThrowsAsync<NonDeterminismViolationException>(() =>
            resumed.RunAsync("order/v2", "order-1001", new { amount = 1000 },
                ctx => Task.FromResult<object?>(null)));
    }

    [Fact]
    public async Task ResumeWithChangedCommandInput_ThrowsNonDeterminismViolation()
    {
        string dir = FreshDir();
        int amount = 1000;
        Func<WorkflowContext, Task<object?>> body = async ctx =>
        {
            await ctx.CallActivityAsync("ChargeCard", new { amount }, actx =>
            {
                if (amount == 1000)
                    throw new SimulatedCrashException(); // scheduled + attempted, never completed
                return Task.FromResult("txn-1");
            });
            return null;
        };

        using (var host = OpenHost(dir))
            await Assert.ThrowsAsync<SimulatedCrashException>(() =>
                host.RunAsync("order/v1", "order-1001", new { order = "x" }, body));

        amount = 5000; // the code now asks the recorded position for a different operation
        using var resumed = OpenHost(dir);
        var ex = await Assert.ThrowsAsync<NonDeterminismViolationException>(() =>
            resumed.RunAsync("order/v1", "order-1001", new { order = "x" }, body));
        Assert.Contains("input fingerprint changed", ex.Message);
    }

    [Fact]
    public void SecondHostOnSameState_IsRefusedWhileFirstHostLives()
    {
        string dir = FreshDir();
        using var first = OpenHost(dir);
        Assert.Throws<StateOwnedByAnotherHostException>(() => OpenHost(dir));
    }
}
