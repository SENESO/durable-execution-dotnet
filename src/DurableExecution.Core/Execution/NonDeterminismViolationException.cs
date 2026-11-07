namespace DurableExecution.Execution;

/// <summary>
/// Replay reached a step that contradicts recorded history: the workflow
/// definition changed, the workflow's input fingerprint changed, or a command
/// at a recorded position now names a different activity or carries different
/// inputs. Replay halts loudly — never silent divergence (foundation §6.3).
/// Detection is by refusal: the engine does not mint a new idempotency key to
/// paper over a changed operation (ADR-012).
/// </summary>
public sealed class NonDeterminismViolationException : Exception
{
    public NonDeterminismViolationException(string message) : base(message) { }
}
