using DurableExecution.Execution;

namespace DurableExecution.Tests.Unit;

/// <summary>
/// ADR-012 identity rules: the key is a pure function of (namespace, instance,
/// command sequence, command identity) — stable across hosts and attempts,
/// distinct per logical invocation.
/// </summary>
public class KeyAndFingerprintTests
{
    [Fact]
    public void IdempotencyKey_IsStableAcrossCalls_AndFullSha256Hex()
    {
        string a = Fingerprints.DeriveIdempotencyKey("default", "order-1001", 1, "ChargeCard");
        string b = Fingerprints.DeriveIdempotencyKey("default", "order-1001", 1, "ChargeCard");
        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
        Assert.True(a.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'));
    }

    [Fact]
    public void IdempotencyKey_DiffersPerLogicalInvocation()
    {
        string first = Fingerprints.DeriveIdempotencyKey("default", "order-1001", 1, "ProcessItem");
        string second = Fingerprints.DeriveIdempotencyKey("default", "order-1001", 2, "ProcessItem");
        Assert.NotEqual(first, second); // same activity in a loop: distinct key per iteration
    }

    [Fact]
    public void IdempotencyKey_DiffersAcrossActivityInstanceAndNamespace()
    {
        string baseline = Fingerprints.DeriveIdempotencyKey("default", "order-1001", 1, "ChargeCard");
        Assert.NotEqual(baseline, Fingerprints.DeriveIdempotencyKey("default", "order-1001", 1, "ReserveStock"));
        Assert.NotEqual(baseline, Fingerprints.DeriveIdempotencyKey("default", "order-2002", 1, "ChargeCard"));
        Assert.NotEqual(baseline, Fingerprints.DeriveIdempotencyKey("other", "order-1001", 1, "ChargeCard"));
    }

    [Fact]
    public void InputFingerprint_ChangesWithTheInput_AndIsStableForEqualInputs()
    {
        Assert.Equal(
            Fingerprints.InputFingerprint(new { amount = 1000 }),
            Fingerprints.InputFingerprint(new { amount = 1000 }));
        Assert.NotEqual(
            Fingerprints.InputFingerprint(new { amount = 1000 }),
            Fingerprints.InputFingerprint(new { amount = 5000 }));
    }
}
