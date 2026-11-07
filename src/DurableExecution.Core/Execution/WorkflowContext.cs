using DurableExecution.History;

namespace DurableExecution.Execution;

/// <summary>
/// The orchestration surface of P0 (foundation §5.3, reduced subset of §14):
/// sequential awaits of durable activity calls, a fan-out combinator
/// (<see cref="CallAllAsync{T}"/>) over the same commands, and ordinary
/// synchronous C# between them. Each call is a <i>command</i>: the first time
/// a position is reached, the command is scheduled (activity identity, input
/// fingerprint, idempotency key recorded); on replay the recorded outcome is
/// returned without executing the activity again. Asking a recorded position
/// for a different activity or different inputs is a
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
    private readonly object _appendGate = new();
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
        var slot = ScheduleOrValidate(activity, input);
        if (slot.Completed is not null)
            return slot.Completed.GetResult<T>()!;
        return await ExecuteAsync(slot, activity, body);
    }

    /// <summary>
    /// Fans out over a list of activity calls — the <c>Task.WhenAll</c> of
    /// this surface (§5.3). Branches are <b>scheduled</b> synchronously, in
    /// list order, before any of them executes: each branch takes the next
    /// command sequence, is fingerprint-checked against history exactly like
    /// a sequential call at the same position, and derives its ADR-012
    /// idempotency key from that position — so branch identity never depends
    /// on the order branches happen to finish in. Branches without a recorded
    /// completion then execute concurrently; each completion is recorded as
    /// it lands. The returned array is in <b>list order</b>, regardless of
    /// completion order: the combination is a pure function of the schedule,
    /// identical on the first run and on every replay.
    ///
    /// Durability semantics are the sequential ones, per branch: a branch
    /// whose completion is recorded is never re-executed; a branch that was
    /// scheduled or attempted but not recorded complete re-executes on
    /// recovery under its recorded key (at-least-once — the guarantee
    /// contract is unchanged; nothing here claims exactly-once effects).
    ///
    /// If any branch faults, the remaining branches still run to completion
    /// (their results are recorded), then the combinator throws an
    /// <see cref="AggregateException"/> carrying every branch fault, like
    /// <c>Task.WhenAll</c>. The faulted branches have an attempt recorded and
    /// no completion, so a resume re-executes exactly those.
    /// </summary>
    public async Task<T[]> CallAllAsync<T>(IReadOnlyList<ActivityCall<T>> calls)
    {
        ArgumentNullException.ThrowIfNull(calls);

        // Phase 1 — scheduling. Synchronous and in list order: positions,
        // fingerprints and keys are settled before any branch body runs, so
        // a changed branch definition is caught here, before any execution,
        // and a crash after this phase leaves every branch scheduled.
        var slots = new CommandSlot[calls.Count];
        for (int i = 0; i < calls.Count; i++)
            slots[i] = ScheduleOrValidate(calls[i].Activity, calls[i].Input);

        // Phase 2 — execution. Branches with a recorded completion replay
        // from history; the rest are dispatched concurrently. Results are
        // placed by branch index, never by arrival.
        var results = new T[calls.Count];
        var faults = new Exception?[calls.Count];
        var pending = new List<Task>(calls.Count);
        for (int i = 0; i < calls.Count; i++)
        {
            int branch = i;
            var slot = slots[branch];
            if (slot.Completed is not null)
            {
                results[branch] = slot.Completed.GetResult<T>()!;
                continue;
            }
            pending.Add(Task.Run(async () =>
            {
                try
                {
                    results[branch] = await ExecuteAsync(slots[branch], calls[branch].Activity, calls[branch].Body);
                }
                catch (Exception e)
                {
                    faults[branch] = e;
                }
            }));
        }
        await Task.WhenAll(pending);

        var errors = faults.OfType<Exception>().ToList();
        if (errors.Count > 0)
            throw new AggregateException(
                $"fan-out: {errors.Count} of {calls.Count} branches faulted; completed branches keep " +
                "their recorded results, faulted branches re-execute on resume", errors);
        return results;
    }

    /// <summary>
    /// The non-generic fan-out: branches that return no result. Same
    /// scheduling, concurrency, and per-branch durability semantics as
    /// <see cref="CallAllAsync{T}"/>.
    /// </summary>
    public async Task CallAllAsync(IReadOnlyList<ActivityCall> calls)
    {
        ArgumentNullException.ThrowIfNull(calls);
        var wrapped = calls.Select(c => new ActivityCall<object?>(
            c.Activity, c.Input, async actx => { await c.Body(actx); return null; })).ToList();
        await CallAllAsync(wrapped);
    }

    /// <summary>One command's scheduling outcome: its position, its key, and its recorded completion if it has one.</summary>
    private sealed class CommandSlot
    {
        public required long Sequence { get; init; }
        public required string IdempotencyKey { get; init; }
        public HistoryEvent? Completed { get; init; }
    }

    /// <summary>
    /// Takes the next command position and settles it against history: a
    /// recorded schedule must name the same activity and carry the same input
    /// fingerprint (anything else is a determinism violation), and its
    /// recorded key is reused verbatim; an unrecorded position is scheduled
    /// now, deriving its key from its position (ADR-012).
    /// </summary>
    private CommandSlot ScheduleOrValidate(string activity, object? input)
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
        }
        else
        {
            key = Fingerprints.DeriveIdempotencyKey(_namespaceId, _instanceId, seq, activity);
            Append(HistoryEvent.ActivityScheduled(_instanceId, seq, activity, key, inputFingerprint));
        }

        var completed = _history.FirstOrDefault(e =>
            e.Type == HistoryEventTypes.ActivityCompleted && e.CommandSequence == seq);
        return new CommandSlot { Sequence = seq, IdempotencyKey = key, Completed = completed };
    }

    /// <summary>
    /// Executes one scheduled command: records the attempt (numbered under
    /// the append gate, so concurrent branches can never interleave attempt
    /// numbers), runs the body with the command's recorded key, and records
    /// the completion. A throw from the body leaves the attempt unrecorded-
    /// complete — the at-least-once window, identical to the sequential path.
    /// </summary>
    private async Task<T> ExecuteAsync<T>(CommandSlot slot, string activity, Func<ActivityContext, Task<T>> body)
    {
        long attempt;
        lock (_appendGate)
        {
            attempt = _history.Count(e =>
                e.Type == HistoryEventTypes.ActivityAttemptStarted && e.CommandSequence == slot.Sequence) + 1;
            AppendLocked(HistoryEvent.ActivityAttemptStarted(_instanceId, slot.Sequence, attempt));
        }

        T result = await body(new ActivityContext(slot.Sequence, activity, slot.IdempotencyKey, attempt));

        Append(HistoryEvent.ActivityCompleted(_instanceId, slot.Sequence, activity, result));
        return result;
    }

    private void Append(HistoryEvent historyEvent)
    {
        lock (_appendGate)
            AppendLocked(historyEvent);
    }

    private void AppendLocked(HistoryEvent historyEvent)
    {
        // Keep the in-memory view in step with the store: the framed event the
        // store recorded is the one replay consults for the rest of this run.
        // The store itself is single-threaded (event sequences are assigned at
        // append time), so every append — sequential or fan-out — is
        // serialized through this gate.
        _history.Add(_store.Append(historyEvent));
    }
}
