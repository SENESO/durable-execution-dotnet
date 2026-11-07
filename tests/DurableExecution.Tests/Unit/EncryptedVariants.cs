namespace DurableExecution.Tests.Unit;

/// <summary>
/// The host-semantics suites — host, fan-out, retries, timers, signals —
/// run a second time with payload encryption on (§6.5 with encryption as
/// a provider dimension): the identical bodies, against stores whose
/// records are AES-256-GCM envelopes at rest. A green derived class
/// claims the suite's behaviour over encrypted storage for that provider,
/// exactly as the plaintext class claims it over plaintext storage. The
/// hand-crafted corruption conformance tests are not re-derived: their
/// damage is plaintext-shaped by construction, and the encrypted damage
/// model (ciphertext flips, wrong keys, cross-mode opens) is covered by
/// <see cref="EncryptionTests"/> per provider instead.
/// </summary>
[Trait("Provider", "File")]
public sealed class HostInProcessTestsFileEncrypted() : HostInProcessTests(new FileStoreFactory(TestEncryption.KeyA));

[Trait("Provider", "SqlServer")]
public sealed class HostInProcessTestsSqlServerEncrypted() : HostInProcessTests(new SqlStoreFactory(TestEncryption.KeyA));

[Trait("Provider", "PostgreSql")]
public sealed class HostInProcessTestsPostgreSqlEncrypted() : HostInProcessTests(new PostgresStoreFactory(TestEncryption.KeyA));

[Trait("Provider", "Sqlite")]
public sealed class HostInProcessTestsSqliteEncrypted() : HostInProcessTests(new SqliteStoreFactory(TestEncryption.KeyA));

[Trait("Provider", "File")]
public sealed class FanOutTestsFileEncrypted() : FanOutTests(new FileStoreFactory(TestEncryption.KeyA));

[Trait("Provider", "SqlServer")]
public sealed class FanOutTestsSqlServerEncrypted() : FanOutTests(new SqlStoreFactory(TestEncryption.KeyA));

[Trait("Provider", "PostgreSql")]
public sealed class FanOutTestsPostgreSqlEncrypted() : FanOutTests(new PostgresStoreFactory(TestEncryption.KeyA));

[Trait("Provider", "Sqlite")]
public sealed class FanOutTestsSqliteEncrypted() : FanOutTests(new SqliteStoreFactory(TestEncryption.KeyA));

[Trait("Provider", "File")]
public sealed class RetryTestsFileEncrypted() : RetryTests(new FileStoreFactory(TestEncryption.KeyA));

[Trait("Provider", "SqlServer")]
public sealed class RetryTestsSqlServerEncrypted() : RetryTests(new SqlStoreFactory(TestEncryption.KeyA));

[Trait("Provider", "PostgreSql")]
public sealed class RetryTestsPostgreSqlEncrypted() : RetryTests(new PostgresStoreFactory(TestEncryption.KeyA));

[Trait("Provider", "Sqlite")]
public sealed class RetryTestsSqliteEncrypted() : RetryTests(new SqliteStoreFactory(TestEncryption.KeyA));

[Trait("Provider", "File")]
public sealed class TimerTestsFileEncrypted() : TimerTests(new FileStoreFactory(TestEncryption.KeyA));

[Trait("Provider", "SqlServer")]
public sealed class TimerTestsSqlServerEncrypted() : TimerTests(new SqlStoreFactory(TestEncryption.KeyA));

[Trait("Provider", "PostgreSql")]
public sealed class TimerTestsPostgreSqlEncrypted() : TimerTests(new PostgresStoreFactory(TestEncryption.KeyA));

[Trait("Provider", "Sqlite")]
public sealed class TimerTestsSqliteEncrypted() : TimerTests(new SqliteStoreFactory(TestEncryption.KeyA));

[Trait("Provider", "File")]
public sealed class SignalTestsFileEncrypted() : SignalTests(new FileStoreFactory(TestEncryption.KeyA));

[Trait("Provider", "SqlServer")]
public sealed class SignalTestsSqlServerEncrypted() : SignalTests(new SqlStoreFactory(TestEncryption.KeyA));

[Trait("Provider", "PostgreSql")]
public sealed class SignalTestsPostgreSqlEncrypted() : SignalTests(new PostgresStoreFactory(TestEncryption.KeyA));

[Trait("Provider", "Sqlite")]
public sealed class SignalTestsSqliteEncrypted() : SignalTests(new SqliteStoreFactory(TestEncryption.KeyA));
