using DurableExecution.Execution;

namespace DurableExecution.Tests.Unit;

/// <summary>
/// A clock the test drives by hand: <see cref="Delay"/> blocks until
/// <see cref="Advance"/> moves time past the delay's deadline. Used to
/// prove durable-timer behaviour — what fires, when, and what survives a
/// host dying mid-wait — without sleeping on the wall clock.
/// </summary>
public sealed class ManualClock : IWorkflowClock
{
    private readonly object _gate = new();
    private DateTimeOffset _now;
    private TaskCompletionSource _changed = NewSignal();
    private readonly TaskCompletionSource _firstDelayEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ManualClock(DateTimeOffset start) => _now = start;

    public DateTimeOffset UtcNow
    {
        get { lock (_gate) return _now; }
    }

    /// <summary>Completes the first time any delay is entered — the point where a scheduled timer is durable and the wait has begun.</summary>
    public Task FirstDelayEntered => _firstDelayEntered.Task;

    public async Task Delay(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        Task wait;
        DateTimeOffset deadline;
        lock (_gate)
        {
            deadline = _now + delay;
            if (delay <= TimeSpan.Zero)
                return;
            wait = _changed.Task;
        }
        _firstDelayEntered.TrySetResult();
        while (true)
        {
            await wait.WaitAsync(cancellationToken);
            lock (_gate)
            {
                if (_now >= deadline)
                    return;
                wait = _changed.Task;
            }
        }
    }

    public void Advance(TimeSpan by)
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            _now += by;
            signal = _changed;
            _changed = NewSignal();
        }
        signal.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
