namespace DurableExecution.Tests.Scenarios;

/// <summary>
/// Payload encryption across real process boundaries: the scenario host
/// reads its key from <c>DE_HISTORY_ENCRYPTION_KEY</c> (the deployment
/// surface) and writes an encrypted history through the default workflow —
/// a clean run, the §8.1 kill inside the charge window with a resume, and
/// the two cross-mode refusals at process level: a resume without the
/// key, and a resume under a different key, both refused as corrupt
/// history (exit 3) rather than opened. Written once, run per provider.
/// </summary>
public abstract class ScenarioEncryptionTests(ScenarioProvider provider) : IDisposable
{
    protected ScenarioProvider Provider { get; } = provider;

    public void Dispose() => Provider.Dispose();

    private static readonly IReadOnlyDictionary<string, string> KeyAEnv =
        new Dictionary<string, string> { ["DE_HISTORY_ENCRYPTION_KEY"] = TestEncryption.KeyABase64 };

    private static readonly IReadOnlyDictionary<string, string> KeyBEnv =
        new Dictionary<string, string> { ["DE_HISTORY_ENCRYPTION_KEY"] = TestEncryption.KeyBBase64 };

    [Fact]
    public void Encrypted_CleanRun_Completes_AndHistoryAtRestIsCiphertext()
    {
        string state = Provider.FreshState("ENC-C");

        var run1 = Provider.Run(state, extraEnv: KeyAEnv);
        Assert.Equal(0, run1.ExitCode);

        var run2 = Provider.Run(state, extraEnv: KeyAEnv);
        Assert.Equal(0, run2.ExitCode);
        Assert.Contains("already completed", run2.Output);

        string raw = Provider.HistoryText(state);
        Assert.Contains("\"enc\":\"aes-256-gcm\"", raw);
        Assert.DoesNotContain("ChargeCard", raw);
        Assert.DoesNotContain("ReserveStock", raw);

        var counters = StateAssert.Counters(state);
        StateAssert.Need(counters, "chargesApplied", 1);
    }

    [Fact]
    public void Encrypted_KillInsideChargeWindow_ResumeCompletes_SingleCharge()
    {
        string state = Provider.FreshState("ENC-B");

        var run1 = Provider.Run(state, crashMode: "in-charge-window", extraEnv: KeyAEnv);
        Assert.True(run1.WasKilled, $"run1 killed by the crash point (exit {run1.ExitCode})");

        var run2 = Provider.Run(state, extraEnv: KeyAEnv);
        Assert.Equal(0, run2.ExitCode);

        var counters = StateAssert.Counters(state);
        StateAssert.Need(counters, "chargeExecutions", 2); // at-least-once under encryption too
        StateAssert.Need(counters, "chargesApplied", 1);   // the gateway still deduplicates by the recorded key
    }

    [Fact]
    public void Encrypted_ResumeWithoutKey_IsRefusedAsCorrupt()
    {
        string state = Provider.FreshState("ENC-NK");

        var run1 = Provider.Run(state, extraEnv: KeyAEnv);
        Assert.Equal(0, run1.ExitCode);

        var run2 = Provider.Run(state);
        Assert.Equal(3, run2.ExitCode);
        Assert.Contains("CORRUPT HISTORY", run2.Output);
        Assert.Contains("no encryption key configured", run2.Output);
    }

    [Fact]
    public void Encrypted_ResumeWithWrongKey_IsRefusedAsCorrupt()
    {
        string state = Provider.FreshState("ENC-WK");

        var run1 = Provider.Run(state, extraEnv: KeyAEnv);
        Assert.Equal(0, run1.ExitCode);

        var run2 = Provider.Run(state, extraEnv: KeyBEnv);
        Assert.Equal(3, run2.ExitCode);
        Assert.Contains("CORRUPT HISTORY", run2.Output);
        Assert.Contains("key id mismatch", run2.Output);
    }
}

/// <summary>Encryption scenarios against the file provider.</summary>
[Trait("Provider", "File")]
public sealed class ScenarioEncryptionTestsFile() : ScenarioEncryptionTests(new FileScenarioProvider());

/// <summary>Encryption scenarios against the SQL Server provider.</summary>
[Trait("Provider", "SqlServer")]
public sealed class ScenarioEncryptionTestsSqlServer() : ScenarioEncryptionTests(new SqlScenarioProvider());

/// <summary>Encryption scenarios against the PostgreSQL provider.</summary>
[Trait("Provider", "PostgreSql")]
public sealed class ScenarioEncryptionTestsPostgreSql() : ScenarioEncryptionTests(new PostgresScenarioProvider());

/// <summary>Encryption scenarios against the SQLite provider.</summary>
[Trait("Provider", "Sqlite")]
public sealed class ScenarioEncryptionTestsSqlite() : ScenarioEncryptionTests(new SqliteScenarioProvider());
