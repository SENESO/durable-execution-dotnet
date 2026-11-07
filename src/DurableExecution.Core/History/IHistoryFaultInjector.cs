namespace DurableExecution.History;

/// <summary>
/// Fault-injection seam for the crash-injection harness (foundation §7.1: the
/// fault suite is a product deliverable, and kill points inside the persistence
/// layer are part of its design). When an injector is attached, the store asks
/// it about each append; a torn write leaves the append uncommitted and then
/// hands control to the injector — which in the harness kills the host
/// process, exactly as a real mid-write crash would. What "uncommitted" looks
/// like is the provider's framing: the file provider writes part of the framed
/// record without its terminating newline and flushes it; a transactional
/// provider dies inside the append transaction, which the server rolls back.
/// Production hosts attach no injector.
/// </summary>
public interface IHistoryFaultInjector
{
    /// <summary>True if this append should be torn (left uncommitted by the provider's framing).</summary>
    bool ShouldTear(HistoryEvent historyEvent);

    /// <summary>Called once the uncommitted state is in place (partial bytes flushed, or the transaction open and uncommitted).</summary>
    void OnTornWrite();
}
