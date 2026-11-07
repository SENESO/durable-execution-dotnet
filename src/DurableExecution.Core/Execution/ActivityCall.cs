namespace DurableExecution.Execution;

/// <summary>
/// One branch of a fan-out (<see cref="WorkflowContext.CallAllAsync{T}"/>):
/// the same triple a sequential <see cref="WorkflowContext.CallActivityAsync{T}"/>
/// call carries — the activity's stable identity, its input (fingerprinted at
/// scheduling time), and the body to execute. A branch's position in the list
/// is its scheduling position: command sequences and idempotency keys are
/// assigned in list order, never in completion order.
/// </summary>
/// <param name="Activity">The activity's stable identity (its name).</param>
/// <param name="Input">The branch input; its fingerprint is recorded and re-checked on replay.</param>
/// <param name="Body">The activity body; receives the branch's own <see cref="ActivityContext"/>.</param>
public sealed record ActivityCall<T>(string Activity, object? Input, Func<ActivityContext, Task<T>> Body)
{
    /// <summary>Optional retry policy for this branch; null means a faulted branch records no failure and re-executes only on resume.</summary>
    public RetryPolicy? RetryPolicy { get; init; }
}

/// <summary>
/// A fan-out branch whose body returns no result — the non-generic sibling of
/// <see cref="ActivityCall{T}"/>, mirroring <c>Task.WhenAll</c>'s two shapes.
/// </summary>
/// <param name="Activity">The activity's stable identity (its name).</param>
/// <param name="Input">The branch input; its fingerprint is recorded and re-checked on replay.</param>
/// <param name="Body">The activity body; receives the branch's own <see cref="ActivityContext"/>.</param>
public sealed record ActivityCall(string Activity, object? Input, Func<ActivityContext, Task> Body)
{
    /// <summary>Optional retry policy for this branch; null means a faulted branch records no failure and re-executes only on resume.</summary>
    public RetryPolicy? RetryPolicy { get; init; }
}
