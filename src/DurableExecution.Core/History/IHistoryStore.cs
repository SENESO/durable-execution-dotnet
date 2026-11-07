namespace DurableExecution.History;

/// <summary>
/// The persistence seam of the execution core: an append-only store for one
/// workflow instance's history (foundation §6.1, §6.2). This interface is what
/// the distributed runtime (stage B) and the future database providers consume;
/// every provider must pass the shared conformance contract of §6.5 before it
/// may ship. P0 ships one provider, <see cref="FileHistoryStore"/>.
/// </summary>
public interface IHistoryStore
{
    /// <summary>
    /// Loads and fully validates the recorded history. A torn (unterminated)
    /// tail record is truncated and reported via
    /// <see cref="HistoryLoadResult.TornTailTruncated"/>; committed damage
    /// throws <see cref="HistoryCorruptionException"/>.
    /// </summary>
    HistoryLoadResult Load();

    /// <summary>
    /// Appends one event, assigning its sequence, sealing it, and flushing it
    /// to durable storage before returning. Appends are O(1): the store keeps
    /// the loaded sequence counter instead of re-reading the log.
    /// </summary>
    /// <returns>The framed event as recorded (sequence and checksum assigned).</returns>
    HistoryEvent Append(HistoryEvent historyEvent);
}

/// <summary>The outcome of loading a history.</summary>
public sealed record HistoryLoadResult(
    IReadOnlyList<HistoryEvent> Events,
    bool TornTailTruncated,
    long TornTailBytes);
