using System.Diagnostics;
using System.Diagnostics.Metrics;
using DurableExecution.History;

namespace DurableExecution.Diagnostics;

/// <summary>
/// The engine's OpenTelemetry surface: one <see cref="ActivitySource"/>
/// and one <see cref="Meter"/>, both named <c>DurableExecution</c>, so
/// any standard OTEL setup (a listener in-process, or an
/// OpenTelemetry SDK exporter) can observe runs without the engine
/// taking a dependency on any exporter.
///
/// Spans:
/// <list type="bullet">
/// <item><c>DurableExecution.Workflow</c> — one per
/// <c>WorkflowHost.RunAsync</c> call (tags: definition id, instance id,
/// and the outcome on completion).</item>
/// <item><c>DurableExecution.Activity</c> — one per activity command
/// execution, retries included (tags: activity, command sequence,
/// instance id). A command replayed from its recorded completion is
/// not an execution and produces no span.</item>
/// <item><c>DurableExecution.StoreAppend</c> — one per history record
/// append attempt on a store (tags: store, event type, instance id).
/// Activity schedule / attempt / complete are visible here as appends
/// of their event types.</item>
/// </list>
///
/// Measurements (counters):
/// <list type="bullet">
/// <item><c>durableexecution.store.appends</c> — successful appends,
/// tagged by store, event type, instance.</item>
/// <item><c>durableexecution.workflow.runs</c> — run outcomes
/// (completed / already_completed / failed), tagged by instance.</item>
/// <item><c>durableexecution.activity.executions</c> — activity body
/// executions by outcome, tagged by activity and instance.</item>
/// </list>
///
/// Overhead, stated honestly: with no listener registered, starting a
/// span is a null check and recording a measurement on an unobserved
/// instrument is close to free — but not literally zero, and this
/// engine does not claim zero. With listeners registered, spans
/// allocate and measurements aggregate per call; that cost is the
/// standard OpenTelemetry cost, paid only by deployments that opt
/// into observability by adding a listener.
/// </summary>
public static class EngineDiagnostics
{
    /// <summary>The engine's activity source (name: <c>DurableExecution</c>).</summary>
    public static readonly ActivitySource ActivitySource = new("DurableExecution");

    /// <summary>The engine's meter (name: <c>DurableExecution</c>).</summary>
    public static readonly Meter Meter = new("DurableExecution");

    internal static readonly Counter<long> StoreAppends = Meter.CreateCounter<long>(
        "durableexecution.store.appends",
        description: "History records successfully appended, by store and event type.");

    internal static readonly Counter<long> WorkflowRuns = Meter.CreateCounter<long>(
        "durableexecution.workflow.runs",
        description: "Workflow runs by outcome.");

    internal static readonly Counter<long> ActivityExecutions = Meter.CreateCounter<long>(
        "durableexecution.activity.executions",
        description: "Activity body executions by outcome (replayed completions are not executions).");

    internal static Activity? StartWorkflowSpan(string definitionId, string instanceId)
    {
        var span = ActivitySource.StartActivity("DurableExecution.Workflow");
        span?.SetTag("durableexecution.definition_id", definitionId);
        span?.SetTag("durableexecution.instance_id", instanceId);
        return span;
    }

    internal static Activity? StartActivitySpan(string activity, long commandSequence, string instanceId)
    {
        var span = ActivitySource.StartActivity("DurableExecution.Activity");
        span?.SetTag("durableexecution.activity", activity);
        span?.SetTag("durableexecution.command_sequence", commandSequence);
        span?.SetTag("durableexecution.instance_id", instanceId);
        return span;
    }

    internal static Activity? StartStoreAppendSpan(string store, string eventType, string instanceId)
    {
        var span = ActivitySource.StartActivity("DurableExecution.StoreAppend");
        span?.SetTag("durableexecution.store", store);
        span?.SetTag("durableexecution.event_type", eventType);
        span?.SetTag("durableexecution.instance_id", instanceId);
        return span;
    }

    internal static void RecordStoreAppend(string store, HistoryEvent framed)
    {
        StoreAppends.Add(1,
            new KeyValuePair<string, object?>("durableexecution.store", store),
            new KeyValuePair<string, object?>("durableexecution.event_type", framed.Type),
            new KeyValuePair<string, object?>("durableexecution.instance_id", framed.InstanceId));
    }

    internal static void RecordWorkflowRun(string instanceId, string outcome)
    {
        WorkflowRuns.Add(1,
            new KeyValuePair<string, object?>("durableexecution.instance_id", instanceId),
            new KeyValuePair<string, object?>("durableexecution.outcome", outcome));
    }

    internal static void RecordActivityExecution(string activity, string instanceId, string outcome)
    {
        ActivityExecutions.Add(1,
            new KeyValuePair<string, object?>("durableexecution.activity", activity),
            new KeyValuePair<string, object?>("durableexecution.instance_id", instanceId),
            new KeyValuePair<string, object?>("durableexecution.outcome", outcome));
    }
}
