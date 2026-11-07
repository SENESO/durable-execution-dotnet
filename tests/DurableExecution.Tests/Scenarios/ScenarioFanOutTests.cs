using System.Text.Json;
using DurableExecution.Execution;

namespace DurableExecution.Tests.Scenarios;

/// <summary>
/// The fan-out combinator (§5.3) across real process kills. One workflow,
/// three parallel ChargeCard branches against the idempotent gateway, with
/// per-branch merchant references (order-1001-fan1/2/3) and per-branch
/// execution counters — so exactly which branches executed, charged, and
/// recorded is provable from state, per branch. Branch timing inside the
/// host is scenario scaffolding that stages the kill point; the workflow's
/// activities, inputs and order never change between runs.
///
/// Written once, run per provider (§6.5 conformance).
/// </summary>
public abstract class ScenarioFanOutTests(ScenarioProvider provider) : IDisposable
{
    protected ScenarioProvider Provider { get; } = provider;

    public void Dispose() => Provider.Dispose();

    private const string Instance = "order-1001";

    private int Attempts(string state, long seq) =>
        StateAssert.Events(Provider.History(state), "ActivityAttemptStarted", seq).Count;

    private static string ExpectedKey(long seq) =>
        Fingerprints.DeriveIdempotencyKey("default", Instance, seq, "ChargeCard");

    private void ScheduleKeysAreAdr012(string state)
    {
        for (long seq = 1; seq <= 3; seq++)
        {
            var scheduled = Assert.Single(StateAssert.Events(Provider.History(state), "ActivityScheduled", seq));
            Assert.Equal(ExpectedKey(seq), scheduled.GetProperty("idempotencyKey").GetString());
        }
    }

    private void GatewayOutcomesForRef(string state, int branch, params string[] expected)
    {
        string merchantRef = $"{Instance}-fan{branch}";
        var rows = StateAssert.GatewayCalls(state)
            .Where(r => r.GetProperty("merchantRef").GetString() == merchantRef)
            .ToList();
        Assert.All(rows, r => Assert.Equal(ExpectedKey(branch), r.GetProperty("key").GetString()));
        var outcomes = rows.Select(r => r.GetProperty("phase").GetString())
            .Where(p => p is "charged" or "duplicate-key").ToArray();
        Assert.True(outcomes.SequenceEqual(expected),
            $"fan{branch} outcomes == [{string.Join(", ", expected)}] (got [{string.Join(", ", outcomes)}])");
    }

    [Fact]
    public void L_KillMidFanOut_ResumeCompletesTheRest_ExactlyOncePerBranchKey()
    {
        string state = Provider.FreshState("L");

        // Branch 1 completes; branch 2 charges and is killed inside its
        // window (effect real, result unrecorded); branch 3 is still in
        // flight and never reaches its effect.
        var run1 = Provider.Run(state, crashMode: "fanout-kill-mid", variant: "fanout");
        Assert.True(run1.WasKilled, $"run1 killed by the crash point (exit {run1.ExitCode})");

        Assert.Equal(3, StateAssert.Events(Provider.History(state), "ActivityScheduled").Count);
        Assert.Single(StateAssert.Events(Provider.History(state), "ActivityCompleted", seq: 1));
        Assert.Empty(StateAssert.Events(Provider.History(state), "ActivityCompleted", seq: 2));
        Assert.Empty(StateAssert.Events(Provider.History(state), "ActivityCompleted", seq: 3));
        var counters1 = StateAssert.Counters(state);
        StateAssert.Need(counters1, "fanExecutions1", 1);
        StateAssert.Need(counters1, "fanExecutions2", 1);
        StateAssert.Need(counters1, "chargesApplied", 2);
        Assert.False(counters1.ContainsKey("fanExecutions3"),
            "branch 3 was in flight when the kill landed — its effect never ran");

        var run2 = Provider.Run(state, variant: "fanout");
        Assert.Equal(0, run2.ExitCode);

        var counters = StateAssert.Counters(state);
        StateAssert.Need(counters, "fanExecutions1", 1); // replayed from history, not re-executed
        StateAssert.Need(counters, "fanExecutions2", 2); // re-executed under its recorded key
        StateAssert.Need(counters, "fanExecutions3", 1); // executed once, after the resume
        StateAssert.Need(counters, "chargesApplied", 3); // one applied charge per branch, total
        Assert.Equal(1, Attempts(state, 1));
        Assert.Equal(2, Attempts(state, 2));
        Assert.Equal(2, Attempts(state, 3)); // the killed in-flight attempt + the executing one

        ScheduleKeysAreAdr012(state);
        GatewayOutcomesForRef(state, 1, "charged");
        GatewayOutcomesForRef(state, 2, "charged", "duplicate-key"); // same key -> deduplicated
        GatewayOutcomesForRef(state, 3, "charged");

        // The combined result lists the branches' transactions in schedule
        // order (fan1, fan2, fan3) — branch 1's value replayed from history,
        // the others fresh from the resume.
        var charges = StateAssert.GatewayState(state).GetProperty("Charges");
        Assert.Equal(3, charges.GetArrayLength());
        var txnByRef = charges.EnumerateArray().ToDictionary(
            c => c.GetProperty("Ref").GetString()!, c => c.GetProperty("Txn").GetString()!);
        string expected = string.Join(",",
            txnByRef[$"{Instance}-fan1"], txnByRef[$"{Instance}-fan2"], txnByRef[$"{Instance}-fan3"]);
        var completed = Assert.Single(StateAssert.Events(Provider.History(state), "WorkflowCompleted"));
        Assert.Equal(expected, completed.GetProperty("result").GetString());
        StateAssert.WorkflowCompletedExactlyOnce(Provider.History(state));
    }

