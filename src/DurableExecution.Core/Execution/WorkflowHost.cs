using DurableExecution.History;

namespace DurableExecution.Execution;

/// <summary>How a workflow run ended.</summary>
public enum WorkflowRunOutcome
{
    /// <summary>The workflow body ran (or resumed) and its completion was recorded.</summary>
    Completed,

    /// <summary>History already records this instance as completed; nothing was executed.</summary>
    AlreadyCompleted,
}

/// <summary>
/// The in-process execution host of stage A (foundation §4.1): owns one
/// instance's state directory, holds single-writer ownership of it, loads and
/// validates history, checks the workflow's definition + input fingerprints
/// before any command runs, drives the workflow body against
/// <see cref="WorkflowContext"/>, and records completion.
///
/// One host runs one instance (the P0 shape). Crash at any point is safe:
/// un-recorded work was never acknowledged, and recorded work replays from
/// history on the next host.
/// </summary>
public sealed class WorkflowHost : IDisposable
{
    private readonly IHistoryStore _store;
    private readonly string _namespaceId;
    private readonly FileStream _ownershipLock;

    /// <param name="store">The history store for the instance (already bound to its state directory).</param>
    /// <param name="stateDirectory">Directory holding the single-writer lock file.</param>
    /// <param name="namespaceId">Tenant namespace (ADR-010: shaped from v1; P0 runs one namespace).</param>
    /// <exception cref="StateOwnedByAnotherHostException">Another running host owns this state.</exception>
    public WorkflowHost(IHistoryStore store, string stateDirectory, string namespaceId = "default")
    {
        _store = store;
        _namespaceId = namespaceId;
        Directory.CreateDirectory(stateDirectory);
        string lockPath = Path.Combine(stateDirectory, "host.lock");
        try
        {
            _ownershipLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new StateOwnedByAnotherHostException(
                "state is owned by another running host — refusing to start (single-writer)");
        }
    }

    /// <summary>The most recent load's outcome, including torn-tail repair.</summary>
    public HistoryLoadResult? LastLoad { get; private set; }

    /// <summary>
    /// Starts or resumes the instance. On resume, the recorded definition id
    /// and workflow input fingerprint are re-checked before the body runs —
    /// a changed definition or input is a
    /// <see cref="NonDeterminismViolationException"/>, not a migration.
    /// </summary>
    /// <param name="definitionId">Stable identifier of the workflow definition (recorded in <c>WorkflowStarted</c>).</param>
    /// <param name="instanceId">The workflow instance id.</param>
    /// <param name="input">The workflow input; its fingerprint is recorded and re-checked.</param>
    /// <param name="body">The orchestration body; receives the durable context. Its return value is recorded as the workflow result.</param>
    public async Task<WorkflowRunOutcome> RunAsync(
        string definitionId,
        string instanceId,
        object? input,
        Func<WorkflowContext, Task<object?>> body)
    {
        var load = _store.Load();
        LastLoad = load;
        var history = new List<HistoryEvent>(load.Events);
        string inputFingerprint = Fingerprints.InputFingerprint(input);

        var started = history.FirstOrDefault(e => e.Type == HistoryEventTypes.WorkflowStarted);
        if (started is not null)
        {
            string recordedDef = started.GetString("def")!;
            if (recordedDef != definitionId)
                throw new NonDeterminismViolationException(
                    $"workflow definition changed (recorded '{recordedDef}', current '{definitionId}')");
            if (started.GetString("inputHash") != inputFingerprint)
                throw new NonDeterminismViolationException(
                    "workflow input fingerprint changed since the workflow started");
            if (history.Any(e => e.Type == HistoryEventTypes.WorkflowCompleted))
                return WorkflowRunOutcome.AlreadyCompleted;
        }
        else
        {
            history.Add(_store.Append(
                HistoryEvent.WorkflowStarted(instanceId, definitionId, inputFingerprint)));
        }

        var context = new WorkflowContext(_namespaceId, instanceId, _store, history);
        object? result = await body(context);
        _store.Append(HistoryEvent.WorkflowCompleted(instanceId, result));
        return WorkflowRunOutcome.Completed;
    }

    /// <summary>
    /// Releases the single-writer lock file and, if the store holds resources
    /// of its own (a database provider's session lock, say), disposes the
    /// store with the host — the host owns the store it was given, so a
    /// disposed host never leaves a store's ownership claim behind.
    /// </summary>
    public void Dispose()
    {
        _ownershipLock.Dispose();
        (_store as IDisposable)?.Dispose();
    }
}
