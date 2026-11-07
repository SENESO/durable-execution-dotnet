namespace DurableExecution.History;

/// <summary>
/// Fault-injection seam for the crash-injection harness (foundation §7.1: the
/// fault suite is a product deliverable, and kill points inside the persistence
/// layer are part of its design). When an injector is attached, the store asks
/// it about each append; a torn write writes only part of the framed record,
/// without its terminating newline, and then hands control to the injector —
/// which in the harness kills the host process, leaving the torn record on
/// disk exactly as a real mid-write crash would. Production hosts attach no
/// injector.
/// </summary>
public interface IHistoryFaultInjector
{
    /// <summary>True if this append should be torn (partially written, unterminated).</summary>
    bool ShouldTear(HistoryEvent historyEvent);

    /// <summary>Called after the partial bytes are flushed to disk.</summary>
    void OnTornWrite();
}
