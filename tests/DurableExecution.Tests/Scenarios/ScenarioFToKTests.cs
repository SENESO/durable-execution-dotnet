namespace DurableExecution.Tests.Scenarios;

/// <summary>
/// Corruption refusal (F), determinism enforcement (G, I, J), loop identity
/// (H), and single-writer ownership (K). A damaged or contradicted history is
/// refused loudly — exit codes and markers are asserted, not just data.
///
/// Written once, run per provider (§6.5): the F scenarios damage the
/// completed history through the provider's own medium (rewriting lines of
/// the log file; updating or deleting rows of the table) and assert the
/// same refusal.
/// </summary>
public abstract class ScenarioFToKTests(ScenarioProvider provider) : IDisposable
{
    protected ScenarioProvider Provider { get; } = provider;

    public void Dispose() => Provider.Dispose();

    private string CompletedState(string scenario)
    {
        string state = Provider.FreshState(scenario);
        var setup = Provider.Run(state);
        Assert.Equal(0, setup.ExitCode);
        return state;
    }

    [Fact]
    public void F1_InvalidJsonInATerminatedRecord_IsRefusedNotDeleted()
    {
        string copy = Provider.CloneState(CompletedState("F"), "-f1");
        Provider.DamageHistory(copy, HistoryDamage.InvalidJsonRecord);

        var run = Provider.Run(copy);
        Assert.Equal(3, run.ExitCode);
        Assert.Contains("CORRUPT HISTORY", run.Output);
    }

    [Fact]
    public void F2_DeletedEvent_SequenceGap_IsRefused()
    {
        string copy = Provider.CloneState(CompletedState("F"), "-f2");
        Provider.DamageHistory(copy, HistoryDamage.DeleteSecondRecord);

        var run = Provider.Run(copy);
        Assert.Equal(3, run.ExitCode);
        Assert.Contains("CORRUPT HISTORY", run.Output);
    }

    [Fact]
    public void F3_TamperedContent_ValidJson_IsCaughtByTheChecksum()
    {
        string copy = Provider.CloneState(CompletedState("F"), "-f3");
        Assert.Contains("\"result\":\"reserved\"", Provider.HistoryText(copy));
        Provider.DamageHistory(copy, HistoryDamage.TamperRecordedResult);

        var run = Provider.Run(copy);
        Assert.Equal(3, run.ExitCode);
        Assert.Contains("checksum mismatch", run.Output);
    }

    [Fact]
    public void G_ChangedWorkflowDefinition_HaltsWithViolation()
    {
        string state = Provider.FreshState("G");
        var run1 = Provider.Run(state, crashMode: "after-complete:1");
        Assert.True(run1.WasKilled, $"run1 killed after command 1 (exit {run1.ExitCode})");

        var run2 = Provider.Run(state, variant: "v2");
        Assert.Equal(4, run2.ExitCode);
        Assert.Contains("NONDETERMINISM VIOLATION", run2.Output);
    }

    [Fact]
    public void H_LoopCommandsHaveDistinctKeys_AndEffectsAreNotDuplicated()
    {
        string state = Provider.FreshState("H");

        var run1 = Provider.Run(state, crashMode: "in-item-window:2", variant: "loop");
        Assert.True(run1.WasKilled, $"run1 killed inside item b's activity (exit {run1.ExitCode})");

        var run2 = Provider.Run(state, variant: "loop");
        Assert.Equal(0, run2.ExitCode);

        var counters = StateAssert.Counters(state);
        StateAssert.Need(counters, "processExecutions", 4); // item b's activity DID re-run

        var items = StateAssert.ItemsStore(state);
        Assert.Equal(3, items.Count); // exactly 3 distinct effects
        Assert.Equal(new[] { "a", "b", "c" }, items.Values.OrderBy(v => v).ToArray());

        var scheduled = StateAssert.Events(Provider.History(state), "ActivityScheduled", activity: "ProcessItem");
        Assert.Equal(new long[] { 1, 2, 3 },
            scheduled.Select(e => e.GetProperty("seq").GetInt64()).OrderBy(s => s).ToArray());
        Assert.Equal(3, scheduled.Select(e => e.GetProperty("idempotencyKey").GetString()).Distinct().Count());
        Assert.Equal(2, StateAssert.Events(Provider.History(state), "ActivityAttemptStarted", seq: 2).Count);
        Assert.Single(StateAssert.Events(Provider.History(state), "ActivityAttemptStarted", seq: 1));
        Assert.Single(StateAssert.Events(Provider.History(state), "ActivityAttemptStarted", seq: 3));
        StateAssert.WorkflowCompletedExactlyOnce(Provider.History(state));
        StateAssert.Consistency(state, Provider.History(state), "ProcessItem", "processExecutions");
    }

    [Fact]
    public void I_ChangedLoopItemInput_IsDetectedByFingerprint()
    {
        string state = Provider.FreshState("I");
        var run1 = Provider.Run(state, crashMode: "after-complete:2", variant: "loop");
        Assert.True(run1.WasKilled, $"run1 killed after item 2 (exit {run1.ExitCode})");

        var run2 = Provider.Run(state, variant: "loop",
            extraEnv: new Dictionary<string, string> { ["ITEMS"] = "x,b,c" });
        Assert.Equal(4, run2.ExitCode);
        Assert.Contains("input fingerprint changed", run2.Output);
    }

    [Fact]
    public void J_ChangedWorkflowInput_IsDetectedByFingerprint()
    {
        string state = Provider.FreshState("J");
        var run1 = Provider.Run(state, crashMode: "after-complete:1",
            extraEnv: new Dictionary<string, string> { ["AMOUNT"] = "1000" });
        Assert.True(run1.WasKilled, $"run1 (amount=1000) killed after command 1 (exit {run1.ExitCode})");

        var run2 = Provider.Run(state,
            extraEnv: new Dictionary<string, string> { ["AMOUNT"] = "5000" });
        Assert.Equal(4, run2.ExitCode);
        Assert.Contains("input fingerprint changed", run2.Output);
    }

    [Fact]
    public void K_SecondHostOnTheSameState_IsRefusedWhileTheLockIsHeld()
    {
        string state = Provider.FreshState("K");

        using var holder = Provider.Start(state,
            extraEnv: new Dictionary<string, string> { ["HOLD_SECONDS"] = "6" });
        Thread.Sleep(2000); // let the holder take the lock

        var refused = Provider.Run(state);
        Assert.Equal(5, refused.ExitCode);
        Assert.Contains("owned by another running host", refused.Output);

        Assert.True(holder.WaitForExit(15000), "lock holder exits");
        Assert.Equal(0, holder.ExitCode);

        var after = Provider.Run(state);
        Assert.Equal(0, after.ExitCode); // after release, a host can run the workflow
    }
}

/// <summary>Scenarios F–K against the file provider (the P0 evidence, unchanged).</summary>
[Trait("Provider", "File")]
public sealed class ScenarioFToKTestsFile() : ScenarioFToKTests(new FileScenarioProvider());

/// <summary>Scenarios F–K against the SQL Server provider (§6.5 conformance).</summary>
[Trait("Provider", "SqlServer")]
public sealed class ScenarioFToKTestsSqlServer() : ScenarioFToKTests(new SqlScenarioProvider());
