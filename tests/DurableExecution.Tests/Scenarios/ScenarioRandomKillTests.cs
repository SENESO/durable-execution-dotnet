namespace DurableExecution.Tests.Scenarios;

/// <summary>
/// S — seeded randomized kill scheduling, per provider (§6.5). For each
/// seed in the bounded set 1..6, the crash harness kills the workflow
/// host at a random append boundary drawn from the seed (printed on the
/// kill line), and the test resumes it — same seed, next run index —
/// until it completes. The resume loop must land on exactly the clean
/// run's contract: the workflow completes and the gateway applied
/// exactly one charge, however many times the host died. A failing
/// seed reproduces deterministically: the kill point of every run is
/// Random(seed * 31 + run) over [1..6], so re-running the scenario for
/// that seed replays the identical kill schedule.
/// </summary>
public abstract class ScenarioRandomKillTests(ScenarioProvider provider) : IDisposable
{
    protected ScenarioProvider Provider { get; } = provider;

    public void Dispose() => Provider.Dispose();

    [Fact]
    public void SeededKills_ResumeToTheSameContract()
    {
        foreach (int seed in new[] { 1, 2, 3, 4, 5, 6 })
        {
            string state = Provider.FreshState($"S{seed}");
            var env = new Dictionary<string, string> { ["CRASH_SEED"] = seed.ToString() };

            bool completed = false;
            for (int run = 0; run < 12 && !completed; run++)
            {
                env["CRASH_RUN"] = run.ToString();
                var result = Provider.Run(state, variant: "default", extraEnv: env);
                if (run == 0)
                {
                    // The draw always lands inside the first run's append
                    // window (1..6 of 8 appends): run 0 is always killed,
                    // and the kill line names the seed and run.
                    Assert.NotEqual(0, result.ExitCode);
                    Assert.Contains($"seed={seed} run=0", result.Output);
                }
                if (result.ExitCode == 0)
                {
                    Assert.Contains("WORKFLOW COMPLETED", result.Output);
                    Assert.Contains("chargesApplied=1", result.Output);
                    completed = true;
                }
            }
            Assert.True(completed, $"seed {seed}: the workflow did not complete within 12 runs");
            Assert.Contains(Provider.History(state),
                e => e.GetProperty("type").GetString() == "WorkflowCompleted");
        }
    }
}

[Trait("Provider", "File")]
public sealed class ScenarioRandomKillTestsFile() : ScenarioRandomKillTests(new FileScenarioProvider());

[Trait("Provider", "SqlServer")]
public sealed class ScenarioRandomKillTestsSqlServer() : ScenarioRandomKillTests(new SqlScenarioProvider());

[Trait("Provider", "PostgreSql")]
public sealed class ScenarioRandomKillTestsPostgreSql() : ScenarioRandomKillTests(new PostgresScenarioProvider());

[Trait("Provider", "Sqlite")]
public sealed class ScenarioRandomKillTestsSqlite() : ScenarioRandomKillTests(new SqliteScenarioProvider());
