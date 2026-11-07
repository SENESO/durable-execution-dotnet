namespace DurableExecution.Execution;

/// <summary>
/// The engine's clock. Durable waits (retry backoff, timers) read time and
/// delay only through this seam, so tests can substitute a fake clock and
/// prove timing behaviour without waiting on the wall clock. Production
/// hosts get <see cref="SystemWorkflowClock"/>.
/// </summary>
public interface IWorkflowClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>Waits for the delay, or throws <see cref="OperationCanceledException"/> if the token fires first.</summary>
    Task Delay(TimeSpan delay, CancellationToken cancellationToken = default);
}

/// <summary>The wall clock: <see cref="DateTimeOffset.UtcNow"/> and <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</summary>
public sealed class SystemWorkflowClock : IWorkflowClock
{
    public static readonly SystemWorkflowClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken = default) =>
        delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, cancellationToken);
}
