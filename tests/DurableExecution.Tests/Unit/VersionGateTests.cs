using DurableExecution.Execution;
using DurableExecution.History;

namespace DurableExecution.Tests.Unit;

/// <summary>
/// The P4 version gates, proven per provider (§6.5): a gate records its
/// version marker on the first live pass and returns the recorded version
/// on every replay; a recorded version outside the host's supported range
/// refuses the run loudly (VersionGateException) before anything executes;
/// a gate inserted before already-recorded work resolves to the default
/// version and records nothing; a different change id at a recorded
/// marker's position — and any ungated change at a recorded position —
/// is a determinism violation.
/// </summary>
public abstract class VersionGateTests(IStoreFactory factory) : IDisposable
{
    protected IStoreFactory Factory { get; } = factory;

    public void Dispose() => Factory.Dispose();

    private sealed class SimulatedCrashException : Exception { }

    private static string FreshDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "p0-version", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private WorkflowHost OpenHost(string dir) => new(Factory.CreateStore(dir), dir);

    private static List<HistoryEvent> LoadHistory(IHistoryStore store) => store.Load().Events.ToList();

    [Fact]
    public async Task FreshRun_RecordsMarkerAtMaxSupported_AndReturnsIt()
    {
        string dir = FreshDir();
        int version = int.MinValue;
        int executions = 0;
        Func<WorkflowContext, Task<object?>> body = async ctx =>
        {
            version = ctx.GetVersion("change-1", 1, 3);
            await ctx.CallActivityAsync("StepA", new { x = 1 }, actx =>
            {
                executions++;
                return Task.FromResult("done");
            });
            return null;
        };

        // (hosts own and dispose their store; history is read back through a fresh one)
        using (var host = OpenHost(dir))
            Assert.Equal(WorkflowRunOutcome.Completed,
                await host.RunAsync("order/v1", "order-1001", new { x = 1 }, body));

        Assert.Equal(3, version);
        Assert.Equal(1, executions);

        var probe = Factory.CreateStore(dir);
        var markers = LoadHistory(probe).Where(e => e.Type == HistoryEventTypes.VersionMarker).ToList();
        Assert.Single(markers);
        Assert.Equal("change-1", markers[0].GetString("changeId"));
        Assert.Equal(3, markers[0].GetInt64("version"));
    }

    [Fact]
    public async Task Resume_ReturnsTheRecordedVersion_NotTheNewMaximum()
    {
        string dir = FreshDir();
        int firstVersion = int.MinValue, resumedVersion = int.MinValue;

        Func<WorkflowContext, Task<object?>> first = async ctx =>
        {
            firstVersion = ctx.GetVersion("change-1", 1, 1);
            await ctx.CallActivityAsync("StepA", new { x = 1 }, actx => Task.FromResult("done"));
            throw new SimulatedCrashException();
        };
        using (var host = OpenHost(dir))
            await Assert.ThrowsAsync<SimulatedCrashException>(
                () => host.RunAsync("order/v1", "order-1001", new { x = 1 }, first));
        Assert.Equal(1, firstVersion);

        Func<WorkflowContext, Task<object?>> resumed = async ctx =>
        {
            resumedVersion = ctx.GetVersion("change-1", 1, 3);
            await ctx.CallActivityAsync("StepA", new { x = 1 }, actx => Task.FromResult("done"));
            return null;
        };
        using (var host = OpenHost(dir))
            Assert.Equal(WorkflowRunOutcome.Completed,
                await host.RunAsync("order/v1", "order-1001", new { x = 1 }, resumed));
        Assert.Equal(1, resumedVersion); // the recorded branch wins over the new maximum
    }

