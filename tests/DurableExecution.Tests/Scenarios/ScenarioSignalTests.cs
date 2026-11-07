using System.Text.Json;
using DurableExecution.History;

namespace DurableExecution.Tests.Scenarios;

/// <summary>
/// Scenario Q (P2 signals): a workflow parks waiting for an 'approve'
/// signal; the host is killed while it waits; a separate sender process
/// records the signal in history while no host runs; the resume delivers
/// it from history and charges exactly the approved amount, exactly once.
/// The history itself proves the order: SignalReceived precedes the
/// ActivityScheduled it unblocked. Written once, run per provider (§6.5).
/// </summary>
public abstract class ScenarioSignalTests(ScenarioProvider provider) : IDisposable
{
    protected ScenarioProvider Provider { get; } = provider;

    public void Dispose() => Provider.Dispose();

    [Fact]
    public async Task Q_KilledWhileWaiting_SignalRecordedWhileDead_DeliveredOnResume()
    {
        string state = Provider.FreshState("Q");

        using var process = Provider.Start(state, variant: "signal");
        var output = process.StandardOutput.ReadToEndAsync();
        // Wait until the workflow's start is durable (it is parked in the
        // wait immediately after); history reads can race a writer, so
        // poll with retries.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            try
            {
                if (Provider.History(state).Any(e => e.GetProperty("type").GetString() == HistoryEventTypes.WorkflowStarted))
                    break;
            }
            catch (Exception) { /* history mid-append; poll again */ }
            Assert.True(DateTime.UtcNow < deadline, "workflow did not start waiting in time");
            await Task.Delay(100);
        }
        // The body parks in the wait right after starting; give it a beat
        // so the kill lands in the wait, not in startup.
        await Task.Delay(300);
        process.Kill();
        await process.WaitForExitAsync();
        Assert.NotEqual(0, process.ExitCode);
        _ = await output; // drain

        var send = Provider.Run(state, variant: "signal",
            extraEnv: new Dictionary<string, string> { ["AMOUNT"] = "777" },
            args: "send-signal");
        Assert.Equal(0, send.ExitCode);

        var resume = Provider.Run(state, variant: "signal");
        Assert.Equal(0, resume.ExitCode);
        Assert.Contains("approvedAmount=777", resume.Output);

        var counters = StateAssert.Counters(state);
        StateAssert.Need(counters, "chargeExecutions", 1);
        StateAssert.Need(counters, "chargesApplied", 1);
        var charges = StateAssert.GatewayState(state).GetProperty("Charges");
        Assert.Equal(777, charges.EnumerateArray().Single().GetProperty("Amount").GetInt32());

        var history = Provider.History(state);
        int signalAt = history.FindIndex(e => e.GetProperty("type").GetString() == HistoryEventTypes.SignalReceived);
        int scheduledAt = history.FindIndex(e => e.GetProperty("type").GetString() == HistoryEventTypes.ActivityScheduled);
        Assert.True(signalAt >= 0 && scheduledAt > signalAt,
            "the signal must be recorded before the activity it unblocked was scheduled");
        StateAssert.WorkflowCompletedExactlyOnce(history);
    }
}

/// <summary>Scenario Q against the file provider.</summary>
[Trait("Provider", "File")]
public sealed class ScenarioSignalTestsFile() : ScenarioSignalTests(new FileScenarioProvider());

/// <summary>Scenario Q against the SQL Server provider.</summary>
[Trait("Provider", "SqlServer")]
public sealed class ScenarioSignalTestsSqlServer() : ScenarioSignalTests(new SqlScenarioProvider());

/// <summary>Scenario Q against the PostgreSQL provider.</summary>
[Trait("Provider", "PostgreSql")]
public sealed class ScenarioSignalTestsPostgreSql() : ScenarioSignalTests(new PostgresScenarioProvider());

/// <summary>Scenario Q against the SQLite provider.</summary>
[Trait("Provider", "Sqlite")]
public sealed class ScenarioSignalTestsSqlite() : ScenarioSignalTests(new SqliteScenarioProvider());
