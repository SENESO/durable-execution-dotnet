namespace DurableExecution.History;

/// <summary>
/// The recorded history is damaged in a way that was committed to disk: invalid
/// framing content, a checksum mismatch, a sequence gap, or a logically invalid
/// transition. Recovery refuses to proceed — a corrupt history is never
/// silently repaired or skipped. (An <i>unterminated</i> tail record is a
/// different case: it was never committed, and the store truncates it instead —
/// see <see cref="FileHistoryStore"/>.)
/// </summary>
public sealed class HistoryCorruptionException : Exception
{
    public HistoryCorruptionException(string message) : base(message) { }
}
