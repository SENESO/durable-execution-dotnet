namespace DurableExecution.Tests.Scenarios;

/// <summary>
/// Foundation §7.3 scenarios 1–3 in their P0 minimal form (plus the clean run
/// and the torn-write case): kill the host at a checkpoint boundary or inside
/// the charge window, restart it, and assert the contract — completed work is
/// not re-executed, unrecorded work re-executes under the same idempotency
/// key, and the gateway outcomes match §2 exactly for each stub mode.
/// </summary>
public class ScenarioAToETests
{
    private static void CommonChecks(string state)
    {
        StateAssert.Consistency(state, "ChargeCard", "chargeExecutions");
        StateAssert.WorkflowCompletedExactlyOnce(state);
    }

    [Fact]
    public void A_KillAfterCompletionRecorded_CompletedWorkIsNotReExecuted()
    {
        string state = HostProcess.FreshState("A");

        var run1 = HostProcess.Run(state, crashMode: "after-complete:1");
        Assert.True(run1.WasKilled, $"run1 killed by the crash point (exit {run1.ExitCode})");

        var run2 = HostProcess.Run(state);
        Assert.Equal(0, run2.ExitCode);

        var counters = StateAssert.Counters(state);
        StateAssert.Need(counters, "chargeExecutions", 1);
        StateAssert.Need(counters, "chargesApplied", 1);
        StateAssert.Need(counters, "reserveExecutions", 1);
        Assert.Single(StateAssert.GatewayCalls(state, phase: "call"));
        Assert.Single(StateAssert.Events(state, "ActivityScheduled", seq: 1));
        CommonChecks(state);
    }

    [Fact]
    public void B_KillInsideChargeWindow_IdempotentGateway_SingleChargeSameKey()
    {
        string state = HostProcess.FreshState("B");

        var run1 = HostProcess.Run(state, crashMode: "in-charge-window");
        Assert.True(run1.WasKilled, $"run1 killed by the crash point (exit {run1.ExitCode})");

        var run2 = HostProcess.Run(state);
        Assert.Equal(0, run2.ExitCode);

        var counters = StateAssert.Counters(state);
        StateAssert.Need(counters, "chargeExecutions", 2); // at-least-once: the activity DID re-run
        StateAssert.Need(counters, "chargesApplied", 1);   // the gateway deduplicated by the stable key
        StateAssert.Need(counters, "reserveExecutions", 1);
        StateAssert.ChargeKeyChecks(state, "charged", "duplicate-key");
        Assert.Single(StateAssert.Events(state, "ActivityScheduled", seq: 1)); // attempts are not re-scheduled
        Assert.Equal(2, StateAssert.Events(state, "ActivityAttemptStarted", seq: 1).Count);
        CommonChecks(state);
    }

    [Fact]
    public void C_CleanRun_DuplicateInvocationIsANoOp()
    {
        string state = HostProcess.FreshState("C");

        var run1 = HostProcess.Run(state);
        Assert.Equal(0, run1.ExitCode);

        var run2 = HostProcess.Run(state);
        Assert.Equal(0, run2.ExitCode);
        Assert.Contains("already completed", run2.Output);

        var counters = StateAssert.Counters(state);
        StateAssert.Need(counters, "chargeExecutions", 1);
        StateAssert.Need(counters, "chargesApplied", 1);
        StateAssert.Need(counters, "reserveExecutions", 1);
        Assert.Single(StateAssert.GatewayCalls(state, phase: "call"));
        CommonChecks(state);
    }

    [Fact]
    public void D_KillDuringWrite_TornTailDiscardedAndReExecuted()
    {
        string state = HostProcess.FreshState("D");

        var run1 = HostProcess.Run(state, crashMode: "during-write");
        Assert.True(run1.WasKilled, $"run1 killed mid-write (exit {run1.ExitCode})");

        var run2 = HostProcess.Run(state);
        Assert.Equal(0, run2.ExitCode);
        Assert.Contains("torn tail", run2.Output); // unterminated != committed, and it is reported

        var counters = StateAssert.Counters(state);
        StateAssert.Need(counters, "chargeExecutions", 2); // torn completion discarded -> re-executed
        StateAssert.Need(counters, "chargesApplied", 1);
        StateAssert.Need(counters, "reserveExecutions", 1);
        StateAssert.ChargeKeyChecks(state, "charged", "duplicate-key");
        _ = StateAssert.History(state); // fully parseable after torn-tail repair
        CommonChecks(state);
    }

    [Fact]
    public void E_NonIdempotentGateway_DuplicateHappensAndReconciliationCompensatesOnce()
    {
        string state = HostProcess.FreshState("E");

        var run1 = HostProcess.Run(state, crashMode: "in-charge-window", gatewayMode: "nonidempotent");
        Assert.True(run1.WasKilled, $"run1 killed by the crash point (exit {run1.ExitCode})");

        var run2 = HostProcess.Run(state, gatewayMode: "nonidempotent");
        Assert.Equal(0, run2.ExitCode); // workflow completes; the duplicate already happened

        var run3 = HostProcess.Run(state, crashMode: "reconcile-after-refund",
            gatewayMode: "nonidempotent", args: new[] { "reconcile" });
        Assert.True(run3.WasKilled, $"reconciliation itself killed after the refund (exit {run3.ExitCode})");

        var run4 = HostProcess.Run(state, gatewayMode: "nonidempotent", args: new[] { "reconcile" });
        Assert.Equal(0, run4.ExitCode);
        Assert.Contains("charges found for ONE logical purchase", run4.Output);

        var counters = StateAssert.Counters(state);
        StateAssert.Need(counters, "chargeExecutions", 2);
        StateAssert.Need(counters, "chargesApplied", 2); // the duplicate REALLY happened — the engine does not hide it
        StateAssert.Need(counters, "refundsApplied", 1); // reconciliation compensated exactly once
        StateAssert.ChargeKeyChecks(state, "charged-ignoring-key", "charged-ignoring-key");

        var gateway = StateAssert.GatewayState(state);
        var charges = gateway.GetProperty("Charges");
        var refunds = gateway.GetProperty("Refunds");
        Assert.Equal(2, charges.GetArrayLength());
        Assert.Equal(1, refunds.GetArrayLength()); // exactly one refund, even though reconcile crashed once
        var chargeTxns = charges.EnumerateArray().Select(c => c.GetProperty("Txn").GetString()).ToHashSet();
        Assert.Contains(refunds[0].GetProperty("ForTxn").GetString(), chargeTxns);

        Assert.Equal(1, StateAssert.Reconciliation(state).GetProperty("duplicatesRefunded").GetInt32());
        Assert.Equal(1, counters["chargesApplied"] - counters["refundsApplied"]); // net effect: one charge
        CommonChecks(state);
    }
}