    [Fact]
    public void M_KillWithEveryBranchInFlight_ResumeExecutesEachBranchOnce()
    {
        string state = Provider.FreshState("M");

        // The kill lands while all three branches are in flight: every
        // branch is scheduled and attempted, none has completed, and no
        // branch has reached the gateway at all.
        var run1 = Provider.Run(state, crashMode: "fanout-kill-early", variant: "fanout");
        Assert.True(run1.WasKilled, $"run1 killed by the crash point (exit {run1.ExitCode})");

        Assert.Equal(3, StateAssert.Events(Provider.History(state), "ActivityScheduled").Count);
        Assert.Empty(StateAssert.Events(Provider.History(state), "ActivityCompleted"));
        Assert.Equal(1, Attempts(state, 1));
        Assert.Equal(1, Attempts(state, 2));
        Assert.Equal(1, Attempts(state, 3));
        Assert.False(File.Exists(Path.Combine(state, "gateway-state.json")),
            "no branch reached the gateway before the kill");

        var run2 = Provider.Run(state, variant: "fanout");
        Assert.Equal(0, run2.ExitCode);

        var counters = StateAssert.Counters(state);
        StateAssert.Need(counters, "fanExecutions1", 1);
        StateAssert.Need(counters, "fanExecutions2", 1);
        StateAssert.Need(counters, "fanExecutions3", 1);
        StateAssert.Need(counters, "chargesApplied", 3);
        Assert.Equal(2, Attempts(state, 1));
        Assert.Equal(2, Attempts(state, 2));
        Assert.Equal(2, Attempts(state, 3));
        ScheduleKeysAreAdr012(state);
        GatewayOutcomesForRef(state, 1, "charged");
        GatewayOutcomesForRef(state, 2, "charged");
        GatewayOutcomesForRef(state, 3, "charged");
        StateAssert.WorkflowCompletedExactlyOnce(Provider.History(state));
    }

    [Fact]
    public void N_ChangedBranchInputAtResume_FingerprintViolationInsideTheFanOut()
    {
        string state = Provider.FreshState("N");

        // All three branches complete; the kill lands after the join, before
        // the workflow's own completion is recorded.
        var run1 = Provider.Run(state, crashMode: "fanout-after-join", variant: "fanout");
        Assert.True(run1.WasKilled, $"run1 killed by the crash point (exit {run1.ExitCode})");
        Assert.Equal(3, StateAssert.Events(Provider.History(state), "ActivityCompleted").Count);
        Assert.Empty(StateAssert.Events(Provider.History(state), "WorkflowCompleted"));

        // Resume with branch 2's input changed: the fan-out's scheduling
        // phase must refuse — a branch position is a recorded position.
        var run2 = Provider.Run(state, variant: "fanout",
            extraEnv: new Dictionary<string, string> { ["FAN_BRANCH2_AMOUNT"] = "9999" });
        Assert.Equal(4, run2.ExitCode);
        Assert.Contains("input fingerprint", run2.Output);

        // The refusal happened before any execution: nothing re-ran.
        var counters = StateAssert.Counters(state);
        StateAssert.Need(counters, "fanExecutions1", 1);
        StateAssert.Need(counters, "fanExecutions2", 1);
        StateAssert.Need(counters, "fanExecutions3", 1);
        StateAssert.Need(counters, "chargesApplied", 3);
        Assert.Empty(StateAssert.Events(Provider.History(state), "WorkflowCompleted"));
    }
}

/// <summary>Fan-out scenarios L–N against the file provider (the P0 evidence, unchanged).</summary>
[Trait("Provider", "File")]
public sealed class ScenarioFanOutTestsFile() : ScenarioFanOutTests(new FileScenarioProvider());

/// <summary>Fan-out scenarios L–N against the SQL Server provider (§6.5 conformance).</summary>
[Trait("Provider", "SqlServer")]
public sealed class ScenarioFanOutTestsSqlServer() : ScenarioFanOutTests(new SqlScenarioProvider());

/// <summary>Fan-out scenarios L–N against the PostgreSQL provider (§6.5 conformance).</summary>
[Trait("Provider", "PostgreSql")]
public sealed class ScenarioFanOutTestsPostgreSql() : ScenarioFanOutTests(new PostgresScenarioProvider());

/// <summary>Fan-out scenarios L–N against the SQLite provider (§6.5 conformance).</summary>
[Trait("Provider", "Sqlite")]
public sealed class ScenarioFanOutTestsSqlite() : ScenarioFanOutTests(new SqliteScenarioProvider());
