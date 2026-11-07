namespace DurableExecution.Execution;

/// <summary>
/// What the engine hands an activity when it executes. The idempotency key is
/// computed by the engine from the command's stable identity and passed in —
/// activities never recompute it and never learn their own command sequence
/// as an input to identity. On replay the <i>recorded</i> key is passed
/// verbatim, so a re-executed attempt presents the same key as the first
/// attempt (foundation §2.1 item 4, ADR-012).
/// </summary>
/// <param name="CommandSequence">The command's position within the instance (1-based).</param>
/// <param name="Activity">The activity's stable identity (its name).</param>
/// <param name="IdempotencyKey">The stable key for outbound calls (gateway key, HTTP header, ...).</param>
/// <param name="Attempt">Which physical attempt this execution is (1-based). Never part of the key.</param>
public sealed record ActivityContext(
    long CommandSequence,
    string Activity,
    string IdempotencyKey,
    long Attempt);