    [Fact]
    public async Task RecordedAboveSupportedMax_RefusesLoudly_AndExecutesNothing()
    {
        string dir = FreshDir();
        int executions = 0;

        Func<WorkflowContext, Task<object?>> first = async ctx =>
        {
            ctx.GetVersion("change-1", 1, 2); // records version 2
            await ctx.CallActivityAsync("StepA", new { x = 1 }, actx =>
            {
                executions++;
                return Task.FromResult("done");
            });
            throw new SimulatedCrashException();
        };
        using (var host = OpenHost(dir))
            await Assert.ThrowsAsync<SimulatedCrashException>(
                () => host.RunAsync("order/v1", "order-1001", new { x = 1 }, first));
        Assert.Equal(1, executions);

        Func<WorkflowContext, Task<object?>> capped = async ctx =>
        {
            await ctx.CallActivityAsync("StepA", new { x = 1 }, actx =>
            {
                executions++;
                return Task.FromResult("done");
            });
            ctx.GetVersion("change-1", 1, 1); // this host no longer supports the recorded 2
            return null;
        };
        using (var host = OpenHost(dir))
        {
            var ex = await Assert.ThrowsAsync<VersionGateException>(
                () => host.RunAsync("order/v1", "order-1001", new { x = 1 }, capped));
            Assert.Equal("change-1", ex.ChangeId);
            Assert.Equal(2, ex.RecordedVersion);
            Assert.Equal(1, ex.MinSupported);
            Assert.Equal(1, ex.MaxSupported);
        }
        Assert.Equal(1, executions); // the recorded completion replayed; nothing executed
    }

    [Fact]
    public async Task RecordedBelowSupportedMin_RefusesLoudly()
    {
        string dir = FreshDir();

        Func<WorkflowContext, Task<object?>> first = async ctx =>
        {
            ctx.GetVersion("change-1", 1, 1); // records version 1
            await ctx.CallActivityAsync("StepA", new { x = 1 }, actx => Task.FromResult("done"));
            throw new SimulatedCrashException();
        };
        using (var host = OpenHost(dir))
            await Assert.ThrowsAsync<SimulatedCrashException>(
                () => host.RunAsync("order/v1", "order-1001", new { x = 1 }, first));

        Func<WorkflowContext, Task<object?>> raised = async ctx =>
        {
            await ctx.CallActivityAsync("StepA", new { x = 1 }, actx => Task.FromResult("done"));
            ctx.GetVersion("change-1", 2, 3); // this host dropped support for the recorded 1
            return null;
        };
        using (var host = OpenHost(dir))
        {
            var ex = await Assert.ThrowsAsync<VersionGateException>(
                () => host.RunAsync("order/v1", "order-1001", new { x = 1 }, raised));
            Assert.Equal(1, ex.RecordedVersion);
            Assert.Equal(2, ex.MinSupported);
        }
    }

    [Fact]
    public async Task GateInsertedBeforeRecordedWork_ResolvesDefault_AndRecordsNoMarker()
    {
        string dir = FreshDir();
        int executions = 0;

        // The pre-gate code: one activity completes, then the host dies.
        using (var host = OpenHost(dir))
            await Assert.ThrowsAsync<SimulatedCrashException>(() => host.RunAsync(
                "order/v1", "order-1001", new { x = 1 }, async ctx =>
                {
                    await ctx.CallActivityAsync("StepA", new { x = 1 }, actx =>
                    {
                        executions++;
                        return Task.FromResult("done");
                    });
                    throw new SimulatedCrashException();
                }));

        int version = int.MinValue;
        Func<WorkflowContext, Task<object?>> gated = async ctx =>
        {
            version = ctx.GetVersion("change-1", 1, 2); // inserted before the recorded work
            await ctx.CallActivityAsync("StepA", new { x = 1 }, actx =>
            {
                executions++;
                return Task.FromResult("done");
            });
            await ctx.CallActivityAsync("StepB", new { x = 2 }, actx => Task.FromResult("done"));
            return null;
        };
        using (var host = OpenHost(dir))
            Assert.Equal(WorkflowRunOutcome.Completed,
                await host.RunAsync("order/v1", "order-1001", new { x = 1 }, gated));

        Assert.Equal(WorkflowContext.DefaultVersion, version);
        Assert.Equal(1, executions); // StepA replayed from its record, never re-executed

        var probe = Factory.CreateStore(dir);
        Assert.DoesNotContain(LoadHistory(probe), e => e.Type == HistoryEventTypes.VersionMarker);
    }

