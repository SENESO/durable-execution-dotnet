using System.Text.Json;

namespace DurableExecution.Tests;

/// <summary>
/// Strict assertions over a scenario's on-disk state. Strict by design: a
/// missing file, a missing field, or a non-integer counter is a failure —
/// never a silent default. These are the data checks of the evidence suite;
/// the process-level checks (exit codes, kill markers) sit next to them in
/// the scenario classes.
/// </summary>
public static class StateAssert
{
    private static JsonElement LoadJson(string stateDir, string file, string label)
    {
        string path = Path.Combine(stateDir, file);
        Assert.True(File.Exists(path), $"{label}: file exists ({path} missing)");
        try
        {
            return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
        }
        catch (Exception e)
        {
            Assert.Fail($"{label}: valid JSON ({e.Message})");
            throw;
        }
    }

    private static List<JsonElement> LoadJsonLines(string stateDir, string file, string label)
    {
        string path = Path.Combine(stateDir, file);
        Assert.True(File.Exists(path), $"{label}: file exists ({path} missing)");
        var rows = new List<JsonElement>();
        int i = 0;
        foreach (string line in File.ReadLines(path))
        {
            i++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                rows.Add(JsonDocument.Parse(line).RootElement.Clone());
            }
            catch (Exception e)
            {
                Assert.Fail($"{label}: line {i} valid JSON ({e.Message})");
            }
        }
        return rows;
    }

    public static Dictionary<string, int> Counters(string stateDir)
    {
        var root = LoadJson(stateDir, "counters.json", "counters");
        var result = new Dictionary<string, int>();
        foreach (var property in root.EnumerateObject())
        {
            Assert.True(property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out _),
                $"counters.{property.Name} is an integer");
            result[property.Name] = property.Value.GetInt32();
        }
        return result;
    }

    /// <summary>Asserts a counter exists with exactly this value (a missing key fails).</summary>
    public static void Need(Dictionary<string, int> counters, string key, int expected)
    {
        Assert.True(counters.TryGetValue(key, out int actual),
            $"counters.{key} == {expected} (field '{key}' missing — no silent zero)");
        Assert.True(actual == expected, $"counters.{key} == {expected} (got {actual})");
    }

    public static List<JsonElement> History(string stateDir) =>
        LoadJsonLines(stateDir, "history.log", "history");

    public static List<JsonElement> Events(string stateDir, string type, long? seq = null, string? activity = null)
    {
        var rows = History(stateDir).Where(e => e.GetProperty("type").GetString() == type);
        if (seq is not null)
            rows = rows.Where(e => e.TryGetProperty("seq", out var s) && s.GetInt64() == seq.Value);
        if (activity is not null)
            rows = rows.Where(e => e.TryGetProperty("activity", out var a) && a.GetString() == activity);
        return rows.ToList();
    }

    public static void WorkflowCompletedExactlyOnce(string stateDir) =>
        Assert.True(Events(stateDir, "WorkflowCompleted").Count == 1,
            "workflow completed exactly once");

    /// <summary>The execution counter of an activity equals its recorded attempts — counters are cross-checked against history, not trusted.</summary>
    public static void Consistency(string stateDir, string activity, string counterKey)
    {
        var counters = Counters(stateDir);
        var seqs = Events(stateDir, "ActivityScheduled", activity: activity)
            .Select(e => e.GetProperty("seq").GetInt64()).ToHashSet();
        int attempts = Events(stateDir, "ActivityAttemptStarted")
            .Count(e => seqs.Contains(e.GetProperty("seq").GetInt64()));
        Assert.True(counters.TryGetValue(counterKey, out int recorded),
            $"consistency: counters.{counterKey} present");
        Assert.True(recorded == attempts,
            $"consistency: counters.{counterKey} == recorded attempts of {activity} ({recorded} vs {attempts})");
    }

    public static List<JsonElement> GatewayCalls(string stateDir, string? phase = null)
    {
        var rows = LoadJsonLines(stateDir, "gateway-calls.log", "gateway-calls");
        return phase is null ? rows : rows.Where(r => r.GetProperty("phase").GetString() == phase).ToList();
    }

    /// <summary>Both charge attempts carried the same key, and the charge outcomes are exactly these, in order.</summary>
    public static void ChargeKeyChecks(string stateDir, params string[] expectedOutcomes)
    {
        var calls = GatewayCalls(stateDir, phase: "call");
        Assert.True(calls.Count == 2, $"gateway received exactly 2 charge calls (got {calls.Count})");
        Assert.True(calls[0].GetProperty("key").GetString() == calls[1].GetProperty("key").GetString(),
            "gateway received the SAME key on both attempts");
        var outcomes = GatewayCalls(stateDir)
            .Select(r => r.GetProperty("phase").GetString())
            .Where(p => p is "charged" or "duplicate-key" or "charged-ignoring-key")
            .ToArray();
        Assert.True(outcomes.SequenceEqual(expectedOutcomes),
            $"charge outcomes == [{string.Join(", ", expectedOutcomes)}] (got [{string.Join(", ", outcomes)}])");
    }

    public static JsonElement GatewayState(string stateDir) =>
        LoadJson(stateDir, "gateway-state.json", "gateway-state");

    public static JsonElement Reconciliation(string stateDir) =>
        LoadJson(stateDir, "reconciliation.json", "reconciliation");

    public static Dictionary<string, string> ItemsStore(string stateDir)
    {
        var root = LoadJson(stateDir, "items-store.json", "items-store");
        return root.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
    }
}
