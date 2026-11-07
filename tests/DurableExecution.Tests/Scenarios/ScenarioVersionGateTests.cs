using System.Text.Json;

namespace DurableExecution.Tests.Scenarios;

/// <summary>
/// R — code-version gates across real process kills (P4), per provider (§6.5).
///
/// The TestHost's "versioned" variants run under the SAME definition id
/// (order/v1) as the default workflow — changed code, gated. R1: an
/// instance killed mid-flight resumes on gated code, which records its
/// marker live and takes the v2 branch for the unexecuted remainder,
/// with the gateway's dedup holding (one charge applied). R2: a gate
/// inserted before already-recorded work resolves to the default
/// version and records nothing — pre-gate history runs its recorded
/// branch to completion. R3: resuming on code whose supported range no
/// longer covers the recorded version is the loud, named refusal
/// (exit 7), with nothing executed. R4: the same code change WITHOUT a
/// gate is a determinism violation on replay (exit 4).
/// </summary>
public abstract class ScenarioVersionGateTests(ScenarioProvider provider) : IDisposable
{
    protected ScenarioProvider Provider { get; } = provider;

    public void Dispose() => Provider.Dispose();

    private List<JsonElement> History(string state) => Provider.History(state);

    private static bool HasMarker(IReadOnlyList<JsonElement> history, long version) =>
        history.Any(e => e.GetProperty("type").GetString() == "VersionMarker" &&
                         e.GetProperty("changeId").GetString() == "reserve-v2" &&
                         e.GetProperty("version").GetInt64() == version);

    [Fact]
    public void R1_KilledInstance_ResumesOnGatedCode_MarkerRecorded_DedupHolds()
    {
        string state = Provider.FreshState("R1");

        var killed = Provider.Run(state, crashMode: "in-charge-window", variant: "default");
        Assert.NotEqual(0, killed.ExitCode); // hard-killed mid-charge

        var resumed = Provider.Run(state, variant: "versioned");
        Assert.Equal(0, resumed.ExitCode);
        Assert.Contains("WORKFLOW COMPLETED", resumed.Output);
        Assert.Contains("gate 'reserve-v2' resolved to version 2", resumed.Output);
        Assert.Contains("chargesApplied=1", resumed.Output);   // the gateway deduped the re-execution
        Assert.Contains("chargeExecutions=2", resumed.Output); // the activity did re-run (at-least-once)

        var history = History(state);
        Assert.True(HasMarker(history, 2), "the gated resume must record its version marker");
        Assert.Contains(history, e => e.GetProperty("type").GetString() == "WorkflowCompleted");
    }

    [Fact]
    public void R2_GateInsertedBeforeRecordedWork_ResolvesDefault_RecordsNothing()
    {
        string state = Provider.FreshState("R2");

        var killed = Provider.Run(state, crashMode: "after-complete:1", variant: "default");
        Assert.NotEqual(0, killed.ExitCode); // died after the first command's completion was durable

        var resumed = Provider.Run(state, variant: "versioned-early");
        Assert.Equal(0, resumed.ExitCode);
        Assert.Contains("WORKFLOW COMPLETED", resumed.Output);
        Assert.Contains("gate 'reserve-v2' resolved to version -1", resumed.Output);
        Assert.Contains("chargeExecutions=1", resumed.Output); // the recorded charge never re-executed

        var history = History(state);
        Assert.DoesNotContain(history, e => e.GetProperty("type").GetString() == "VersionMarker");
        Assert.Contains(history, e => e.GetProperty("type").GetString() == "WorkflowCompleted");
    }

    [Fact]
    public void R3_RecordedVersionOutsideSupportedRange_IsTheNamedRefusal()
    {
        string state = Provider.FreshState("R3");

        // The gated code runs and dies after both commands complete: the
        // marker (version 2) is durable, the workflow completion is not.
        var killed = Provider.Run(state, crashMode: "after-complete:2", variant: "versioned");
        Assert.NotEqual(0, killed.ExitCode);
        Assert.True(HasMarker(History(state), 2));

        var refused = Provider.Run(state, variant: "versioned-capped");
        Assert.Equal(7, refused.ExitCode);
        Assert.Contains("VERSION GATE REFUSAL", refused.Output);
        Assert.Contains("'reserve-v2'", refused.Output);
        Assert.Contains("recorded at version 2", refused.Output);

        var history = History(state);
        Assert.DoesNotContain(history, e => e.GetProperty("type").GetString() == "WorkflowCompleted");
    }

    [Fact]
    public void R4_UngatedChangeOnResume_IsADeterminismViolation()
    {
        string state = Provider.FreshState("R4");

        var killed = Provider.Run(state, crashMode: "after-complete:1", variant: "default");
        Assert.NotEqual(0, killed.ExitCode);

        var resumed = Provider.Run(state, variant: "mutated");
        Assert.Equal(4, resumed.ExitCode);
        Assert.Contains("NONDETERMINISM VIOLATION", resumed.Output);
    }
}

[Trait("Provider", "File")]
public sealed class ScenarioVersionGateTestsFile() : ScenarioVersionGateTests(new FileScenarioProvider());

[Trait("Provider", "SqlServer")]
public sealed class ScenarioVersionGateTestsSqlServer() : ScenarioVersionGateTests(new SqlScenarioProvider());

[Trait("Provider", "PostgreSql")]
public sealed class ScenarioVersionGateTestsPostgreSql() : ScenarioVersionGateTests(new PostgresScenarioProvider());

[Trait("Provider", "Sqlite")]
public sealed class ScenarioVersionGateTestsSqlite() : ScenarioVersionGateTests(new SqliteScenarioProvider());