    [Fact]
    public async Task GateInsertedAfterRecordedWork_RecordsItsMarkerLive()
    {
        string dir = FreshDir();

        using (var host = OpenHost(dir))
            await Assert.ThrowsAsync<SimulatedCrashException>(() => host.RunAsync(
                "order/v1", "order-1001", new { x = 1 }, async ctx =>
                {
                    await ctx.CallActivityAsync("StepA", new { x = 1 }, actx => Task.FromResult("done"));
                    throw new SimulatedCrashException();
                }));

        int version = int.MinValue;
        Func<WorkflowContext, Task<object?>> gated = async ctx =>
        {
            await ctx.CallActivityAsync("StepA", new { x = 1 }, actx => Task.FromResult("done"));
            version = ctx.GetVersion("change-1", 1, 2); // after the recorded prefix: live ground
            await ctx.CallActivityAsync("StepB", new { x = 2 }, actx => Task.FromResult("done"));
            return null;
        };
        using (var host = OpenHost(dir))
            Assert.Equal(WorkflowRunOutcome.Completed,
                await host.RunAsync("order/v1", "order-1001", new { x = 1 }, gated));

        Assert.Equal(2, version);
        var probe = Factory.CreateStore(dir);
        var marker = Assert.Single(LoadHistory(probe), e => e.Type == HistoryEventTypes.VersionMarker);
        Assert.Equal("change-1", marker.GetString("changeId"));
        Assert.Equal(2, marker.GetInt64("version"));
    }

    [Fact]
    public async Task DifferentChangeIdAtARecordedMarker_IsADeterminismViolation()
    {
        string dir = FreshDir();

        Func<WorkflowContext, Task<object?>> first = async ctx =>
        {
            ctx.GetVersion("change-a", 1, 1);
            await ctx.CallActivityAsync("StepA", new { x = 1 }, actx => Task.FromResult("done"));
            throw new SimulatedCrashException();
        };
        using (var host = OpenHost(dir))
            await Assert.ThrowsAsync<SimulatedCrashException>(
                () => host.RunAsync("order/v1", "order-1001", new { x = 1 }, first));

        Func<WorkflowContext, Task<object?>> renamed = async ctx =>
        {
            await ctx.CallActivityAsync("StepA", new { x = 1 }, actx => Task.FromResult("done"));
            ctx.GetVersion("change-b", 1, 1); // the recorded marker names change-a at this position
            return null;
        };
        using (var host = OpenHost(dir))
            await Assert.ThrowsAsync<NonDeterminismViolationException>(
                () => host.RunAsync("order/v1", "order-1001", new { x = 1 }, renamed));
    }

    [Fact]
    public async Task UngatedChangeAtARecordedPosition_IsADeterminismViolation()
    {
        string dir = FreshDir();

        using (var host = OpenHost(dir))
            await Assert.ThrowsAsync<SimulatedCrashException>(() => host.RunAsync(
                "order/v1", "order-1001", new { x = 1 }, async ctx =>
                {
                    await ctx.CallActivityAsync("StepA", new { x = 1 }, actx => Task.FromResult("done"));
                    throw new SimulatedCrashException();
                }));

        // No gate, and the recorded first command is silently swapped.
        Func<WorkflowContext, Task<object?>> mutated = async ctx =>
        {
            await ctx.CallActivityAsync("StepB", new { x = 2 }, actx => Task.FromResult("done"));
            return null;
        };
        using (var host = OpenHost(dir))
            await Assert.ThrowsAsync<NonDeterminismViolationException>(
                () => host.RunAsync("order/v1", "order-1001", new { x = 1 }, mutated));
    }

