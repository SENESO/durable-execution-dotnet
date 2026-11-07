using DurableExecution.History;

namespace DurableExecution.Tests.Unit;

/// <summary>
/// A conformance fixture that can also damage its provider's snapshot —
/// the snapshot tests' equivalent of writing raw history records.
/// </summary>
public interface ISnapshotStoreFixture : IHistoryStoreFixture
{
    void CorruptSnapshot();
}

/// <summary>
/// The snapshot conformance tests (P1): a snapshot plus its tail must
/// reconstruct exactly the logical state a full replay reconstructs — same
/// events in the same roles, same recorded idempotency keys, same results —
/// and a damaged snapshot is corruption, refused loudly like a damaged
/// history record (the sealing model admits no silent fallback). Written
/// once, run per provider; the fixtures force a snapshot every 3 events, so
/// the sample history below snapshots at its attempt and tails at its
/// completion.
/// </summary>
public abstract class SnapshotStoreTests(ISnapshotStoreFixture fixture) : IDisposable
{
    protected ISnapshotStoreFixture Fixture { get; } = fixture;

    public void Dispose() => Fixture.Dispose();

    private static void AppendSample(IHistoryStore store)
    {
        store.Append(HistoryEvent.WorkflowStarted("inst-1", "order/v1",
            DurableExecution.Execution.Fingerprints.InputFingerprint(null)));
        store.Append(HistoryEvent.ActivityScheduled("inst-1", 1, "ChargeCard", new string('b', 64), new string('c', 64)));
        store.Append(HistoryEvent.ActivityAttemptStarted("inst-1", 1, 1));
        store.Append(HistoryEvent.ActivityCompleted("inst-1", 1, "ChargeCard", "txn-1"));
    }

    private static List<string> Shape(IReadOnlyList<HistoryEvent> events) =>
        events.Select(e =>
            $"{e.Type}|seq={e.CommandSequence}|activity={e.GetString("activity")}|" +
            $"key={e.GetString("idempotencyKey")}|result={e.GetResult<string>()}").ToList();

    [Fact]
    public void SnapshotPlusTail_ReconstructsTheSameStateAsFullReplay()
    {
        AppendSample(Fixture.CreateLoadedStore());

        var full = Fixture.CreateStore().Load();
        var snapshotStore = (IHistorySnapshotStore)Fixture.CreateStore();
        var fromSnapshot = snapshotStore.LoadFromSnapshot();

        Assert.True(fromSnapshot.SnapshotUsed);
        Assert.Equal(3, fromSnapshot.SnapshotThroughN);
        Assert.Equal(Shape(full.Events), Shape(fromSnapshot.Load.Events));
        Assert.Equal(
            full.Events.Single(e => e.Type == HistoryEventTypes.ActivityScheduled).GetString("idempotencyKey"),
            fromSnapshot.Load.Events.Single(e => e.Type == HistoryEventTypes.ActivityScheduled).GetString("idempotencyKey"));
    }

    [Fact]
    public void CorruptSnapshot_IsRefusedLoudly_NotSilentlyBypassed()
    {
        AppendSample(Fixture.CreateLoadedStore());
        Fixture.CorruptSnapshot();

        var snapshotStore = (IHistorySnapshotStore)Fixture.CreateStore();
        var ex = Assert.Throws<HistoryCorruptionException>(() => snapshotStore.LoadFromSnapshot());
        Assert.Contains("checksum mismatch", ex.Message);
    }

    [Fact]
    public void SnapshotAheadOfHistory_IsRefused()
    {
        AppendSample(Fixture.CreateLoadedStore());
        Fixture.WriteRawRecords(); // the history vanishes under its snapshot

        var snapshotStore = (IHistorySnapshotStore)Fixture.CreateStore();
        var ex = Assert.Throws<HistoryCorruptionException>(() => snapshotStore.LoadFromSnapshot());
        Assert.Contains("ahead of history", ex.Message);
    }
}

/// <summary>Snapshot tests against the file provider.</summary>
[Trait("Provider", "File")]
public sealed class SnapshotStoreTestsFile() : SnapshotStoreTests(new FileHistoryStoreFixture());

/// <summary>Snapshot tests against the SQL Server provider.</summary>
[Trait("Provider", "SqlServer")]
public sealed class SnapshotStoreTestsSqlServer() : SnapshotStoreTests(new SqlHistoryStoreFixture());

/// <summary>Snapshot tests against the PostgreSQL provider.</summary>
[Trait("Provider", "PostgreSql")]
public sealed class SnapshotStoreTestsPostgreSql() : SnapshotStoreTests(new PostgresHistoryStoreFixture());

/// <summary>Snapshot tests against the SQLite provider.</summary>
[Trait("Provider", "Sqlite")]
public sealed class SnapshotStoreTestsSqlite() : SnapshotStoreTests(new SqliteHistoryStoreFixture());
