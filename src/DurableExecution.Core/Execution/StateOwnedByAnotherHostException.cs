namespace DurableExecution.Execution;

/// <summary>
/// Another running host already owns this instance's state (single-writer
/// ownership, held as an OS file lock for the host's lifetime). A second host
/// is refused rather than allowed to interleave appends. This is the host's
/// coarse ownership guard on one machine; the stores' lease fencing
/// (foundation §9, P3 — see <see cref="History.FencingOptions"/>) is the
/// finer, token-based authority for hosts that cannot hold a lock.
/// </summary>
public sealed class StateOwnedByAnotherHostException : Exception
{
    public StateOwnedByAnotherHostException(string message) : base(message) { }
}
