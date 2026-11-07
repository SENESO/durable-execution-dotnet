using System.Text;
using System.Text.Json.Nodes;
using DurableExecution.History;

namespace DurableExecution.Tests.Unit;

/// <summary>
/// The store's framing and validation rules, tested directly: torn is not
/// corrupt, corrupt is never repaired silently, and the structural rules of
/// §6.1/ADR-014 hold on load.
/// </summary>
public class HistoryStoreTests
{
    private static string FreshDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "p0-unit", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static FileHistoryStore LoadedStore(string dir)
    {
        var store = new FileHistoryStore(dir);
        store.Load();
        return store;
    }

    /// <summary>Frames one raw record the way the store does (sequence + seal), for crafting histories by hand.</summary>
    private static string Frame(JsonObject fields, long n)
    {
        fields["n"] = n;
        fields["sum"] = FileHistoryStore.Checksum(fields);
        return fields.ToJsonString();
    }

    private static JsonObject RawEvent(string type, Action<JsonObject> fill)
    {
        var fields = new JsonObject
        {
            ["v"] = 1,
            ["type"] = type,
            ["instance"] = "inst-1",
            ["ts"] = DateTimeOffset.UtcNow.ToString("o"),
        };
        fill(fields);
        return fields;
    }

    private static void WriteHistory(string dir, params string[] lines) =>
        File.WriteAllText(Path.Combine(dir, "history.log"), string.Join("\n", lines) + "\n");

    [Fact]
    public void AppendAndLoad_RoundtripPreservesEventsAndSequences()
    {
        string dir = FreshDir();
        var store = LoadedStore(dir);
        store.Append(HistoryEvent.WorkflowStarted("inst-1", "order/v1", new string('a', 64)));
        store.Append(HistoryEvent.ActivityScheduled("inst-1", 1, "ChargeCard", new string('b', 64), new string('c', 64)));
        store.Append(HistoryEvent.ActivityAttemptStarted("inst-1", 1, 1));

        var reloaded = new FileHistoryStore(dir).Load();
        Assert.False(reloaded.TornTailTruncated);
        Assert.Equal(3, reloaded.Events.Count);
        Assert.Equal(new long[] { 1, 2, 3 }, reloaded.Events.Select(e => e.N).ToArray());
        Assert.Equal("ChargeCard", reloaded.Events[1].GetString("activity"));
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyHistory()
    {
        var result = new FileHistoryStore(FreshDir()).Load();
        Assert.Empty(result.Events);
        Assert.False(result.TornTailTruncated);
    }

    [Fact]
    public void Append_ContinuesSequenceAcrossStoreInstances()
    {
        string dir = FreshDir();
        LoadedStore(dir).Append(HistoryEvent.WorkflowStarted("inst-1", "order/v1", new string('a', 64)));

        var second = LoadedStore(dir);
        var appended = second.Append(HistoryEvent.WorkflowCompleted("inst-1", null));
        Assert.Equal(2, appended.N);
    }

    [Fact]
    public void Load_TornTail_IsTruncatedAndReported_NeverCommitted()
    {
        string dir = FreshDir();
        var store = LoadedStore(dir);
        store.Append(HistoryEvent.WorkflowStarted("inst-1", "order/v1", new string('a', 64)));

        // A crash mid-append leaves half a record with no terminating newline.
        string path = Path.Combine(dir, "history.log");
        byte[] partial = Encoding.UTF8.GetBytes("{\"v\":1,\"type\":\"ActivityScheduled\",\"inst");
        using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write))
            fs.Write(partial, 0, partial.Length);