    [Fact]
    public async Task RepeatedCallsForOneChange_ReturnOneVersion_AndRecordOneMarker()
    {
        string dir = FreshDir();
        int first = int.MinValue, second = int.MinValue;
        Func<WorkflowContext, Task<object?>> body = async ctx =>
        {
            first = ctx.GetVersion("change-1", 1, 2);
            second = ctx.GetVersion("change-1", 1, 2);
            await ctx.CallActivityAsync("StepA", new { x = 1 }, actx => Task.FromResult("done"));
            return null;
        };
        using (var host = OpenHost(dir))
            Assert.Equal(WorkflowRunOutcome.Completed,
                await host.RunAsync("order/v1", "order-1001", new { x = 1 }, body));

        Assert.Equal(first, second);
        var probe = Factory.CreateStore(dir);
        Assert.Single(LoadHistory(probe), e => e.Type == HistoryEventTypes.VersionMarker);
    }
}

/// <summary>
/// The marker's passage through snapshots, per provider: a history whose
/// snapshot folds a version marker reconstructs it on snapshot load, so a
/// resume from a snapshot still resolves the recorded branch.
/// </summary>
public abstract class VersionMarkerSnapshotTests(IEncryptionFixture fixture) : IDisposable
{
    protected IEncryptionFixture Fixture { get; } = fixture;

    public void Dispose() => Fixture.Dispose();

    [Fact]
    public void VersionMarker_SurvivesSnapshotLoad()
    {
        var store = Fixture.CreateStore(null, snapshotInterval: 2);
        store.Load();
        store.Append(HistoryEvent.WorkflowStarted("inst-1", "order/v1",
            Fingerprints.InputFingerprint(new { x = 1 })));
        store.Append(HistoryEvent.ActivityScheduled("inst-1", 1, "StepA",
            Fingerprints.DeriveIdempotencyKey("default", "inst-1", 1, "StepA"),
            Fingerprints.InputFingerprint(new { x = 1 })));
        store.Append(HistoryEvent.VersionMarker("inst-1", "change-1", 2));
        store.Append(HistoryEvent.ActivityCompleted("inst-1", 1, "StepA", "done"));
        Fixture.ReleaseStores();

        var relaunched = Fixture.CreateStore(null, snapshotInterval: 2);
        var load = ((IHistorySnapshotStore)relaunched).LoadFromSnapshot();
        Assert.True(load.SnapshotUsed);
        var marker = Assert.Single(load.Load.Events, e => e.Type == HistoryEventTypes.VersionMarker);
        Assert.Equal("change-1", marker.GetString("changeId"));
        Assert.Equal(2, marker.GetInt64("version"));
        Fixture.ReleaseStores();
    }
}

[Trait("Provider", "File")]
public sealed class FileVersionGateTests() : VersionGateTests(new FileStoreFactory());

[Trait("Provider", "SqlServer")]
public sealed class SqlVersionGateTests() : VersionGateTests(new SqlStoreFactory());

[Trait("Provider", "PostgreSql")]
public sealed class PostgresVersionGateTests() : VersionGateTests(new PostgresStoreFactory());

[Trait("Provider", "Sqlite")]
public sealed class SqliteVersionGateTests() : VersionGateTests(new SqliteStoreFactory());

[Trait("Provider", "File")]
public sealed class FileVersionMarkerSnapshotTests() : VersionMarkerSnapshotTests(new FileEncryptionFixture());

[Trait("Provider", "SqlServer")]
public sealed class SqlVersionMarkerSnapshotTests() : VersionMarkerSnapshotTests(new SqlEncryptionFixture());

[Trait("Provider", "PostgreSql")]
public sealed class PostgresVersionMarkerSnapshotTests() : VersionMarkerSnapshotTests(new PostgresEncryptionFixture());

[Trait("Provider", "Sqlite")]
public sealed class SqliteVersionMarkerSnapshotTests() : VersionMarkerSnapshotTests(new SqliteEncryptionFixture());
