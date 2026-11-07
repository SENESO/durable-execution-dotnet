namespace DurableExecution.Execution;

/// <summary>
/// A per-call retry policy for an activity (P2). A failed attempt is
/// recorded durably (<c>ActivityAttemptFailed</c>) before the engine waits,
/// so the failure count is part of history: a host that dies between
/// attempts resumes with the budget it actually has left, and a workflow
/// whose recorded failures reach <see cref="MaxAttempts"/> is exhausted —
/// resuming it re-throws <see cref="ActivityFailedException"/> without
/// executing the body again. An attempt interrupted by a crash (started,
/// never recorded failed or complete) does not consume budget: it never
/// finished, so it is not a failure, exactly as it is not a completion.
///
/// Backoff is a pure function of the failure count — no jitter — so the
/// schedule is identical on the first run and on every replay.
/// <see cref="MaxAttempts"/> counts failed attempts in total (not per
/// host), including the first.
/// </summary>
public sealed record RetryPolicy
{
    public RetryPolicy(int maxAttempts, TimeSpan initialDelay, double backoffMultiplier = 2.0, TimeSpan? maxDelay = null)
    {
        if (maxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), "a policy must allow at least one attempt");
        if (initialDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(initialDelay), "delay cannot be negative");
        if (backoffMultiplier < 1.0)
            throw new ArgumentOutOfRangeException(nameof(backoffMultiplier), "backoff multiplier cannot shrink the delay");
        if (maxDelay is { } cap && cap < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxDelay), "delay cap cannot be negative");
        MaxAttempts = maxAttempts;
        InitialDelay = initialDelay;
        BackoffMultiplier = backoffMultiplier;
        MaxDelay = maxDelay;
    }

    /// <summary>Total attempts allowed, including the first.</summary>
    public int MaxAttempts { get; }

    /// <summary>Wait after the first recorded failure.</summary>
    public TimeSpan InitialDelay { get; }

    /// <summary>Growth factor per additional failure.</summary>
    public double BackoffMultiplier { get; }

    /// <summary>Optional cap on any single wait.</summary>
    public TimeSpan? MaxDelay { get; }

    /// <summary>The wait after recorded failure number <paramref name="failedAttempts"/> (1-based).</summary>
    public TimeSpan DelayAfterFailedAttempt(int failedAttempts)
    {
        if (failedAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(failedAttempts));
        double ms = InitialDelay.TotalMilliseconds * Math.Pow(BackoffMultiplier, failedAttempts - 1);
        if (MaxDelay is { } cap && ms > cap.TotalMilliseconds)
            ms = cap.TotalMilliseconds;
        return TimeSpan.FromMilliseconds(ms);
    }
}

/// <summary>
/// An activity exhausted its <see cref="RetryPolicy"/>: every allowed
/// attempt is recorded as failed in history. The workflow does not
/// complete. Resuming re-throws this exception deterministically from the
/// recorded failures, without executing the activity again.
/// </summary>
public sealed class ActivityFailedException : Exception
{
    public ActivityFailedException(string activity, int failedAttempts, Exception? lastFailure)
        : base($"activity '{activity}' exhausted its retry policy after {failedAttempts} failed attempt(s)" +
               (lastFailure is null ? " (recorded in history; the activity was not executed again)" : $": {lastFailure.Message}"),
               lastFailure)
    {
        Activity = activity;
        FailedAttempts = failedAttempts;
    }

    public string Activity { get; }

    /// <summary>How many attempts are recorded as failed in history.</summary>
    public int FailedAttempts { get; }
}
