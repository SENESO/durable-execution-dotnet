namespace DurableExecution.Execution;

/// <summary>
/// Another running host already owns this instance's state (single-writer
/// ownership, held as an OS file lock for the host's lifetime). A second host
/// is refused rather than allowed to interleave appends. This is spike-level
/// ownership on one machine; lease fencing across machines is a later phase
/// (foundation §9, P3).
/// </summary>
public sealed class StateOwnedByAnotherHostException : Exception
{
    public StateOwnedByAnotherHostException(string message) : base(message) { }
}
