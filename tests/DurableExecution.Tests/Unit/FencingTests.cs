using DurableExecution.Execution;
using DurableExecution.History;
using DurableExecution.PostgreSql;
using DurableExecution.Sqlite;
using DurableExecution.SqlServer;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DurableExecution.Tests.Unit;

/// <summary>
/// P3 fencing, proven per provider (§6.5): two stores contend for one
/// instance's lease over the real store medium. A first acquire issues
/// token 1; a second owner is refused while the lease is valid; after
/// the lease expires the successor takes over with a HIGHER token, the
/// stalled owner's next append is refused with
/// <see cref="StaleFencingTokenException"/>, and history stays
/// sealed-valid throughout. Expiry alone — without any takeover —
/// also fences the stale writer out, and re-acquiring issues the next
/// token, never the same one twice.
/// </summary>
public abstract class FencingTests(IFencingFixture fixture) : IDisposable
{
    protected IFencingFixture Fixture { get; } = fixture;

    public void Dispose() => Fixture.Dispose();

    private static readonly TimeSpan LongLease = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShortLease = TimeSpan.FromMilliseconds(1500);

    private static HistoryEvent Started() =>
        HistoryEvent.WorkflowStarted("inst-1", "order/v1", Fingerprints.InputFingerprint(new { x = 1 }));

    private static HistoryEvent Scheduled() =>
        HistoryEvent.ActivityScheduled("inst-1", 1, "StepA",
            Fingerprints.DeriveIdempotencyKey("default", "inst-1", 1, "StepA"),
            Fingerprints.InputFingerprint(new { x = 1 }));

    [Fact]
    public void FirstAcquire_IssuesTokenOne_AndAppendsWork()
    {
        var a = Fixture.CreateStore("host-a", LongLease);
        a.Load();
        Assert.Equal(1, Fixture.TokenOf(a));
        a.Append(Started());
        a.Append(Scheduled());
        Assert.Equal(2, a.Load().Events.Count);
        Assert.Equal(1, Fixture.TokenOf(a)); // re-load renews my lease at the same token
        Fixture.ReleaseStores();
    }

    [Fact]
    public void SecondOwner_IsRefusedWhileTheLeaseIsValid()
    {
        var a = Fixture.CreateStore("host-a", LongLease);
        a.Load();
        var b = Fixture.CreateStore("host-b", LongLease);
        Assert.Throws<StateOwnedByAnotherHostException>(() => b.Load());
        Fixture.ReleaseStores();
    }

    [Fact]
    public void TakeoverAfterExpiry_BumpsToken_StaleWriterIsRefused_HistoryStaysSealedValid()
    {
        var a = Fixture.CreateStore("host-a", ShortLease);
        a.Load();
        Assert.Equal(1, Fixture.TokenOf(a));
        a.Append(Started());

        Thread.Sleep(2100); // host A stalls past its lease

        var b = Fixture.CreateStore("host-b", ShortLease);
        b.Load();
        Assert.Equal(2, Fixture.TokenOf(b)); // the token only ever moves up
        b.Append(Scheduled());

        var stale = Assert.Throws<StaleFencingTokenException>(() => a.Append(Scheduled()));
        Assert.Equal(1, stale.Token);
        Assert.Equal("host-a", stale.OwnerId);

        // The successor's view of history loads and validates whole:
        // A's record plus B's record, seals intact, nothing of A's
        // refused append present.
        var events = b.Load().Events;
        Assert.Equal(2, events.Count);
        Assert.Equal(HistoryEventTypes.WorkflowStarted, events[0].Type);
        Assert.Equal(HistoryEventTypes.ActivityScheduled, events[1].Type);
        Fixture.ReleaseStores();
    }

    [Fact]
    public void ExpiredLease_AloneFencesTheWriterOut_ReacquireIssuesTheNextToken()
    {
        var a = Fixture.CreateStore("host-a", ShortLease);
        a.Load();
        a.Append(Started());

        Thread.Sleep(2100); // the lease lapses; nobody takes over

        Assert.Throws<StaleFencingTokenException>(() => a.Append(Scheduled()));

        a.Load(); // re-acquire after the lapse
        Assert.Equal(2, Fixture.TokenOf(a)); // a fresh token, never a reuse
        a.Append(Scheduled());
        Assert.Equal(2, a.Load().Events.Count);
        Fixture.ReleaseStores();
    }
}

