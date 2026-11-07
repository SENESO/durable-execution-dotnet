using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace DurableExecution.History;

/// <summary>
/// Payload encryption at rest for history records (foundation §6.1
/// payload separation, ADR-014/017): AES-256-GCM envelope encryption,
/// opt-in per store and off by default — a store constructed without a
/// cipher frames plaintext records exactly as before, and existing
/// histories keep loading.
///
/// <b>Envelope.</b> When a store has a cipher, the record it persists is
/// not the sealed JSON record but an envelope around it:
/// <c>{"v":1,"enc":"aes-256-gcm","kid":…,"nonce":…,"ct":…,"sum":…}</c>.
/// The plaintext of the encryption is the complete inner record — every
/// field, including its own <c>sum</c> — so the record model is untouched:
/// decryption restores the exact sealed record, and the shared structural
/// validator then verifies it under exactly the rules a plaintext history
/// is verified under.
///
/// <b>Sealing stays coherent.</b> The envelope carries its own seal: the
/// same SHA-256 checksum discipline, computed over the envelope's
/// canonical content — that is, <i>over the ciphertext</i>. Casual damage
/// (bit rot, a truncated copy) is therefore caught by the seal with the
/// same words as a plaintext record, before decryption is attempted; and
/// because the seal is a checksum rather than a MAC, deliberate tampering
/// that recomputes it is caught one layer down by the GCM authentication
/// tag. The key id (<c>kid</c>, the first 16 hex characters of the key's
/// SHA-256) and a fresh random 96-bit nonce ride in the frame, as the
/// foundation's envelope shape requires.
///
/// <b>No silent fallback, in either direction.</b> A store with a cipher
/// reads only envelopes — a plaintext record under it is refused
/// (<see cref="HistoryCorruptionException"/>), so enabling encryption
/// over an existing plaintext history is a refusal with a clear error,
/// never a silent migration and never a mixed history. A store without a
/// cipher reads only plaintext — an envelope under it is refused, never
/// decrypted opportunistically and never fed to the validator as if it
/// were a record. A record sealed under a different key is refused by
/// key id; a record whose ciphertext fails GCM authentication is refused
/// as tampered.
///
/// <b>Key supply.</b> The key reaches a store already constructed; the
/// deployment surface is the environment variable
/// <c>DE_HISTORY_ENCRYPTION_KEY</c> (base64 of the 32 key bytes), read by
/// <see cref="FromEnvironment"/>. A variable that is set but malformed is
/// a loud startup refusal — a store never falls back to plaintext because
/// its key failed to parse.
/// </summary>
public sealed class HistoryEncryption
{
    /// <summary>The environment variable that carries the base64-encoded 32-byte key.</summary>
    public const string KeyEnvironmentVariable = "DE_HISTORY_ENCRYPTION_KEY";

    private const string AlgorithmId = "aes-256-gcm";
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    /// <summary>Domain separation for the GCM additional authenticated data: ciphertext is bound to this record format, not portable to another context.</summary>
    private static readonly byte[] AssociatedData =
        Encoding.UTF8.GetBytes("DurableExecution.History.Record.v1");

    private readonly byte[] _key;

    public HistoryEncryption(byte[] key)
    {
        if (key is null || key.Length != 32)
            throw new ArgumentException("history encryption key must be exactly 32 bytes (AES-256)", nameof(key));
        _key = (byte[])key.Clone();
        KeyId = Convert.ToHexString(SHA256.HashData(_key)).ToLowerInvariant()[..16];
    }

    /// <summary>The key's public identifier: the first 16 hex characters of its SHA-256. Recorded in every envelope so a wrong key is refused by id, before decryption.</summary>
    public string KeyId { get; }

