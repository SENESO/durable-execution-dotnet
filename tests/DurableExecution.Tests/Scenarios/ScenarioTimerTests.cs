using DurableExecution.History;

namespace DurableExecution.Tests.Scenarios;

/// <summary>
/// Scenario P (P2 durable timers, real clock): a workflow waits 3 seconds
/// on a durable timer, then charges. In the kill run the host dies ~400ms
/// into the wait; the resume must fire at the ORIGINAL due time — proven
/// by the wall-clock marks the workflow appends: the fire lands ~3s after
/// the first run started, and well under 3s after the resume started
/// (a fresh 3s wait on resume would land at ~3s after the resume).
/// Written once, run per provider (§6.5).
/// </summary>
public abstract class ScenarioTimerTests(ScenarioProvider provider) : IDisposable
{
    protected ScenarioProvider Provider { get; } = provider;

    public void Dispose() => Provider.Dispose();

    private static (long FirstStart, long LastStart, long Fired) TimerMarks(string state)
    {
        var lines = File.ReadAllLines(Path.Combine(state, "timer.log"))
            .Select(l => l.Split(' ', 2)).ToList();
        long Stamp(string mark, bool last) =>
            (last ? lines.Last(x => x[0] == mark) : lines.First(x => x[0] == mark)).Length > 1
                ? long.Parse((last ? lines.Last(x => x[0] == mark) : lines.First(x => x[0] == mark))[1])
                : throw new InvalidOperationException($"timer mark '{mark}' missing");
        return (Stamp("start", last: false), Stamp("start", last: true), Stamp("fired", last: true));
    }

    [Fact]
    public void P_TimerFires_ThenWorkflowCharges()
    {
        string state = Provider.FreshState("P");
        Assert.Equal(0, Provider.Run(state, variant: "timer").ExitCode);

        var counters = StateAssert.Counters(state);
        StateAssert.Need(counters, "chargeExecutions", 1);
        StateAssert.Need(counters, "chargesApplied", 1);

        var history = Provider.History(state);
        Assert.Single(StateAssert.Events(history, HistoryEventTypes.TimerScheduled));
        Assert.Single(StateAssert.Events(history, HistoryEventTypes.TimerFired));

        var (firstStart, _, fired) = TimerMarks(state);
        long waitedMs = fired - firstStart;
        Assert.InRange(waitedMs, 2800, 10000);
    }

    [Fact]
    public void P_KillWhileWaiting_ResumeFiresAtTheRecordedDueTime()
    {
        string state = Provider.FreshState("P-kill");
        Assert.True(Provider.Run(state, crashMode: "timer-kill-while-waiting", variant: "timer").WasKilled);
        Assert.Equal(0, Provider.Run(state, variant: "timer").ExitCode);

        var counters = StateAssert.Counters(state);
        StateAssert.Need(counters, "chargeExecutions", 1);
        StateAssert.Need(counters, "chargesApplied", 1);

        var history = Provider.History(state);
        Assert.Single(StateAssert.Events(history, HistoryEventTypes.TimerScheduled)); // scheduled once, in the killed run
        Assert.Single(StateAssert.Events(history, HistoryEventTypes.TimerFired));

        var (firstStart, resumeStart, fired) = TimerMarks(state);
        // Fired at the original due time (~3s after the first start)…
        Assert.InRange(fired - firstStart, 2800, 10000);
        // …not after a fresh full wait on resume (that would be ~3s after
        // the resume's start; the resume began ~0.4s in, so the remainder
        // is ~2.6s — assert it is clearly less than the full duration).
        Assert.True(fired - resumeStart < 2900,
            $"resume waited {fired - resumeStart}ms — the timer restarted instead of honouring its recorded due time");
    }
}

/// <summary>Scenario P against the file provider.</summary>
[Trait("Provider", "File")]
public sealed class ScenarioTimerTestsFile() : ScenarioTimerTests(new FileScenarioProvider());

/// <summary>Scenario P against the SQL Server provider.</summary>
[Trait("Provider", "SqlServer")]
public sealed class ScenarioTimerTestsSqlServer() : ScenarioTimerTests(new SqlScenarioProvider());

/// <summary>Scenario P against the PostgreSQL provider.</summary>
[Trait("Provider", "PostgreSql")]
public sealed class ScenarioTimerTestsPostgreSql() : ScenarioTimerTests(new PostgresScenarioProvider());

/// <summary>Scenario P against the SQLite provider.</summary>
[Trait("Provider", "Sqlite")]
public sealed class ScenarioTimerTestsSqlite() : ScenarioTimerTests(new SqliteScenarioProvider());
