using System.Text.Json;
using DurableExecution.IO;

namespace DurableExecution.TestHost;

/// <summary>Raised when auxiliary harness state (counters, gateway ledger) is unreadable.</summary>
public sealed class AuxStateException : Exception
{
    public AuxStateException(string message) : base(message) { }
}

/// <summary>
/// Execution counters, persisted atomically after every change so a killed
/// host leaves exact counts behind. A missing counter key is a test failure,
/// never a silent zero — the assertions read this file strictly.
/// </summary>
public sealed class Counters
{
    private readonly string _path;
    private readonly Dictionary<string, int> _values;

    private Counters(string path, Dictionary<string, int> values)
    {
        _path = path;
        _values = values;
    }

    public static Counters Load(string stateDir)
    {
        string path = Path.Combine(stateDir, "counters.json");
        if (!File.Exists(path))
            return new Counters(path, new Dictionary<string, int>());
        try
        {
            return new Counters(path,
                JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path))
                ?? throw new InvalidOperationException("null"));
        }
        catch (Exception e)
        {
            throw new AuxStateException($"counters.json unreadable ({e.Message})");
        }
    }

    public void Bump(string name)
    {
        _values[name] = _values.GetValueOrDefault(name) + 1;
        Save();
    }

    public void Set(string name, int value)
    {
        _values[name] = value;
        Save();
    }

    public int Get(string name) => _values.GetValueOrDefault(name);

    private void Save() => AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_values));
}

/// <summary>
/// A string-keyed effect store for the loop workflow: effects are addressed by
/// the command's idempotency key, so a re-executed activity observes its own
/// prior effect instead of duplicating it (an idempotency-protected effect).
/// </summary>
public sealed class ItemsStore
{
    private readonly string _path;
    private readonly Dictionary<string, string> _items;

    private ItemsStore(string path, Dictionary<string, string> items)
    {
        _path = path;
        _items = items;
    }

    public static ItemsStore Load(string stateDir)
    {
        string path = Path.Combine(stateDir, "items-store.json");
        if (!File.Exists(path))
            return new ItemsStore(path, new Dictionary<string, string>());
        try
        {
            return new ItemsStore(path,
                JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                ?? throw new InvalidOperationException("null"));
        }
        catch (Exception e)
        {
            throw new AuxStateException($"items-store.json unreadable ({e.Message})");
        }
    }

    public IReadOnlyDictionary<string, string> Items => _items;

    public bool TryGet(string key, out string value) => _items.TryGetValue(key, out value!);

    public void Apply(string key, string item)
    {
        _items[key] = item;
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_items));
    }
}
