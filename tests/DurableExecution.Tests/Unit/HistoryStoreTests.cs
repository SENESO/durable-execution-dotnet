using System.Text;
using DurableExecution.History;

namespace DurableExecution.Tests.Unit;

/// <summary>
/// The file provider's framing rules that have no counterpart in a
/// transactional medium: a record counts as committed only if it is
/// newline-terminated, so an unterminated tail is a torn write — truncated
/// and reported on load, never committed, never mistaken for corruption.
/// (The portable store contract — append/load semantics and the corruption
/// rules — is the §6.5 conformance suite in
/// <see cref="HistoryStoreConformanceTests"/>, run per provider.)
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
}
