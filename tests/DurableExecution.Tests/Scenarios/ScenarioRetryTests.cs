using DurableExecution.History;

namespace DurableExecution.Tests.Scenarios;

/// <summary>
/// Scenario O (P2 retries): a flaky activity that fails twice and succeeds
/// on the third attempt, and the crash proof — a kill between attempts,
/// after attempt 1's failure is durable and attempt 2 is in flight. The
/// resume must spend exactly the remaining budget: attempt 3 succeeds
/// under the same recorded idempotency key, one charge is applied, and the
/// failure records in history are exactly the attempts that failed.
/// Written once, run per provider (§6.5).
/// </summary>
public abstract class ScenarioRetryTests(ScenarioProvider provider) : IDisposable
{
    protected ScenarioProvider Provider { get; } = provider;

    public void Dispose() => Provider.Dispose();

    private static List<string> AttemptLog(string state)
    {
        string path = Path.Combine(state, "attempts.log");
        return File.Exists(path)
            ? File.ReadAllLines(path).Where(l => l.Length > 0).ToList()
            : new List<string>();
    }

    private void AssertKeyLineage(string state)
    {
        var history = Provider.History(state);
        string scheduledKey = StateAssert.Events(history, HistoryEventTypes.ActivityScheduled)
            .Single().GetProperty("idempotencyKey").GetString()!;
        var log = AttemptLog(state);
        Assert.Equal(3, log.Count);
        Assert.All(log, line => Assert.Equal(scheduledKey, line.Split(':', 2)[1]));
        Assert.Equal(new[] { "1", "2", "3" }, log.Select(l => l.Split(':', 2)[0]).ToArray());
    }

    [Fact]
    public void O_FlakyTwice_ThenSucceeds_FailuresRecorded_OneCharge()
    {
        string state = Provider.FreshState("O");
        var run = Provider.Run(state, variant: "retry");
        Assert.Equal(0, run.ExitCode);

        var counters = StateAssert.Counters(state);
        StateAssert.Need(counters, "retryExecutions", 3);
        StateAssert.Need(counters, "chargesApplied", 1);

        var history = Provider.History(state);
        var failed = history.Where(e => e.GetProperty("type").GetString() == HistoryEventTypes.ActivityAttemptFailed)
            .Select(e => e.GetProperty("attempt").GetInt64()).ToArray();
        Assert.Equal(new long[] { 1, 2 }, failed);
        AssertKeyLineage(state);
    }

    [Fact]
    public void O_KillBetweenAttempts_ResumeSpendsOnlyTheRemainingBudget()
    {
        string state = Provider.FreshState("O-kill");
        Assert.True(Provider.Run(state, crashMode: "retry-kill-on-attempt-2", variant: "retry").WasKilled);
        Assert.Equal(0, Provider.Run(state, variant: "retry").ExitCode);

        var counters = StateAssert.Counters(state);
        StateAssert.Need(counters, "retryExecutions", 3);
        StateAssert.Need(counters, "chargesApplied", 1);

        var history = Provider.History(state);
        // Attempt 2 was killed in flight: started, never recorded failed.
        // Exactly one failure is durable — attempt 1's.
        var failed = history.Where(e => e.GetProperty("type").GetString() == HistoryEventTypes.ActivityAttemptFailed)
            .Select(e => e.GetProperty("attempt").GetInt64()).ToArray();
        Assert.Equal(new long[] { 1 }, failed);
        var started = history.Where(e => e.GetProperty("type").GetString() == HistoryEventTypes.ActivityAttemptStarted)
            .Select(e => e.GetProperty("attempt").GetInt64()).ToArray();
        Assert.Equal(new long[] { 1, 2, 3 }, started);
        AssertKeyLineage(state);
    }
}

/// <summary>Scenario O against the file provider.</summary>
[Trait("Provider", "File")]
public sealed class ScenarioRetryTestsFile() : ScenarioRetryTests(new FileScenarioProvider());

/// <summary>Scenario O against the SQL Server provider.</summary>
[Trait("Provider", "SqlServer")]
public sealed class ScenarioRetryTestsSqlServer() : ScenarioRetryTests(new SqlScenarioProvider());

/// <summary>Scenario O against the PostgreSQL provider.</summary>
[Trait("Provider", "PostgreSql")]
public sealed class ScenarioRetryTestsPostgreSql() : ScenarioRetryTests(new PostgresScenarioProvider());

/// <summary>Scenario O against the SQLite provider.</summary>
[Trait("Provider", "Sqlite")]
public sealed class ScenarioRetryTestsSqlite() : ScenarioRetryTests(new SqliteScenarioProvider());
