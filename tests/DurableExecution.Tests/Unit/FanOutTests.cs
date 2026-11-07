using DurableExecution.Execution;
using DurableExecution.History;

namespace DurableExecution.Tests.Unit;

/// <summary>
/// The fan-out combinator (§5.3), exercised in-process: branches are scheduled
/// in list order with ADR-012 keys per position, results combine in schedule
/// order regardless of completion order, a faulted branch keeps the other
/// branches' recorded completions and re-executes alone on resume under its
/// recorded key, and a changed branch definition is caught by the same
/// fingerprint checks as a sequential call. (The same semantics across real
/// process kills are the FanOut scenarios.)
/// </summary>
public class FanOutTests
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

    private static IReadOnlyList<HistoryEvent> LoadHistory(string dir) =>
        new FileHistoryStore(dir).Load().Events;

    [Fact]
    public async Task ResultsCombineInScheduleOrder_RegardlessOfCompletionOrder()
    {
        string dir = FreshDir();
        var started = new TaskCompletionSource[3];
        var gates = new TaskCompletionSource[3];
        for (int i = 0; i < 3; i++)
        {
            started[i] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gates[i] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        string[]? joined = null;
        Func<WorkflowContext, Task<object?>> body = async ctx =>
        {
            var calls = new List<ActivityCall<string>>();
            for (int i = 0; i < 3; i++)
            {
                int branch = i;
                calls.Add(new ActivityCall<string>("ChargeCard", new { branch }, async actx =>
                {
                    started[branch].TrySetResult();
                    await gates[branch].Task;
                    return "r" + (branch + 1);
                }));
            }
            string[] results = await ctx.CallAllAsync(calls);
            joined = results;
            return string.Join(",", results);
        };

        using var host = OpenHost(dir);
        var run = host.RunAsync("order/v1", "order-1001", new { amount = 1000 }, body);
        await Task.WhenAll(started.Select(s => s.Task)).WaitAsync(TimeSpan.FromSeconds(30));

        // Complete the branches in reverse order: 3, then 2, then 1.
        gates[2].TrySetResult();
        await Task.Delay(150);
        gates[1].TrySetResult();
        await Task.Delay(150);
        gates[0].TrySetResult();

        Assert.Equal(WorkflowRunOutcome.Completed, await run.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(new[] { "r1", "r2", "r3" }, joined!); // schedule order, not arrival order

        // And the completions really did land in history in reverse order —
        // the ordering above is the combinator's doing, not the clock's.
        var completionOrder = LoadHistory(dir)
            .Where(e => e.Type == HistoryEventTypes.ActivityCompleted)
            .Select(e => e.CommandSequence!.Value).ToArray();
        Assert.Equal(new long[] { 3, 2, 1 }, completionOrder);
    }

    [Fact]
    public async Task BranchKeys_AreTheAdr012KeysOfTheirPositions()
    {
        string dir = FreshDir();
        var seen = new Dictionary<long, ActivityContext>();

        Func<WorkflowContext, Task<object?>> body = async ctx =>
        {
            await ctx.CallActivityAsync("ReserveStock", new { orderId = "order-1001" }, actx =>
            {
                seen[actx.CommandSequence] = actx;
                return Task.FromResult("reserved");
            });
            await ctx.CallAllAsync(new List<ActivityCall<string>>
            {
                new("ChargeCard", new { amount = 100 }, actx => { seen[actx.CommandSequence] = actx; return Task.FromResult("t1"); }),
                new("ChargeCard", new { amount = 200 }, actx => { seen[actx.CommandSequence] = actx; return Task.FromResult("t2"); }),
            });
            return null;
        };

        using var host = OpenHost(dir);
        await host.RunAsync("order/v1", "order-1001", new { amount = 1000 }, body);

        // The fan-out continues the instance's command sequence: the branches
        // are commands 2 and 3, keyed exactly as sequential calls there
        // would be (ADR-012 is a function of position, not of call shape).
        Assert.Equal(
            Fingerprints.DeriveIdempotencyKey("default", "order-1001", 2, "ChargeCard"),
            seen[2].IdempotencyKey);
        Assert.Equal(
            Fingerprints.DeriveIdempotencyKey("default", "order-1001", 3, "ChargeCard"),
            seen[3].IdempotencyKey);
        Assert.Equal(1, seen[2].Attempt);
        Assert.Equal(1, seen[3].Attempt);
    }

    [Fact]
    public async Task PartialCompletion_ResumeExecutesOnlyTheFaultedBranch_WithItsRecordedKey()
    {
        string dir = FreshDir();
        var executions = new int[3];
        var branch2Keys = new List<string>();
        bool branch2Faults = true;
        string? joined = null;

        Func<WorkflowContext, Task<object?>> body = async ctx =>
        {
            var calls = new List<ActivityCall<string>>();
            for (int i = 0; i < 3; i++)
            {
                int branch = i;
                calls.Add(new ActivityCall<string>("ChargeCard", new { amount = (branch + 1) * 100 }, actx =>
                {
                    executions[branch]++;
                    if (branch == 1)
                    {
                        branch2Keys.Add(actx.IdempotencyKey);
                        if (branch2Faults)
                            throw new SimulatedCrashException(); // effect may have happened; no result recorded
                    }
                    return Task.FromResult("r" + (branch + 1));
                }));
            }
            string[] results = await ctx.CallAllAsync(calls);
            joined = string.Join(",", results);
            return joined;
        };

        using (var host = OpenHost(dir))
        {
            var ex = await Assert.ThrowsAsync<AggregateException>(() =>
                host.RunAsync("fanout/v1", "order-1001", new { amount = 1000 }, body));
            Assert.IsType<SimulatedCrashException>(Assert.Single(ex.InnerExceptions));
        }
        Assert.Equal(new[] { 1, 1, 1 }, executions); // every branch ran once; two recorded completions

        branch2Faults = false;
        using (var host = OpenHost(dir))
            Assert.Equal(WorkflowRunOutcome.Completed,
                await host.RunAsync("fanout/v1", "order-1001", new { amount = 1000 }, body));

        Assert.Equal(new[] { 1, 2, 1 }, executions); // only the faulted branch re-executed
        Assert.Equal("r1,r2,r3", joined);
        Assert.Equal(2, branch2Keys.Count);
        Assert.Equal(branch2Keys[0], branch2Keys[1]); // the recorded key, verbatim, on the retry
        Assert.Equal(
            Fingerprints.DeriveIdempotencyKey("default", "order-1001", 2, "ChargeCard"),
            branch2Keys[0]);
    }

    [Fact]
    public async Task ChangedBranchInputAtResume_ThrowsNonDeterminismViolation()
    {
        string dir = FreshDir();
        int branch2Amount = 200;

        Func<WorkflowContext, Task<object?>> Body() => async ctx =>
        {
            await ctx.CallAllAsync(new List<ActivityCall<string>>
            {
                new("ChargeCard", new { amount = 100 }, actx => Task.FromResult("r1")),
                new("ChargeCard", new { amount = branch2Amount }, actx => throw new SimulatedCrashException()),
                new("ChargeCard", new { amount = 300 }, actx => Task.FromResult("r3")),
            });
            return null;
        };

        using (var host = OpenHost(dir))
            await Assert.ThrowsAsync<AggregateException>(() =>
                host.RunAsync("fanout/v1", "order-1001", new { amount = 1000 }, Body()));

        branch2Amount = 999; // the code now asks branch 2's recorded position for different inputs
        using var resumed = OpenHost(dir);
        var ex = await Assert.ThrowsAsync<NonDeterminismViolationException>(() =>
            resumed.RunAsync("fanout/v1", "order-1001", new { amount = 1000 }, Body()));
        Assert.Contains("command 2", ex.Message);
        Assert.Contains("input fingerprint changed", ex.Message);
    }

    [Fact]
    public async Task ChangedBranchActivityAtResume_ThrowsNonDeterminismViolation()
    {
        string dir = FreshDir();
        string branch2Activity = "ChargeCard";

        Func<WorkflowContext, Task<object?>> Body() => async ctx =>
        {
            await ctx.CallAllAsync(new List<ActivityCall<string>>
            {
                new("ChargeCard", new { amount = 100 }, actx => Task.FromResult("r1")),
                new(branch2Activity, new { amount = 200 }, actx => throw new SimulatedCrashException()),
                new("ChargeCard", new { amount = 300 }, actx => Task.FromResult("r3")),
            });
            return null;
        };

        using (var host = OpenHost(dir))
            await Assert.ThrowsAsync<AggregateException>(() =>
                host.RunAsync("fanout/v1", "order-1001", new { amount = 1000 }, Body()));

        branch2Activity = "ChargeCardV2"; // same position, different activity identity
        using var resumed = OpenHost(dir);
        var ex = await Assert.ThrowsAsync<NonDeterminismViolationException>(() =>
            resumed.RunAsync("fanout/v1", "order-1001", new { amount = 1000 }, Body()));
        Assert.Contains("command 2 was recorded as 'ChargeCard'", ex.Message);
    }

    [Fact]
    public async Task DuplicateBranches_GetDistinctKeys_AndBothExecute()
    {
        string dir = FreshDir();
        var keys = new List<string>();
        int executions = 0;

        Func<WorkflowContext, Task<object?>> body = async ctx =>
        {
            await ctx.CallAllAsync(new List<ActivityCall<string>>
            {
                new("ProcessItem", new { item = "x" }, actx => { keys.Add(actx.IdempotencyKey); executions++; return Task.FromResult("a"); }),
                new("ProcessItem", new { item = "x" }, actx => { keys.Add(actx.IdempotencyKey); executions++; return Task.FromResult("b"); }),
            });
            return null;
        };

        using var host = OpenHost(dir);
        await host.RunAsync("fanout/v1", "order-1001", new { amount = 1000 }, body);

        Assert.Equal(2, executions); // identical branches are two commands, not one deduplicated call
        Assert.Distinct(keys);
        Assert.Contains(Fingerprints.DeriveIdempotencyKey("default", "order-1001", 1, "ProcessItem"), keys);
        Assert.Contains(Fingerprints.DeriveIdempotencyKey("default", "order-1001", 2, "ProcessItem"), keys);
    }

    [Fact]
    public async Task EmptyFanOut_ReturnsEmpty_AndConsumesNoCommandPositions()
    {
        string dir = FreshDir();
        long sequentialSeq = 0;

        Func<WorkflowContext, Task<object?>> body = async ctx =>
        {
            string[] results = await ctx.CallAllAsync(new List<ActivityCall<string>>());
            Assert.Empty(results);
            await ctx.CallActivityAsync("ChargeCard", new { amount = 100 }, actx =>
            {
                sequentialSeq = actx.CommandSequence;
                return Task.FromResult("t");
            });
            return null;
        };

        using var host = OpenHost(dir);
        await host.RunAsync("fanout/v1", "order-1001", new { amount = 1000 }, body);
        Assert.Equal(1, sequentialSeq); // the empty fan-out scheduled nothing
    }

    [Fact]
    public async Task NonGenericFanOut_ExecutesEveryBranch_AndReplaysThemSilently()
    {
        string dir = FreshDir();
        var executions = new int[2];
        bool crashAfterJoin = true;

        Func<WorkflowContext, Task<object?>> body = async ctx =>
        {
            await ctx.CallAllAsync(new List<ActivityCall>
            {
                new("ReserveStock", new { slot = 1 }, actx => { executions[0]++; return Task.CompletedTask; }),
                new("ReserveStock", new { slot = 2 }, actx => { executions[1]++; return Task.CompletedTask; }),
            });
            if (crashAfterJoin)
            {
                crashAfterJoin = false;
                throw new SimulatedCrashException(); // join is durable; the workflow is not
            }
            return null;
        };

        using (var host = OpenHost(dir))
            await Assert.ThrowsAsync<SimulatedCrashException>(() =>
                host.RunAsync("fanout/v1", "order-1001", new { amount = 1000 }, body));

        using (var host = OpenHost(dir))
            Assert.Equal(WorkflowRunOutcome.Completed,
                await host.RunAsync("fanout/v1", "order-1001", new { amount = 1000 }, body));

        Assert.Equal(new[] { 1, 1 }, executions); // both branches' results came from history on resume
    }
}
