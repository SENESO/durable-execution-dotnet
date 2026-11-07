using System.Diagnostics;
using System.Diagnostics.Metrics;
using DurableExecution.Execution;
using DurableExecution.History;

namespace DurableExecution.Tests.Unit;

/// <summary>
/// The OpenTelemetry surface, proven with in-memory listeners (the
/// exporter's seat, without any exporter): a workflow run emits the
/// documented spans — workflow, one activity span per executed command,
/// one store-append span per record with its event type — and the
/// documented measurements with their names and tags. Everything is
/// filtered by this test's unique instance id, so parallel suites
/// cannot pollute the evidence.
/// </summary>
public sealed class TelemetryTests : IDisposable
{
    private readonly List<Activity> _spans = new();
    private readonly List<(string Instrument, long Value, KeyValuePair<string, object?>[] Tags)> _measurements = new();
    private readonly ActivityListener _activityListener;
    private readonly MeterListener _meterListener;
    private readonly string _instance = "otel-" + Guid.NewGuid().ToString("N")[..8];

    public TelemetryTests()
    {
        _activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "DurableExecution",
            Sample = (ref ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
        };
        _activityListener.ActivityStopped = activity =>
        {
            lock (_spans) _spans.Add(activity);
        };
        ActivitySource.AddActivityListener(_activityListener);

        _meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "DurableExecution")
                    listener.EnableMeasurementEvents(instrument);
            },
        };
        _meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            lock (_measurements) _measurements.Add((instrument.Name, value, tags.ToArray()));
        });
        _meterListener.Start();
    }

    public void Dispose()
    {
        _activityListener.Dispose();
        _meterListener.Dispose();
    }

    private static string? Tag(Activity activity, string key) =>
        activity.TagObjects.FirstOrDefault(t => t.Key == key).Value as string;

    private bool Mine(Activity activity) => Tag(activity, "durableexecution.instance_id") == _instance;

    private static bool HasTag((string Instrument, long Value, KeyValuePair<string, object?>[] Tags) m, string key, string value) =>
        m.Tags.Any(t => t.Key == key && (string?)t.Value == value);

    [Fact]
    public async Task WorkflowRun_EmitsTheDocumentedSpansAndMeasurements()
    {
        string dir = Path.Combine(Path.GetTempPath(), "p0-otel", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        using (var host = new WorkflowHost(new FileHistoryStore(dir), dir))
        {
            var outcome = await host.RunAsync("order/v1", _instance, new { x = 1 }, async ctx =>
            {
                await ctx.CallActivityAsync("ChargeCard", new { amount = 5 }, actx => Task.FromResult("txn"));
                await ctx.CallActivityAsync("ReserveStock", new { orderId = "o" }, actx => Task.FromResult("ok"));
                return "txn";
            });
            Assert.Equal(WorkflowRunOutcome.Completed, outcome);
        }

        List<Activity> spans;
        lock (_spans) spans = _spans.ToList();

        var workflow = Assert.Single(spans, a => a.OperationName == "DurableExecution.Workflow" && Mine(a));
        Assert.Equal("order/v1", Tag(workflow, "durableexecution.definition_id"));
        Assert.Equal("completed", Tag(workflow, "durableexecution.outcome"));

        var activities = spans.Where(a => a.OperationName == "DurableExecution.Activity" && Mine(a)).ToList();
        Assert.Equal(2, activities.Count);
        Assert.Contains(activities, a => Tag(a, "durableexecution.activity") == "ChargeCard");
        Assert.Contains(activities, a => Tag(a, "durableexecution.activity") == "ReserveStock");

        var appendSpans = spans.Where(a => a.OperationName == "DurableExecution.StoreAppend" && Mine(a)).ToList();
        Assert.All(appendSpans, a => Assert.Equal("file", Tag(a, "durableexecution.store")));
        Assert.Contains(appendSpans, a => Tag(a, "durableexecution.event_type") == "WorkflowStarted");
        Assert.Contains(appendSpans, a => Tag(a, "durableexecution.event_type") == "ActivityScheduled");
        Assert.Contains(appendSpans, a => Tag(a, "durableexecution.event_type") == "ActivityAttemptStarted");
        Assert.Contains(appendSpans, a => Tag(a, "durableexecution.event_type") == "ActivityCompleted");
        Assert.Contains(appendSpans, a => Tag(a, "durableexecution.event_type") == "WorkflowCompleted");

        List<(string Instrument, long Value, KeyValuePair<string, object?>[] Tags)> measurements;
        lock (_measurements) measurements = _measurements.ToList();

        var appendMeasurements = measurements
            .Where(m => m.Instrument == "durableexecution.store.appends" &&
                        HasTag(m, "durableexecution.instance_id", _instance))
            .ToList();
        Assert.Equal(8, appendMeasurements.Sum(m => m.Value)); // started + 2 x (scheduled, attempt, completed) + workflow completed
        Assert.Contains(appendMeasurements, m => HasTag(m, "durableexecution.event_type", "ActivityScheduled"));
        Assert.All(appendMeasurements, m => Assert.True(HasTag(m, "durableexecution.store", "file")));

        Assert.Contains(measurements, m =>
            m.Instrument == "durableexecution.workflow.runs" &&
            HasTag(m, "durableexecution.instance_id", _instance) &&
            HasTag(m, "durableexecution.outcome", "completed") && m.Value == 1);
        Assert.Equal(2, measurements
            .Where(m => m.Instrument == "durableexecution.activity.executions" &&
                        HasTag(m, "durableexecution.instance_id", _instance) &&
                        HasTag(m, "durableexecution.outcome", "completed"))
            .Sum(m => m.Value));
    }
}
