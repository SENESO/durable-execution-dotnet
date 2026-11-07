using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DurableExecution.Execution;

/// <summary>
/// Hashing primitives behind command identity (foundation §2.1 item 4, ADR-012).
/// Everything here is a pure function of its inputs: the same logical command
/// produces the same fingerprint and the same idempotency key on every host,
/// on every attempt, forever.
/// </summary>
public static class Fingerprints
{
    /// <summary>Lowercase hex SHA-256 of a UTF-8 string.</summary>
    public static string Sha256Hex(string text)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    /// <summary>
    /// Fingerprint of a command's (or a workflow's) inputs: a hash of the
    /// serialized input value. Recorded next to the command when it is first
    /// scheduled and re-checked on every replay, so a step whose inputs changed
    /// under an existing history is detected instead of silently reused.
    /// </summary>
    public static string InputFingerprint(object? input) =>
        Sha256Hex(JsonSerializer.Serialize(input));

    /// <summary>
    /// The stable idempotency key of one logical command (ADR-012):
    /// <c>StableHash(Namespace | WorkflowInstance | CommandSequence | CommandIdentity)</c>.
    /// Attempt count is deliberately not an input — attempt 1 and attempt 9 of
    /// the same command carry the same key. A loop calling the same activity
    /// gets a distinct key per iteration because the sequence differs.
    /// </summary>
    public static string DeriveIdempotencyKey(string namespaceId, string instanceId, long commandSequence, string commandIdentity) =>
        Sha256Hex($"{namespaceId}|{instanceId}|{commandSequence}|{commandIdentity}");

    internal static bool IsHex64(string? s) =>
        s is { Length: 64 } && s.All(Uri.IsHexDigit);
}