    /// <summary>Builds a cipher from a base64-encoded 32-byte key.</summary>
    /// <exception cref="InvalidOperationException">The value is not base64 or does not decode to 32 bytes.</exception>
    public static HistoryEncryption FromKeyBase64(string base64)
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(base64.Trim());
        }
        catch (FormatException e)
        {
            throw new InvalidOperationException("history encryption key is not valid base64", e);
        }
        if (key.Length != 32)
            throw new InvalidOperationException(
                $"history encryption key must decode to 32 bytes (AES-256); it decoded to {key.Length}");
        return new HistoryEncryption(key);
    }

    /// <summary>
    /// Reads the cipher from <see cref="KeyEnvironmentVariable"/>: null
    /// when the variable is unset (encryption off), a cipher when it
    /// names a valid key, and a loud <see cref="InvalidOperationException"/>
    /// when it is set but malformed — never a silent plaintext fallback.
    /// </summary>
    public static HistoryEncryption? FromEnvironment()
    {
        string? value = Environment.GetEnvironmentVariable(KeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
            return null;
        try
        {
            return FromKeyBase64(value);
        }
        catch (InvalidOperationException e)
        {
            throw new InvalidOperationException(
                $"{KeyEnvironmentVariable} is set but unusable: {e.Message} — refusing to start rather than fall back to plaintext history", e);
        }
    }

    /// <summary>True when the stored text is an encryption envelope rather than a plaintext record.</summary>
    public static bool IsEnvelope(string recordText)
    {
        if (!recordText.StartsWith('{'))
            return false;
        try
        {
            var node = JsonNode.Parse(recordText);
            return node is JsonObject obj && obj.ContainsKey("enc");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The read path of a store without a cipher: returns the record
    /// unchanged when it is plaintext, and refuses it when it is an
    /// envelope — ciphertext is never fed to the validator as a record.
    /// </summary>
    public static string EnsurePlaintext(string recordText)
    {
        if (IsEnvelope(recordText))
            throw new HistoryCorruptionException(
                $"record: history is encrypted (AES-256-GCM) but this store has no encryption key configured " +
                $"({KeyEnvironmentVariable}) — refusing to read ciphertext without its key");
        return recordText;
    }

    /// <summary>Encrypts one sealed record (or one sealed snapshot payload) into its envelope form.</summary>
    public string Seal(string plaintext)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        byte[] plainBytes = Encoding.UTF8.GetBytes(plaintext);
        byte[] cipherBytes = new byte[plainBytes.Length];
        byte[] tag = new byte[TagBytes];
        using (var gcm = new AesGcm(_key, TagBytes))
            gcm.Encrypt(nonce, plainBytes, cipherBytes, tag, AssociatedData);

        byte[] ct = new byte[cipherBytes.Length + TagBytes];
        cipherBytes.CopyTo(ct, 0);
        tag.CopyTo(ct, cipherBytes.Length);

        var envelope = new JsonObject
        {
            ["v"] = 1,
            ["enc"] = AlgorithmId,
            ["kid"] = KeyId,
            ["nonce"] = Convert.ToBase64String(nonce),
            ["ct"] = Convert.ToBase64String(ct),
        };
        envelope["sum"] = HistorySealing.Checksum(envelope);
        return envelope.ToJsonString();
    }

    /// <summary>
    /// Decrypts one envelope back into the sealed record it carries.
    /// Every failure — malformed envelope, wrong key, seal mismatch,
    /// GCM authentication failure — is a
    /// <see cref="HistoryCorruptionException"/>: from the store's side,
    /// an unreadable record is refused, never skipped or repaired.
    /// </summary>
    public string Open(string recordText)
    {
        JsonObject envelope;
        try
        {
            envelope = JsonNode.Parse(recordText)?.AsObject()
                ?? throw new HistoryCorruptionException("record: encrypted record is not a JSON object");
        }
        catch (HistoryCorruptionException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new HistoryCorruptionException("record: encrypted record is not valid JSON");
        }

        if (!envelope.TryGetPropertyValue("enc", out var encNode) || encNode is null)
            throw new HistoryCorruptionException(
                "record: history record is not encrypted, but this store has payload encryption enabled — " +
                "refusing to read a plaintext record (enabling encryption over a plaintext history is refused, not migrated)");
        if (encNode.GetValue<string>() != AlgorithmId)
            throw new HistoryCorruptionException(
                $"record: unsupported payload encryption '{encNode.GetValue<string>()}'");
        if (!envelope.TryGetPropertyValue("kid", out var kidNode) || kidNode is null ||
            kidNode.GetValue<string>() != KeyId)
            throw new HistoryCorruptionException(
                $"record: encryption key id mismatch (record was sealed under key " +
                $"'{kidNode?.GetValue<string>()}', this store's key is '{KeyId}') — wrong key, or a history written under another key");
        if (!envelope.TryGetPropertyValue("sum", out var sumNode) || sumNode is null ||
            sumNode.GetValue<string>() != HistorySealing.Checksum(envelope))
            throw new HistoryCorruptionException(
                "record: checksum mismatch (content tampered or partially written)");

        byte[] nonce, ct;
        try
        {
            nonce = Convert.FromBase64String(ReqString(envelope, "nonce"));
            ct = Convert.FromBase64String(ReqString(envelope, "ct"));
        }
        catch (FormatException)
        {
            throw new HistoryCorruptionException("record: envelope nonce/ciphertext is not valid base64");
        }
        if (nonce.Length != NonceBytes || ct.Length < TagBytes)
            throw new HistoryCorruptionException("record: envelope nonce/ciphertext has an invalid length");

        byte[] cipherBytes = ct[..^TagBytes];
        byte[] tag = ct[^TagBytes..];
        byte[] plainBytes = new byte[cipherBytes.Length];
        try
        {
            using var gcm = new AesGcm(_key, TagBytes);
            gcm.Decrypt(nonce, cipherBytes, tag, plainBytes, AssociatedData);
        }
        catch (CryptographicException)
        {
            throw new HistoryCorruptionException(
                "record: payload failed AES-256-GCM authentication — ciphertext tampered, or the key is wrong");
        }
        return Encoding.UTF8.GetString(plainBytes);
    }

    private static string ReqString(JsonObject envelope, string field)
    {
        if (!envelope.TryGetPropertyValue(field, out var node) || node is null ||
            node.GetValueKind() != System.Text.Json.JsonValueKind.String)
            throw new HistoryCorruptionException($"record: envelope field '{field}' missing or not a string");
        return node.GetValue<string>();
    }
}
