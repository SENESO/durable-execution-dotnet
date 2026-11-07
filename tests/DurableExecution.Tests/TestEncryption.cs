using DurableExecution.History;

namespace DurableExecution.Tests;

/// <summary>
/// The fixed test keys for the payload-encryption coverage. These are
/// test vectors, not credentials: two distinct 32-byte keys so the
/// wrong-key refusals can be told from the right-key roundtrips, plus
/// their base64 forms for the environment-variable surface
/// (<c>DE_HISTORY_ENCRYPTION_KEY</c>) the scenario host reads.
/// </summary>
public static class TestEncryption
{
    private static byte[] KeyBytesA => Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray();

    private static byte[] KeyBytesB => Enumerable.Range(0, 32).Select(i => (byte)(0xA0 + i)).ToArray();

    public static readonly HistoryEncryption KeyA = new(KeyBytesA);

    public static readonly HistoryEncryption KeyB = new(KeyBytesB);

    public static string KeyABase64 => Convert.ToBase64String(KeyBytesA);

    public static string KeyBBase64 => Convert.ToBase64String(KeyBytesB);
}
