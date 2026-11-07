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
    private readonly IWorkflowClock _clock;
    private readonly object _appendGate = new();
    private readonly List<HistoryEvent> _signals = new();
    private readonly Dictionary<string, int> _signalConsumption = new();
    private readonly Dictionary<string, TaskCompletionSource> _signalWaiters = new();
    private long _commandSequence;

    internal WorkflowContext(string namespaceId, string instanceId, IHistoryStore store, List<HistoryEvent> history,
        IWorkflowClock? clock = null)
    {
        _namespaceId = namespaceId;
        _instanceId = instanceId;
        _store = store;
        _history = history;
        _clock = clock ?? SystemWorkflowClock.Instance;
        _signals.AddRange(history.Where(e => e.Type == HistoryEventTypes.SignalReceived));
    }

    /// <summary>The engine clock this context waits on (retry backoff, timers).</summary>
    internal IWorkflowClock Clock => _clock;

    public string InstanceId => _instanceId;

    /// <summary>
    /// Calls an activity durably. If this command's outcome is already
    /// recorded, the recorded result is returned and the activity is
    /// <b>not</b> executed. Otherwise the activity executes (at-least-once:
    /// a crash between execution and recording means it will execute again
    /// on recovery, carrying the same idempotency key).
    /// </summary>
    public async Task<T> CallActivityAsync<T>(string activity, object? input, Func<ActivityContext, Task<T>> body,
        RetryPolicy? retryPolicy = null)
    {
        var slot = ScheduleOrValidate(activity, input);
        if (slot.Completed is not null)
            return slot.Completed.GetResult<T>()!;
        return await ExecuteAsync(slot, activity, body, retryPolicy);
    }

    /// <summary>
    /// A durable timer (P2): waits until a due time that is recorded in
    /// history before the wait begins. The first time the position is
    /// reached, <c>TimerScheduled</c> settles the due time (engine clock +
    /// duration) and the requested duration; the wait then runs against
    /// the recorded due time, so a host that dies mid-wait resumes waiting
    /// only for the remainder — the timer fires at its original due time,
    /// not a fresh delay after the restart. <c>TimerFired</c> is recorded
    /// when the wait completes; a replay that finds it returns at once.
    /// Asking the recorded position for a different duration is a
    /// determinism violation, like changing an activity's input.
    /// </summary>
    public async Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        if (duration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), "timer duration cannot be negative");
        long seq = ++_commandSequence;

        var scheduled = _history.FirstOrDefault(e =>
            e.Type == HistoryEventTypes.TimerScheduled && e.CommandSequence == seq);
        DateTimeOffset dueAt;
        if (scheduled is not null)
        {
            if (scheduled.GetInt64("durationMs") != (long)duration.TotalMilliseconds)
                throw new NonDeterminismViolationException(
                    $"timer at command {seq} was recorded with duration {scheduled.GetInt64("durationMs")}ms " +
                    $"but the code now requests {(long)duration.TotalMilliseconds}ms");
            dueAt = DateTimeOffset.Parse(scheduled.GetString("dueAt")!);
        }
        else
        {
            if (_history.Any(e => e.Type == HistoryEventTypes.ActivityScheduled && e.CommandSequence == seq))
                throw new NonDeterminismViolationException(
                    $"command {seq} was recorded as an activity but the code now requests a timer there");
            dueAt = _clock.UtcNow + duration;
            Append(HistoryEvent.TimerScheduled(_instanceId, seq, dueAt, (long)duration.TotalMilliseconds));
        }

        if (_history.Any(e => e.Type == HistoryEventTypes.TimerFired && e.CommandSequence == seq))
            return;

        TimeSpan remaining = dueAt - _clock.UtcNow;
        if (remaining > TimeSpan.Zero)
            await _clock.Delay(remaining, cancellationToken);
        Append(HistoryEvent.TimerFired(_instanceId, seq));
    }

    /// <summary>
    /// Waits for an external signal (P2). Signals are recorded in history
    /// (<c>SignalReceived</c>) <b>before</b> they are delivered — a signal
    /// is never visible to the workflow before it is durable. Delivery
    /// consumes signals per name, in recorded order: the oldest
    /// unconsumed signal with the requested name is returned; signals for
    /// other names are untouched. A signal recorded before the run started
    /// (appended to the store while no host ran) is delivered from history
    /// exactly like one delivered live through the host. Waits take no
    /// command position — signals are external input, and their recorded
    /// order is what makes consumption deterministic across replays.
    /// </summary>
    public async Task<T> WaitForSignalAsync<T>(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        while (true)
        {
            TaskCompletionSource waiter;
            lock (_appendGate)
            {
                if (TryConsumeLocked<T>(name, out var payload))
                    return payload!;
                if (!_signalWaiters.TryGetValue(name, out var existing))
                {
                    existing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _signalWaiters[name] = existing;
                }
                waiter = existing;
            }
            await waiter.Task.WaitAsync(cancellationToken);
        }
    }

    private bool TryConsumeLocked<T>(string name, out T? payload)
    {
        int consumed = _signalConsumption.GetValueOrDefault(name);
        var matches = _signals.Where(e => e.GetString("name") == name).ToList();
        if (consumed < matches.Count)
        {
            _signalConsumption[name] = consumed + 1;
            payload = matches[consumed].GetResult<T>();
            return true;
        }
        payload = default;
        return false;
    }

    /// <summary>
    /// Records a signal in history and only then wakes a matching waiter —
    /// the recorded-before-delivered rule, enforced by ordering inside the
    /// append gate. Called by the host while a run is active.
    /// </summary>
    internal void DeliverSignal(string name, object? payload)
    {
        TaskCompletionSource? waiter;
        lock (_appendGate)
        {
            var framed = AppendLocked(HistoryEvent.SignalReceived(_instanceId, name, payload));
            _signals.Add(framed);
            _signalWaiters.Remove(name, out waiter);
        }
        waiter?.TrySetResult();
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
                    results[branch] = await ExecuteAsync(slots[branch], calls[branch].Activity, calls[branch].Body, calls[branch].RetryPolicy);
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
    /// Executes one scheduled command. Without a retry policy this is the
    /// P0 shape: record the attempt, run the body, record the completion;
    /// a throw leaves the attempt unrecorded-complete (the at-least-once
    /// window). With a policy, a failed attempt is recorded as failed
    /// (<c>ActivityAttemptFailed</c>) before the backoff wait, so the
    /// budget survives the host: a crash between attempts resumes with the
    /// recorded failure count, and a command whose recorded failures reach
    /// the policy's maximum is exhausted — it throws
    /// <see cref="ActivityFailedException"/> without executing again,
    /// on the first run and on every resume alike.
    /// </summary>
    private async Task<T> ExecuteAsync<T>(CommandSlot slot, string activity, Func<ActivityContext, Task<T>> body,
        RetryPolicy? retryPolicy)
    {
        long RecordedFailures() => _history.Count(e =>
            e.Type == HistoryEventTypes.ActivityAttemptFailed && e.CommandSequence == slot.Sequence);

        if (retryPolicy is not null && RecordedFailures() >= retryPolicy.MaxAttempts)
            throw new ActivityFailedException(activity, (int)RecordedFailures(), null);

        while (true)
        {
            long attempt;
            lock (_appendGate)
            {
                attempt = _history.Count(e =>
                    e.Type == HistoryEventTypes.ActivityAttemptStarted && e.CommandSequence == slot.Sequence) + 1;
                AppendLocked(HistoryEvent.ActivityAttemptStarted(_instanceId, slot.Sequence, attempt));
            }

            T result;
            try
            {
                result = await body(new ActivityContext(slot.Sequence, activity, slot.IdempotencyKey, attempt));
            }
            catch (Exception ex) when (retryPolicy is not null && ex is not OperationCanceledException)
            {
                Append(HistoryEvent.ActivityAttemptFailed(
                    _instanceId, slot.Sequence, attempt, $"{ex.GetType().Name}: {ex.Message}"));
                long failures = RecordedFailures();
                if (failures >= retryPolicy.MaxAttempts)
                    throw new ActivityFailedException(activity, (int)failures, ex);
                await _clock.Delay(retryPolicy.DelayAfterFailedAttempt((int)failures));
                continue;
            }

            Append(HistoryEvent.ActivityCompleted(_instanceId, slot.Sequence, activity, result));
            return result;
        }
    }

    private void Append(HistoryEvent historyEvent)
    {
        lock (_appendGate)
            AppendLocked(historyEvent);
    }

    private HistoryEvent AppendLocked(HistoryEvent historyEvent)
    {
        // Keep the in-memory view in step with the store: the framed event the
        // store recorded is the one replay consults for the rest of this run.
        // The store itself is single-threaded (event sequences are assigned at
        // append time), so every append — sequential or fan-out — is
        // serialized through this gate.
        var framed = _store.Append(historyEvent);
        _history.Add(framed);
        return framed;
    }
}
