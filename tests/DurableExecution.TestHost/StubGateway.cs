using System.Text.Json;
using DurableExecution.Execution;
using DurableExecution.IO;

namespace DurableExecution.TestHost;

public sealed record ChargeRecord(string Ref, string Key, string Txn, int Amount);

public sealed record RefundRecord(string Ref, string Key, string Txn, string ForTxn, int Amount);

public sealed class GatewayState
{
    public List<ChargeRecord> Charges { get; set; } = new();
    public List<RefundRecord> Refunds { get; set; } = new();
}

/// <summary>Raised when an idempotency key is presented with a different amount or reference than it first carried.</summary>
public sealed class IdempotencyKeyConflictException : Exception
{
    public IdempotencyKeyConflictException(string message) : base(message) { }
}

/// <summary>
/// Scriptable payment-gateway stub (foundation §7.1) in the two modes the
/// guarantee contract is tested against:
///
/// - <b>idempotent</b>: a charge presented with an already-seen key returns the
///   original transaction without charging again; the same key with a
///   different amount/reference is a detected conflict, refused loudly.
/// - <b>nonidempotent</b>: the key is ignored and every call charges — the
///   duplicate genuinely happens, and reconciliation (a real refund through
///   this same gateway, under its own key) is the compensation.
///
/// Every call is logged (flushed to disk) <i>before</i> it executes, so an
/// attempt is on record even if the host dies mid-charge. Ledger state is
/// written atomically (temp + fsync + rename).
/// </summary>
public sealed class StubGateway
{
    private readonly string _statePath;
    private readonly string _callsPath;
    private readonly string _mode;
    private readonly GatewayState _state;
    private readonly Counters _counters;

    private StubGateway(string stateDir, string mode, GatewayState state, Counters counters)
    {
        _statePath = Path.Combine(stateDir, "gateway-state.json");
        _callsPath = Path.Combine(stateDir, "gateway-calls.log");
        _mode = mode;
        _state = state;
        _counters = counters;
    }

    public static StubGateway Load(string stateDir, string mode, Counters counters)
    {
        string path = Path.Combine(stateDir, "gateway-state.json");
        GatewayState state;
        if (!File.Exists(path))
        {
            state = new GatewayState();
        }
        else
        {
            try
            {
                state = JsonSerializer.Deserialize<GatewayState>(File.ReadAllText(path))
                        ?? throw new InvalidOperationException("null");
            }
            catch (Exception e)
            {
                throw new AuxStateException($"gateway-state.json unreadable ({e.Message})");
            }
        }
        return new StubGateway(stateDir, mode, state, counters);
    }

    public GatewayState State => _state;

    public bool IsIdempotent => _mode != "nonidempotent";

    public string Charge(string idempotencyKey, int amount, string merchantRef)
    {
        LogCall(idempotencyKey, merchantRef, "call"); // the attempt is on record FIRST
        if (!IsIdempotent)
        {
            string ignoredTxn = NewTxn("txn");
            _state.Charges.Add(new ChargeRecord(merchantRef, idempotencyKey, ignoredTxn, amount));
            Save();
            _counters.Bump("chargesApplied");
            LogCall(idempotencyKey, merchantRef, "charged-ignoring-key");
            Console.WriteLine($"[gateway:nonidempotent] CHARGED {amount} EGP (idempotency key IGNORED), {ignoredTxn}");
            return ignoredTxn;
        }

        var existing = _state.Charges.FirstOrDefault(c => c.Key == idempotencyKey);
        if (existing is not null)
        {
            if (existing.Amount != amount || existing.Ref != merchantRef)
                throw new IdempotencyKeyConflictException(
                    $"key was used for ({existing.Ref}, {existing.Amount}) but is now presented for ({merchantRef}, {amount})");
            LogCall(idempotencyKey, merchantRef, "duplicate-key");
            Console.WriteLine($"[gateway] key already seen -> NO new charge (returns {existing.Txn})");
            return existing.Txn;
        }

        string newTxn = NewTxn("txn");
        _state.Charges.Add(new ChargeRecord(merchantRef, idempotencyKey, newTxn, amount));
        Save();
        _counters.Bump("chargesApplied");
        LogCall(idempotencyKey, merchantRef, "charged");
        Console.WriteLine($"[gateway] CHARGED {amount} EGP, key={idempotencyKey[..16]}..., {newTxn}");
        return newTxn;
    }

    public string Refund(string forTxn, int amount, string refundKey, string merchantRef)
    {
        LogCall(refundKey, merchantRef, "refund-call");
        var existing = _state.Refunds.FirstOrDefault(r => r.Key == refundKey);
        if (existing is not null)
        {
            LogCall(refundKey, merchantRef, "refund-duplicate");
            Console.WriteLine($"[gateway] refund key already seen -> NO second refund (returns {existing.Txn})");
            return existing.Txn;
        }
        string txn = NewTxn("rfnd");
        _state.Refunds.Add(new RefundRecord(merchantRef, refundKey, txn, forTxn, amount));
        Save();
        LogCall(refundKey, merchantRef, "refunded");
        Console.WriteLine($"[gateway] REFUNDED {amount} EGP for {forTxn}, refund txn {txn}");
        return txn;
    }

    /// <summary>
    /// The refund key for compensating a charge: derived from the charge's own
    /// identity, so re-running reconciliation derives the same key and the
    /// gateway deduplicates the refund.
    /// </summary>
    public static string RefundKeyFor(string merchantRef, string chargeTxn) =>
        Fingerprints.Sha256Hex($"refund|{merchantRef}|{chargeTxn}");

    private void Save() => AtomicFile.WriteAllText(_statePath, JsonSerializer.Serialize(_state));

    private void LogCall(string key, string merchantRef, string phase)
    {
        string line = JsonSerializer.Serialize(new { key, merchantRef, mode = _mode, phase }) + "\n";
        using var fs = new FileStream(_callsPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(line);
        fs.Write(bytes, 0, bytes.Length);
        fs.Flush(true);
    }

    private static string NewTxn(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];
}
