using DurableExecution.History;

namespace DurableExecution.Execution;

/// <summary>
/// The orchestration surface of P0 (foundation §5.3, reduced subset of §14):
/// sequential awaits of durable activity calls, with ordinary synchronous C#
/// between them. Each call is a <i>command</i>: the first time a position is
/// reached, the command is scheduled (activity identity, input fingerprint,
/// idempotency key recorded); on replay the recorded outcome is returned
/// without executing the activity again. Asking a recorded position for a
/// different activity or different inputs is a
/// <see cref="NonDeterminismViolationException"/>, never a silent re-keying.
///
/// P0 form: the activity body travels with the call. Resolving activities by
/// name from a registry is a developer-experience step beyond P0; the durable
/// semantics — scheduling, identity, replay — are the ones the evidence suite
/// pins down here.
/// </summary>
public sealed class WorkflowContext
{
    private readonly string _namespaceId;
    private readonly string _instanceId;
    private readonly IHistoryStore _store;
    private readonly List<HistoryEvent> _history;
    private long _commandSequence;

    internal WorkflowContext(string namespaceId, string instanceId, IHistoryStore store, List<HistoryEvent> history)
    {
        _namespaceId = namespaceId;
        _instanceId = instanceId;
        _store = store;
        _history = history;
    }

    public string InstanceId => _instanceId;

    /// <summary>
    /// Calls an activity durably. If this command's outcome is already
    /// recorded, the recorded result is returned and the activity is
    /// <b>not</b> executed. Otherwise the activity executes (at-least-once:
    /// a crash between execution and recording means it will execute again
    /// on recovery, carrying the same idempotency key).
    /// </summary>
    public async Task<T> CallActivityAsync<T>(string activity, object? input, Func<ActivityContext, Task<T>> body)
    {
        long seq = ++_commandSequence;
        string inputFingerprint = Fingerprints.InputFingerprint(input);

        var scheduled = _history.FirstOrDefault(e =>
            e.Type == HistoryEventTypes.ActivityScheduled && e.CommandSequence == seq);
        string key;
        if (scheduled is not null)
        {
            string recordedActivity = scheduled.GetString("activity")!;
            if (recordedActivity != activity)
                throw new NonDeterminismViolationException(
                    $"command {seq} was recorded as '{recordedActivity}' but the code now requests '{activity}'");
            if (scheduled.GetString("inputHash") != inputFingerprint)
                throw new NonDeterminismViolationException(
                    $"command {seq} ('{activity}') input fingerprint changed since it was first scheduled");
            key = scheduled.GetString("idempotencyKey")!; // the recorded key, verbatim

            var done = _history.FirstOrDefault(e =>
                e.Type == HistoryEventTypes.ActivityCompleted && e.CommandSequence == seq);
            if (done is not null)
                return done.GetResult<T>()!;
        }
        else
        {
            key = Fingerprints.DeriveIdempotencyKey(_namespaceId, _instanceId, seq, activity);
            Append(HistoryEvent.ActivityScheduled(_instanceId, seq, activity, key, inputFingerprint));
        }

        long attempt = _history.Count(e =>
            e.Type == HistoryEventTypes.ActivityAttemptStarted && e.CommandSequence == seq) + 1;
        Append(HistoryEvent.ActivityAttemptStarted(_instanceId, seq, attempt));

        T result = await body(new ActivityContext(seq, activity, key, attempt));

        Append(HistoryEvent.ActivityCompleted(_instanceId, seq, activity, result));
        return result;
    }

    private void Append(HistoryEvent historyEvent)
    {
        // Keep the in-memory view in step with the store: the framed event the
        // store recorded is the one replay consults for the rest of this run.
        _history.Add(_store.Append(historyEvent));
    }
}
