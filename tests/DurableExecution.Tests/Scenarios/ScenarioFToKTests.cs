using System.Diagnostics;

namespace DurableExecution.Tests.Scenarios;

/// <summary>
/// Corruption refusal (F), determinism enforcement (G, I, J), loop identity
/// (H), and single-writer ownership (K). A damaged or contradicted history is
/// refused loudly — exit codes and markers are asserted, not just data.
/// </summary>
public class ScenarioFToKTests
{
    private static string CompletedState(string scenario)
    {
        string state = HostProcess.FreshState(scenario);
        var setup = HostProcess.Run(state);
        Assert.Equal(0, setup.ExitCode);
        return state;
    }

    private static string CopyState(string state, string suffix)
    {
        string copy = state + suffix;
        Directory.CreateDirectory(copy);
        foreach (string file in Directory.GetFiles(state))
            File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
        return copy;
    }

    private static void RewriteHistory(string state, Func<List<string>, List<string>> edit)
    {
        string path = Path.Combine(state, "history.log");
        var lines = File.ReadAllLines(path).ToList();
        File.WriteAllText(path, string.Join("\n", edit(lines)) + "\n");
    }

    [Fact]
    public void F1_InvalidJsonInATerminatedRecord_IsRefusedNotDeleted()
    {
        string copy = CopyState(CompletedState("F"), "-f1");
        RewriteHistory(copy, lines => { lines[2] = "{this is not valid json"; return lines; });

        var run = HostProcess.Run(copy);
        Assert.Equal(3, run.ExitCode);
        Assert.Contains("CORRUPT HISTORY", run.Output);
    }

    [Fact]
    public void F2_DeletedEvent_SequenceGap_IsRefused()
    {
        string copy = CopyState(CompletedState("F"), "-f2");
        RewriteHistory(copy, lines => { lines.RemoveAt(1); return lines; });

        var run = HostProcess.Run(copy);
        Assert.Equal(3, run.ExitCode);
        Assert.Contains("CORRUPT HISTORY", run.Output);
    }

    [Fact]
    public void F3_TamperedContent_ValidJson_IsCaughtByTheChecksum()
    {
        string copy = CopyState(CompletedState("F"), "-f3");
        string path = Path.Combine(copy, "history.log");
        string text = File.ReadAllText(path);
        Assert.Contains("\"result\":\"reserved\"", text);
        File.WriteAllText(path, text.Replace("\"result\":\"reserved\"", "\"result\":\"tampered\""));

        var run = HostProcess.Run(copy);
        Assert.Equal(3, run.ExitCode);
        Assert.Contains("checksum mismatch", run.Output);
    }

    [Fact]
    public void G_ChangedWorkflowDefinition_HaltsWithViolation()
    {
        string state = HostProcess.FreshState("G");
        var run1 = HostProcess.Run(state, crashMode: "after-complete:1");
        Assert.True(run1.WasKilled, $"run1 killed after command 1 (exit {run1.ExitCode})");

        var run2 = HostProcess.Run(state, variant: "v2");
        Assert.Equal(4, run2.ExitCode);
        Assert.Contains("NONDETERMINISM VIOLATION", run2.Output);
    }

    [Fact]
    public void H_LoopCommandsHaveDistinctKeys_AndEffectsAreNotDuplicated()
    {
        string state = HostProcess.FreshState("H");

        var run1 = HostProcess.Run(state, crashMode: "in-item-window:2", variant: "loop");
        Assert.True(run1.WasKilled, $"run1 killed inside item b's activity (exit {run1.ExitCode})");

        var run2 = HostProcess.Run(state, variant: "loop");
        Assert.Equal(0, run2.ExitCode);

        var counters = StateAssert.Counters(state);
        StateAssert.Need(counters, "processExecutions", 4); // item b's activity DID re-run

        var items = StateAssert.ItemsStore(state);
        Assert.Equal(3, items.Count); // exactly 3 distinct effects
        Assert.Equal(new[] { "a", "b", "c" }, items.Values.OrderBy(v => v).ToArray());

        var scheduled = StateAssert.Events(state, "ActivityScheduled", activity: "ProcessItem");
        Assert.Equal(new long[] { 1, 2, 3 },
            scheduled.Select(e => e.GetProperty("seq").GetInt64()).OrderBy(s => s).ToArray());
        Assert.Equal(3, scheduled.Select(e => e.GetProperty("idempotencyKey").GetString()).Distinct().Count());
        Assert.Equal(2, StateAssert.Events(state, "ActivityAttemptStarted", seq: 2).Count);
        Assert.Single(StateAssert.Events(state, "ActivityAttemptStarted", seq: 1));
        Assert.Single(StateAssert.Events(state, "ActivityAttemptStarted", seq: 3));
        StateAssert.WorkflowCompletedExactlyOnce(state);
        StateAssert.Consistency(state, "ProcessItem", "processExecutions");
    }

    [Fact]
    public void I_ChangedLoopItemInput_IsDetectedByFingerprint()
    {
        string state = HostProcess.FreshState("I");
        var run1 = HostProcess.Run(state, crashMode: "after-complete:2", variant: "loop");
        Assert.True(run1.WasKilled, $"run1 killed after item 2 (exit {run1.ExitCode})");

        var run2 = HostProcess.Run(state, variant: "loop",
            extraEnv: new Dictionary<string, string> { ["ITEMS"] = "x,b,c" });
        Assert.Equal(4, run2.ExitCode);
        Assert.Contains("input fingerprint changed", run2.Output);
    }

    [Fact]
    public void J_ChangedWorkflowInput_IsDetectedByFingerprint()
    {
        string state = HostProcess.FreshState("J");
        var run1 = HostProcess.Run(state, crashMode: "after-complete:1",
            extraEnv: new Dictionary<string, string> { ["AMOUNT"] = "1000" });
        Assert.True(run1.WasKilled, $"run1 (amount=1000) killed after command 1 (exit {run1.ExitCode})");

        var run2 = HostProcess.Run(state,
            extraEnv: new Dictionary<string, string> { ["AMOUNT"] = "5000" });
        Assert.Equal(4, run2.ExitCode);
        Assert.Contains("input fingerprint changed", run2.Output);
    }

    [Fact]
    public void K_SecondHostOnTheSameState_IsRefusedWhileTheLockIsHeld()
    {
        string state = HostProcess.FreshState("K");

        using var holder = HostProcess.Start(state,
            extraEnv: new Dictionary<string, string> { ["HOLD_SECONDS"] = "6" });
        Thread.Sleep(2000); // let the holder take the lock

        var refused = HostProcess.Run(state);
        Assert.Equal(5, refused.ExitCode);
        Assert.Contains("owned by another running host", refused.Output);

        Assert.True(holder.WaitForExit(15000), "lock holder exits");
        Assert.Equal(0, holder.ExitCode);

        var after = HostProcess.Run(state);
        Assert.Equal(0, after.ExitCode); // after release, a host can run the workflow
    }
}