/// <summary>A fencing fixture: stores with pinned owner ids and chosen leases over one scratch state.</summary>
public interface IFencingFixture : IDisposable
{
    IHistoryStore CreateStore(string ownerId, TimeSpan lease);

    /// <summary>The fencing token a store currently holds, read from the concrete store type.</summary>
    long TokenOf(IHistoryStore store);

    void ReleaseStores();
}

internal abstract class FencingFixtureBase : IFencingFixture
{
    private readonly List<IHistoryStore> _stores = new();

    protected abstract IHistoryStore BuildStore(string ownerId, TimeSpan lease);

    public IHistoryStore CreateStore(string ownerId, TimeSpan lease)
    {
        var store = BuildStore(ownerId, lease);
        _stores.Add(store);
        return store;
    }

    public long TokenOf(IHistoryStore store) => store switch
    {
        FileHistoryStore file => file.FencingToken!.Value,
        SqlHistoryStore sql => sql.FencingToken!.Value,
        PostgresHistoryStore pg => pg.FencingToken!.Value,
        SqliteHistoryStore lite => lite.FencingToken!.Value,
        _ => throw new ArgumentException($"unknown store type {store.GetType().Name}"),
    };

    public void ReleaseStores()
    {
        foreach (var store in _stores)
            (store as IDisposable)?.Dispose();
        _stores.Clear();
    }

    public virtual void Dispose() => ReleaseStores();
}

internal sealed class FileFencingFixture : FencingFixtureBase
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "p0-fencing", Guid.NewGuid().ToString("N")[..8]);

    public FileFencingFixture() => Directory.CreateDirectory(_dir);

    protected override IHistoryStore BuildStore(string ownerId, TimeSpan lease) =>
        new FileHistoryStore(_dir, fencing: new FencingOptions(lease, ownerId));
}

internal sealed class SqliteFencingFixture : FencingFixtureBase
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "p0-fencing", Guid.NewGuid().ToString("N")[..8]);

    public SqliteFencingFixture() => Directory.CreateDirectory(_dir);

    protected override IHistoryStore BuildStore(string ownerId, TimeSpan lease) =>
        new SqliteHistoryStore(_dir, fencing: new FencingOptions(lease, ownerId));
}

internal sealed class SqlFencingFixture : FencingFixtureBase
{
    private readonly (string Name, string Connection) _database = SqlTestDatabases.CreateDatabase("fencing");

    protected override IHistoryStore BuildStore(string ownerId, TimeSpan lease) =>
        new SqlHistoryStore(_database.Connection, "inst-1", fencing: new FencingOptions(lease, ownerId));

    public override void Dispose()
    {
        base.Dispose();
        try { SqlTestDatabases.DropDatabase(_database.Name); }
        catch (SqlException) { /* best-effort cleanup of a scratch database */ }
    }
}

internal sealed class PostgresFencingFixture : FencingFixtureBase
{
    private readonly (string Name, string Connection) _database = PostgresTestDatabases.CreateDatabase("fencing");

    protected override IHistoryStore BuildStore(string ownerId, TimeSpan lease) =>
        new PostgresHistoryStore(_database.Connection, "inst-1", fencing: new FencingOptions(lease, ownerId));

    public override void Dispose()
    {
        base.Dispose();
        try { PostgresTestDatabases.DropDatabase(_database.Name); }
        catch (NpgsqlException) { /* best-effort cleanup of a scratch database */ }
    }
}

[Trait("Provider", "File")]
public sealed class FileFencingTests() : FencingTests(new FileFencingFixture());

[Trait("Provider", "Sqlite")]
public sealed class SqliteFencingTests() : FencingTests(new SqliteFencingFixture());

[Trait("Provider", "SqlServer")]
public sealed class SqlFencingTests() : FencingTests(new SqlFencingFixture());

[Trait("Provider", "PostgreSql")]
public sealed class PostgresFencingTests() : FencingTests(new PostgresFencingFixture());
