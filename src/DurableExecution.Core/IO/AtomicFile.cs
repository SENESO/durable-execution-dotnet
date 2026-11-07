using System.Text;

namespace DurableExecution.IO;

/// <summary>
/// Atomic durable file replacement: write to a temporary file in the same
/// directory, flush to disk, then rename over the target. A crash leaves either
/// the old content or the new content, never a torn mixture. Used for auxiliary
/// state (counters, gateway ledgers, effect stores) that must not corrupt when
/// the host dies mid-write. Note the honest limit: this proves behaviour under
/// process crashes; directory-entry durability under real power loss is a
/// filesystem/hardware property and is not claimed here.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string content)
    {
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(true);
        }
        File.Move(tmp, path, overwrite: true);
    }
}
