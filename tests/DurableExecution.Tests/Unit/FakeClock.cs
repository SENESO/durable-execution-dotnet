using DurableExecution.Execution;

namespace DurableExecution.Tests.Unit;

/// <summary>
/// A test clock: delays are recorded and complete immediately, advancing
/// the clock by the delay — so retry backoff schedules can be asserted
/// exactly, without waiting on the wall clock.
/// </summary>
public sealed class FakeClock : IWorkflowClock
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow => _now;

    /// <summary>Every delay the engine requested, in order.</summary>
    public List<TimeSpan> Delays { get; } = new();

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        Delays.Add(delay);
        _now += delay;
        return Task.CompletedTask;
    }
}
