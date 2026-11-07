using System.Text.Json;

namespace DurableExecution.Tests.Scenarios;

/// <summary>
/// Snapshot equivalence at scenario level (P1): forcing a snapshot every
/// other event through the crash scenarios must change nothing observable —
/// the same counters, the same recorded idempotency keys, the same history
/// shape (types, command sequences, activities, keys, results) as the
/// no-snapshot control run, with the resume provably served by a snapshot
/// (the host reports the snapshot point it resumed from). Written once, run
/// per provider (§6.5).
/// </summary>
public abstract class SnapshotScenarioTests(ScenarioProvider provider) : IDisposable
{
    protected ScenarioProvider Provider { get; } = provider;

    public void Dispose() => Provider.Dispose();

    private static readonly IReadOnlyDictionary<string, string> SnapshotEnv =
        new Dictionary<string, string> { ["SNAPSHOT_INTERVAL"] = "2" };

    private static List<string> Shape(IReadOnlyList<JsonElement> history) =>
        history.Select(e =>
        {
            string Field(string name) =>
                e.TryGetProperty(name, out var v) ? v.ToString() : "";
            string shape = $"{Field("type")}|seq={Field("seq")}|activity={Field("activity")}|" +
                   $"key={Field("idempotencyKey")}|result={Field("result")}";
            // The gateway's transaction ids are random per state directory;
            // they are not part of the equivalence claim — the recorded
            // keys, activities, positions and result roles are.
            return System.Text.RegularExpressions.Regex.Replace(shape, "txn-[0-9a-f]+", "txn-*");
        }).ToList();

    private void AssertEquivalent(string control, string snapped, bool unordered = false, params string[] counters)
    {
        var controlCounters = StateAssert.Counters(control);
        var snappedCounters = StateAssert.Counters(snapped);
        foreach (string counter in counters)
            Assert.Equal(controlCounters[counter], snappedCounters[counter]);
        var controlShape = Shape(Provider.History(control));
        var snappedShape = Shape(Provider.History(snapped));
        if (unordered)
        {
            // Fan-out branches race: the order attempts and completions
            // land in history is a property of the clock, not of the run's
            // semantics. Equivalence there is a multiset claim — the same
            // records, the same keys, the same results.
            controlShape.Sort();
            snappedShape.Sort();
        }
        Assert.Equal(controlShape, snappedShape);
    }

    [Fact]
    public void B_WithSnapshots_SameOutcomeKeyAndHistoryShape()
    {
        string control = Provider.FreshState("snapB-control");
        Assert.True(Provider.Run(control, crashMode: "in-charge-window").WasKilled);
        Assert.Equal(0, Provider.Run(control).ExitCode);

        string snapped = Provider.FreshState("snapB");
        Assert.True(Provider.Run(snapped, crashMode: "in-charge-window", extraEnv: SnapshotEnv).WasKilled);
        var resume = Provider.Run(snapped, extraEnv: SnapshotEnv);
        Assert.Equal(0, resume.ExitCode);
        Assert.Contains("snapshot used through", resume.Output);

        StateAssert.ChargeKeyChecks(snapped, "charged", "duplicate-key");
        AssertEquivalent(control, snapped, unordered: false, "chargeExecutions", "chargesApplied", "reserveExecutions");
    }

    [Fact]
    public void L_WithSnapshots_SameBranchOutcomesKeysAndHistoryShape()
    {
        string control = Provider.FreshState("snapL-control");
        Assert.True(Provider.Run(control, crashMode: "fanout-kill-mid", variant: "fanout").WasKilled);
        Assert.Equal(0, Provider.Run(control, variant: "fanout").ExitCode);

        string snapped = Provider.FreshState("snapL");
        Assert.True(Provider.Run(snapped, crashMode: "fanout-kill-mid", variant: "fanout",
            extraEnv: SnapshotEnv).WasKilled);
        var resume = Provider.Run(snapped, variant: "fanout", extraEnv: SnapshotEnv);
        Assert.Equal(0, resume.ExitCode);
        Assert.Contains("snapshot used through", resume.Output);

        // The deterministic invariants of the scenario, per run. Branch 2
        // executed exactly twice: the kill is inside it, after its charge,
        // so its completion is never recorded in the killed run and the
        // resume re-executes it under its recorded key (the gateway
        // deduplicates). Branches 1 and 3 executed once or twice each —
        // whether the kill caught them in flight or already recorded
        // depends on scheduling under load, and both are the scenario
        // working as specified. What may never vary: exactly one applied
        // charge per branch.
        foreach (string state in new[] { control, snapped })
        {
            var counters = StateAssert.Counters(state);
            Assert.Contains(counters["fanExecutions1"], new[] { 1, 2 });
            StateAssert.Need(counters, "fanExecutions2", 2);
            Assert.Contains(counters["fanExecutions3"], new[] { 1, 2 });
            StateAssert.Need(counters, "chargesApplied", 3);
        }

        // History equivalence as a multiset over the records whose content
        // is deterministic: schedules (positions, activities, keys) and
        // completions (positions, results). Attempt records are excluded —
        // how many attempts branch 3 needed depends on whether the kill
        // landed before or after its staged delay, which is the clock's
        // business, not the snapshot's.
        static List<string> DeterministicShape(IReadOnlyList<JsonElement> history) =>
            Shape(history)
                .Where(line => !line.StartsWith("ActivityAttemptStarted|"))
                .OrderBy(line => line)
                .ToList();
        Assert.Equal(
            DeterministicShape(Provider.History(control)),
            DeterministicShape(Provider.History(snapped)));
    }
}

/// <summary>Snapshot scenario equivalence against the file provider.</summary>
[Trait("Provider", "File")]
public sealed class SnapshotScenarioTestsFile() : SnapshotScenarioTests(new FileScenarioProvider());

/// <summary>Snapshot scenario equivalence against the SQL Server provider.</summary>
[Trait("Provider", "SqlServer")]
public sealed class SnapshotScenarioTestsSqlServer() : SnapshotScenarioTests(new SqlScenarioProvider());

/// <summary>Snapshot scenario equivalence against the PostgreSQL provider.</summary>
[Trait("Provider", "PostgreSql")]
public sealed class SnapshotScenarioTestsPostgreSql() : SnapshotScenarioTests(new PostgresScenarioProvider());

/// <summary>Snapshot scenario equivalence against the SQLite provider.</summary>
[Trait("Provider", "Sqlite")]
public sealed class SnapshotScenarioTestsSqlite() : SnapshotScenarioTests(new SqliteScenarioProvider());