        var result = new FileHistoryStore(dir).Load();
        Assert.True(result.TornTailTruncated);
        Assert.Single(result.Events);
        Assert.EndsWith("\n", File.ReadAllText(path)); // the file is whole again
    }

    [Fact]
    public void Load_TerminatedInvalidJson_IsCorruption_NotTorn()
    {
        string dir = FreshDir();
        LoadedStore(dir).Append(HistoryEvent.WorkflowStarted("inst-1", "order/v1", new string('a', 64)));
        File.AppendAllText(Path.Combine(dir, "history.log"), "{this is not valid json\n");

        var ex = Assert.Throws<HistoryCorruptionException>(() => new FileHistoryStore(dir).Load());
        Assert.Contains("not valid JSON", ex.Message);
    }

    [Fact]
    public void Load_TamperedContent_IsCaughtByChecksum()
    {
        string dir = FreshDir();
        var store = LoadedStore(dir);
        store.Append(HistoryEvent.WorkflowStarted("inst-1", "order/v1", new string('a', 64)));
        string path = Path.Combine(dir, "history.log");
        File.WriteAllText(path, File.ReadAllText(path).Replace("order/v1", "order/v9")); // still valid JSON

        var ex = Assert.Throws<HistoryCorruptionException>(() => new FileHistoryStore(dir).Load());
        Assert.Contains("checksum mismatch", ex.Message);
    }

    [Fact]
    public void Load_SequenceGap_IsCorruption()
    {
        string dir = FreshDir();
        string started = Frame(RawEvent("WorkflowStarted", f =>
        {
            f["def"] = "order/v1";
            f["inputHash"] = new string('a', 64);
        }), n: 1);
        string completed = Frame(RawEvent("WorkflowCompleted", _ => { }), n: 3); // n=2 was deleted
        WriteHistory(dir, started, completed);

        var ex = Assert.Throws<HistoryCorruptionException>(() => new FileHistoryStore(dir).Load());
        Assert.Contains("sequence gap", ex.Message);
    }

    [Fact]
    public void Load_DuplicateScheduled_IsCorruption()
    {
        string dir = FreshDir();
        JsonObject Scheduled() => RawEvent("ActivityScheduled", f =>
        {
            f["seq"] = 1;
            f["activity"] = "ChargeCard";
            f["idempotencyKey"] = new string('b', 64);
            f["inputHash"] = new string('c', 64);
        });
        string started = Frame(RawEvent("WorkflowStarted", f =>
        {
            f["def"] = "order/v1";
            f["inputHash"] = new string('a', 64);
        }), n: 1);
        WriteHistory(dir, started, Frame(Scheduled(), n: 2), Frame(Scheduled(), n: 3));

        var ex = Assert.Throws<HistoryCorruptionException>(() => new FileHistoryStore(dir).Load());
        Assert.Contains("duplicate ActivityScheduled", ex.Message);
    }

    [Fact]
    public void Load_CompletionWithoutSchedule_IsCorruption()
    {
        string dir = FreshDir();
        string started = Frame(RawEvent("WorkflowStarted", f =>
        {
            f["def"] = "order/v1";
            f["inputHash"] = new string('a', 64);
        }), n: 1);
        string completed = Frame(RawEvent("ActivityCompleted", f =>
        {
            f["seq"] = 1;
            f["activity"] = "ChargeCard";
            f["result"] = "txn-1";
        }), n: 2);
        WriteHistory(dir, started, completed);

        var ex = Assert.Throws<HistoryCorruptionException>(() => new FileHistoryStore(dir).Load());
        Assert.Contains("without ActivityScheduled", ex.Message);
    }

    [Fact]
    public void Load_EventAfterWorkflowCompleted_IsCorruption()
    {
        string dir = FreshDir();
        string started = Frame(RawEvent("WorkflowStarted", f =>
        {
            f["def"] = "order/v1";
            f["inputHash"] = new string('a', 64);
        }), n: 1);
        string wfCompleted = Frame(RawEvent("WorkflowCompleted", _ => { }), n: 2);
        string attempt = Frame(RawEvent("ActivityAttemptStarted", f =>
        {
            f["seq"] = 1;
            f["attempt"] = 1;
        }), n: 3);
        WriteHistory(dir, started, wfCompleted, attempt);

        var ex = Assert.Throws<HistoryCorruptionException>(() => new FileHistoryStore(dir).Load());
        Assert.Contains("after WorkflowCompleted", ex.Message);
    }
}
