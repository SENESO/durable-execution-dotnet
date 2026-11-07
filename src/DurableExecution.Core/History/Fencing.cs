namespace DurableExecution.History;

/// <summary>
/// Opt-in fencing for a history store (P3 core): per-instance leases with
/// a monotonic fencing token, for deployments where a host cannot hold
/// the provider's session lock — or must not be trusted to keep it.
///
/// How it works, identically on every provider: enabling fencing
/// replaces the provider's session/ownership lock as the concurrency
/// authority. <c>Load</c> acquires the instance's lease — the lease
/// record lives beside the history (a row in the provider's leases
/// table; a sealed <c>history.lease.json</c> file on the file
/// provider) — and is issued the current fencing token. Taking over a
/// free or expired lease increments the token; the token never
/// decreases and is never reused. Every append presents the token
/// inside its own commit unit (the append's transaction on the
/// database providers) and is refused with
/// <see cref="StaleFencingTokenException"/> when the lease no longer
/// names this owner with this token, or has expired. A stalled host
/// that wakes after its lease was taken over therefore cannot write:
/// its next append is refused, and history stays sealed-valid.
///
/// What this does NOT solve, precisely: it is cooperative fencing
/// inside one store. (1) It orders writers that go through a fencing
/// store over the same history; a writer that bypasses the store and
/// edits the medium directly is caught only by the history's own seal
/// and end-of-history checks on next load, not by the token. (2) It is
/// not consensus: there is one lease record in one database — no
/// cross-database or cross-machine agreement, no protection if the
/// store itself is replicated or split. (3) Lease expiry runs on the
/// store's clock (the database server's clock on the database
/// providers; the machine clock on the file/SQLite providers, which
/// are single-machine by construction), so a lease is only as
/// trustworthy as that clock. (4) On the file provider the lease check
/// and the log append are two writes, not one transaction — a takeover
/// racing the exact append instant is caught by the end-of-history
/// check on the next load rather than inside the append; the database
/// providers couple check and append in one transaction.
/// </summary>
public sealed record FencingOptions
{
    public FencingOptions(TimeSpan leaseDuration, string? ownerId = null)
    {
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "a fencing lease must have a positive duration");
        LeaseDuration = leaseDuration;
        OwnerId = ownerId;
    }

    /// <summary>How long an acquired lease stays valid without renewal.</summary>
    public TimeSpan LeaseDuration { get; }

    /// <summary>
    /// Who holds the lease. Null lets the store generate a unique owner
    /// id per store instance (machine, process, random suffix) — two
    /// store objects then never share an identity by accident. Pin an id
    /// only when one logical host deliberately spans store objects.
    /// </summary>
    public string? OwnerId { get; }

    /// <summary>The owner id a store actually uses: the pinned one, or a generated unique one.</summary>
    internal string ResolveOwnerId() =>
        OwnerId ?? $"{Environment.MachineName}-{Environment.ProcessId}-{Guid.NewGuid():N}";
}

/// <summary>
/// The loud refusal of fencing: this store's lease is no longer the
/// instance's current lease — it expired, or a successor took over with
/// a higher fencing token. The append did not happen; history is
/// untouched. The writer must re-acquire through <c>Load</c> (and
/// re-read what the successor wrote) before writing again.
/// </summary>
public sealed class StaleFencingTokenException : Exception
{
    public StaleFencingTokenException(string instanceId, string ownerId, long token, string currentState)
        : base($"stale fencing token: this store writes for instance '{instanceId}' as owner '{ownerId}' " +
               $"with fencing token {token}, but {currentState} — the append was refused and history is untouched; " +
               "re-load to re-acquire the lease before writing")
    {
        InstanceId = instanceId;
        OwnerId = ownerId;
        Token = token;
    }

    public string InstanceId { get; }
    public string OwnerId { get; }
    public long Token { get; }
}
