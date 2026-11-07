# STATUS — P0 foundation stage

Recorded 2026-11-07. This note maps the Engineering Foundation (v0.2) to what
this repository implements and proves today, and names everything it does
not. The evidence rule (§7.2) governs every line: implemented-and-proven
means the cited suite runs it green from a clean checkout via
`./run-tests.sh`.

**Latest recorded runs (2026-11-07, after the final completion wave
below): 266 tests, 266 passed, 0 failed** with `./run-tests.sh`
(the file and SQLite providers — the no-server gate), and **524 tests,
524 passed, 0 failed** with both `DE_SQLSERVER_CONNECTION` and
`DE_POSTGRES_CONNECTION` set against test servers — the same suite run
per provider under the §6.5 conformance gate (see the P1, P2, and
final-wave sections below for the coverage matrices).

## P0 acceptance criteria (Foundation §14) — status

| Criterion | Status | Evidence |
|---|---|---|
| The minimal workflow runs end-to-end | Proven | Scenario C; `HostInProcessTests.MinimalWorkflow_*` |
| §7.3 scenario 1: kill during activity execution → activity re-runs (at-least-once), workflow completes | Proven, minimal form | Scenario B (kill inside the charge window; `chargeExecutions == 2`, workflow completes) |
| §7.3 scenario 2: kill after external charge, before result recording, idempotent gateway → single charge, key identical across attempts | Proven, minimal form | Scenario B: `chargesApplied == 1`, both gateway calls carry the same key, outcomes `[charged, duplicate-key]` |
| §7.3 scenario 3: same, non-idempotent gateway → documented §2 behaviour + reconciliation asserted | Proven, minimal form | Scenario E: duplicate charge really happens (`chargesApplied == 2`), reconciliation refunds exactly once and survives being killed mid-refund |
| Completed activities are not re-executed (by execution counters, not inspection) | Proven | Scenario A (`chargeExecutions == 1` after a kill past the completion record); counters cross-checked against recorded attempts in every A–E run |
| Replay against a deliberately non-deterministic change throws `NonDeterminismViolation` | Proven | Scenarios G (definition), I (loop item input), J (workflow input amount 1000→5000); `HostInProcessTests` resume-violation tests |

## Scenario mapping (spike v3 A–K → this suite)

The 11 scenarios of the crash-recovery proof (Foundation §19, condition 7)
are carried over one-for-one, now as xUnit tests driving the library through
a real child process (`DurableExecution.TestHost`). The spike asserted 102
checks via bash exit codes + a Python verifier; those checks are ported as
strict assertions over the same state files (counters, history, gateway call
log, gateway ledger, items store, reconciliation report).

| Spike | Suite test | What it pins |
|---|---|---|
| A | `ScenarioAToETests.A_*` | Kill after a completion is durable → nothing re-executes |
| B | `ScenarioAToETests.B_*` | §7.3 scenarios 1+2 — the §8.1 window against an idempotent gateway |
| C | `ScenarioAToETests.C_*` | Clean run; re-invoking a completed instance is a no-op |
| D | `ScenarioAToETests.D_*` | Kill mid-write → torn tail truncated (unterminated ≠ committed), command re-executes |
| E | `ScenarioAToETests.E_*` | §7.3 scenario 3 — non-idempotent duplicate + crash-safe reconciliation, net effect one charge |
| F1/F2/F3 | `ScenarioFToKTests.F1/F2/F3_*` | Terminated-record damage is corruption and refused (invalid JSON / sequence gap / checksum-caught tamper) |
| G | `ScenarioFToKTests.G_*` | Changed workflow definition at resume → violation |
| H | `ScenarioFToKTests.H_*` | Loop: 3 commands, 3 distinct keys, item b re-executes (4 executions) without duplicating its effect (3 effects) |
| I | `ScenarioFToKTests.I_*` | Changed loop item input → fingerprint violation |
| J | `ScenarioFToKTests.J_*` | Changed workflow input (amount) → fingerprint violation |
| K | `ScenarioFToKTests.K_*` | Second host on the same state refused while the single-writer lock is held |

Fan-out scenarios (new with the §5.3 fan-out — no spike counterpart; same
harness, one workflow of three parallel ChargeCard branches with per-branch
references and execution counters):

| Suite test | What it pins |
|---|---|
| `ScenarioFanOutTests.L_*` | Kill mid-fan-out: branch 1 complete, branch 2 charged-but-unrecorded, branch 3 in flight → resume replays branch 1, re-executes branch 2 under its recorded key (gateway deduplicates: outcomes `charged`, `duplicate-key`), executes branch 3 once; one applied charge per branch; per-branch attempts and executions asserted separately; combined workflow result is the branches' transactions in schedule order |
| `ScenarioFanOutTests.M_*` | Kill with every branch in flight (all scheduled + attempted, none completed, none reached the gateway) → resume executes each branch exactly once; attempts per branch go 1 → 2 without duplicating effects |
| `ScenarioFanOutTests.N_*` | Kill after the join (all branches complete, workflow completion unrecorded), then resume with branch 2's input changed → fingerprint violation inside the fan-out, raised before any execution; nothing re-runs |

Unit coverage beside the scenarios: history framing/validation rules
(torn vs corrupt, gap, duplicate schedule, orphan completion, event after
completion, sequence continuation across store instances, append before
load refused), ADR-012 key properties (stability, hex shape, distinctness
per sequence/activity/instance/namespace), input fingerprints, in-process
host semantics (resume, recorded-key reuse, violations — including the
activity-identity change at a recorded position — ownership), and the
fan-out unit tests: result combination in schedule order against a forced
reverse completion order, branch keys as the ADR-012 keys of their
positions (continuing the sequence after a sequential call), partial
completion resuming only the faulted branch under its recorded key,
changed branch input/activity refused at resume, duplicate branches keyed
distinctly, empty fan-out consuming no command positions, and the
non-generic overload.

## P1, first item — SQL Server provider and the §6.5 conformance suite

Delivered 2026-11-07, gated exactly as §6.5 requires: the provider ships
because the shared suite runs green against it, not because the code
compiles.

**The provider** (`src/DurableExecution.SqlServer`, `SqlHistoryStore`):
one instance's history as rows of `dbo.HistoryEvents (InstanceId, N,
Payload)` in a caller-provisioned database. A row's payload is the
identical sealed JSON record the file provider frames — same fields, same
checksum (`HistorySealing`) — and load runs the same structural validator
(`HistoryRecordValidator`, extracted from the file provider), so both
media refuse the same damage with the same words (asserted by the F
scenarios and the conformance corruption tests on both providers).

- **Checkpoint atomicity (§6.2):** an append is one serializable
  transaction — verify the table still ends where this store's counter
  says, insert, commit. A crash mid-append leaves the whole record or
  nothing; `TornTailTruncated` is always false, because a rolled-back
  transaction is never committed — the file provider's torn tail is a
  framing property of files, and scenario D asserts each provider's own
  surfacing of the same contract (below).
- **Single-writer (§9):** a dedicated connection holds an exclusive
  session application lock (`sp_getapplock`, resource per instance) from
  load until dispose — the OS lock's in-database analogue. A second store
  for the same instance is refused (`StateOwnedByAnotherHostException`)
  after a 2-second grace window that exists only because the server
  reaps a killed owner's session asynchronously; a live owner outlasts
  the window and is refused. Appends additionally re-verify the
  end-of-history inside their transaction, so a writer that bypasses the
  cooperative lock and appends anyway is detected and refused over.
  Limits, honestly: the lock is cooperative, and none of this fences
  writers across databases or machines — cross-device fencing stays P3
  and is not faked here.
- **The fault seam's meaning on this provider:** the harness's
  torn-write kill fires inside the append transaction; the process dies,
  the connection dies with it, and the server rolls the record back.

**The conformance suite (§6.5):** the scenario bodies (A–N), the store
contract tests, and the host/fan-out unit tests are each written once
against a provider surface and executed per provider via derived test
classes (trait `Provider=File` / `Provider=SqlServer`). `run-tests.sh`
excludes the SQL Server classes unless `DE_SQLSERVER_CONNECTION` names a
test server, so the file evidence (47) stays reproducible anywhere and
the SQL evidence is claimed only where it ran. SQL runs provision and
drop one scratch database per test; no connection string exists in the
repository. Environment note: the suite runs under the xunit console
runner (vstest's host cannot connect in the build sandbox), and the
runner's flat assembly probing loads SqlClient's platform facade instead
of the deps-selected implementation — the test project pre-loads the
correct runtime asset in a module initializer
(`SqlClientRuntimeAsset.cs`), which is a no-op under a normal app host.

| Evidence | File | SQL Server |
|---|---|---|
| Scenarios A–E (§7.3 + clean run + mid-write) | 5 | 5 |
| Scenarios F–K (corruption, determinism, loop, ownership) | 8 | 8 |
| Fan-out scenarios L–N | 3 | 3 |
| Store conformance contract (append/load, corruption rules) | 10 | 10 |
| Host unit tests | 8 | 8 |
| Fan-out unit tests | 8 | 8 |
| SQL-provider-specific (session lock, instance isolation) | — | 4 |
| File-framing only (torn tail truncation/report) | 1 | — |
| Keys and fingerprints (provider-agnostic) | 4 | — |
| **Totals** | **47** | **46** |

Named adaptations, with reasons:

- **Scenario D** runs on both providers with provider-shaped discard
  evidence: on the file provider, unterminated bytes on disk plus the
  "torn tail" report on resume; on SQL Server, the killed append's row
  simply absent (rolled back) and no torn-tail claim made. The contract
  asserted identically on both: the interrupted completion is not
  committed, the command re-executes under its recorded key, the gateway
  deduplicates (chargeExecutions 2, chargesApplied 1).
- **The torn-tail unit test** stays file-only: truncating an unterminated
  tail is a framing behaviour a transactional medium does not have.
- **Scenario K** asserts the host's ownership refusal on both providers
  (the host file lock fires first); the SQL store's own session-lock
  refusal is asserted directly by the provider-specific tests above.

The SQL evidence was produced against SQL Server 2022 (Developer
Edition) running in the build environment, installed from the kept
package and driven under a dedicated least-privilege test login
(`dbcreator` only, so the suite can provision its scratch databases);
the databases are dropped by the tests, verified empty afterwards.

## P1, remaining items — PostgreSQL provider, SQLite provider, snapshots

Delivered 2026-11-07 under the same §6.5 gate: each provider ships
because the shared suite runs green against it.

**PostgreSQL** (`src/DurableExecution.PostgreSql`, `PostgresHistoryStore`):
one instance's history as rows of `public.history_events (instance_id,
n, payload)` in a caller-provisioned database; the payload is the
identical sealed record, load runs the identical shared validator, and
an append is one serializable transaction that re-verifies the end of
history before inserting. Single-writer is the in-database analogue of
the SQL Server session lock: a dedicated connection holds
`pg_try_advisory_lock` on a 64-bit key derived from the SHA-256 of the
instance's lock resource, from load until dispose, with the same
2-second grace and the same refusal. Limits, honestly: advisory locks
are cooperative, the derived key could theoretically collide with an
unrelated advisory lock in the same database (the append end-check
still detects a bypassing writer), and nothing here fences writers
across databases or machines — cross-device fencing stays P3. Evidence:
the full conformance matrix green against PostgreSQL 16.15 stood up
locally as a scratch cluster under a dedicated least-privilege login
(`createdb` only, so the suite can provision its scratch databases);
the databases are dropped by the tests and the cluster was discarded.

**SQLite** (`src/DurableExecution.Sqlite`, `SqliteHistoryStore`): the
embedded development provider — one `history.db` file per state
directory, `HistoryEvents (N, Payload)`, `PRAGMA synchronous = FULL`,
one transaction per append with the same end-of-history re-verification.
Single-writer is the file provider's own mechanism: an OS lock file
held from load to dispose. No server, no environment variable; the
suite runs it always.

**Snapshots (the remaining P1 item).** Every store now writes a sealed
snapshot of the workflow's logical state every N appends (default 50;
configurable per store, 0 disables): the same facts a full replay would
rebuild — per-command activity, input fingerprint and recorded
idempotency key, attempt counts, results, workflow result — sealed with
the same SHA-256 checksum discipline as history records. On the file
provider the snapshot lands by atomic rename after the append; on the
database providers it is upserted inside the append's own transaction,
so a snapshot can never claim events a crash rolled back. Resume loads
snapshot + tail instead of replaying from event zero. Semantics, chosen
by the sealing model: a snapshot is sealed data, so a damaged,
unparseable, or ahead-of-history snapshot is corruption and is refused
loudly with `HistoryCorruptionException` — there is no silent fallback
to full replay, for the same reason a damaged history record is refused
rather than skipped. Equivalence is proven, not asserted: per provider,
store tests reconstruct a snapshotted history two ways and compare
event-for-event (roles, keys, results), corrupt-snapshot and
ahead-of-history refusals are asserted, and two crash scenarios (B: the
§8.1 window; L: mid-fan-out kill) run with a snapshot forced every 2
events against a no-snapshot control, asserting identical counters,
identical recorded keys, identical history shape, and a resume the host
reports as snapshot-served.

| Evidence | File | SQL Server | PostgreSQL | SQLite |
|---|---|---|---|---|
| Scenarios A–E (§7.3 + clean run + mid-write) | 5 | 5 | 5 | 5 |
| Scenarios F–K (corruption, determinism, loop, ownership) | 8 | 8 | 8 | 8 |
| Fan-out scenarios L–N | 3 | 3 | 3 | 3 |
| Store conformance contract (append/load, corruption rules) | 10 | 10 | 10 | 10 |
| Host unit tests | 8 | 8 | 8 | 8 |
| Fan-out unit tests | 8 | 8 | 8 | 8 |
| Snapshot store tests (equivalence, corruption, ahead-of-history) | 3 | 3 | 3 | 3 |
| Snapshot scenario equivalence (B and L, snapshots forced) | 2 | 2 | 2 | 2 |
| SQL-provider-specific (session lock, instance isolation) | — | 4 | — | — |
| File-framing only (torn tail truncation/report) | 1 | — | — | — |
| Keys and fingerprints (provider-agnostic) | 4 | — | — | — |
| **Totals** | **52** | **51** | **47** | **47** |

One honest note on the snapshot scenario equivalence for L: fan-out
branches race, so the comparison is over the deterministic content —
schedules, keys, completions, results — as a multiset; attempt records
are excluded because how many attempts the in-flight branch needed is a
property of the clock, and branches 1 and 3 may each execute once or
twice depending on whether the kill caught them in flight. What may
never vary, and is asserted per run: branch 2 executes exactly twice,
and exactly one charge per branch is applied.

## P2 core — retry policies, durable timers, signals

Delivered 2026-11-07, in the roadmap's order, each gated on the same
per-provider suite.

**Retry policies.** A call may carry a `RetryPolicy` (max attempts,
initial delay, backoff multiplier, optional cap). A failed attempt is
recorded (`ActivityAttemptFailed`: command, attempt, error) before the
backoff wait, so the budget is durable: a host that dies between
attempts resumes with the recorded failure count, and a command whose
recorded failures reach the maximum is exhausted — resuming re-throws
`ActivityFailedException` without executing again. An attempt
interrupted by a crash consumes no budget (it never finished, exactly
as it is not a completion). Backoff is a pure function of the failure
count — no jitter — and waits run on the new `IWorkflowClock` seam.
Without a policy, behaviour is unchanged from P0, asserted by test.
Evidence per provider: unit tests (flaky-then-succeeds with the fake
clock asserting the [100ms, 200ms] schedule and one key across
attempts; durable exhaustion across a resume; no-policy regression)
and scenario O across real process kills, including a kill between
attempts after which the resume spends exactly the remaining budget.

**Durable timers.** `DelayAsync` takes a command position.
`TimerScheduled` records the due time and requested duration before
the wait begins; the wait runs against the recorded due time, so a
host that dies mid-wait resumes waiting only for the remainder, and
`TimerFired` closes the timer. A changed duration at the recorded
position is a determinism violation. Evidence per provider: manual-
clock unit tests (fires at the recorded due time, not before; death
mid-wait; violation; zero duration) and scenario P — a real-clock 3s
timer killed ~0.4s into the wait, proven from the workflow's own
wall-clock marks to fire at the original due time on resume.

**Signals.** `WaitForSignalAsync` waits for an external signal;
`SignalReceived` is appended to history before delivery, always — the
host's `SendSignal` appends inside the append gate and only then
wakes a waiter, and a signal appended to the store while no host ran
is delivered from history on the next run. Consumption is per name,
oldest first. Named limit: live delivery to a *running* host is an
in-process call on that host; a cross-process sender appends while no
host runs (the single-writer lock is the boundary) — there is no
cross-process live push in this stage. Evidence per provider: unit
tests (recorded-before-delivered ordering in history, payload-driven
effect, pre-run delivery, per-name ordering, no-active-run refusal)
and scenario Q — killed while waiting, signal recorded by a separate
sender process while dead, resume charges exactly the approved
amount exactly once.

Updated totals (the P1 matrix plus P2):

| Evidence | File | SQL Server | PostgreSQL | SQLite |
|---|---|---|---|---|
| P1 matrix (scenarios A–N, contract, host, fan-out, snapshots, provider-specific, keys) | 52 | 51 | 47 | 47 |
| Retry unit tests | 4 | 4 | 4 | 4 |
| Scenario O (retries, incl. kill between attempts) | 2 | 2 | 2 | 2 |
| Timer unit tests (manual clock) | 4 | 4 | 4 | 4 |
| Scenario P (timer, kill while waiting, real clock) | 2 | 2 | 2 | 2 |
| Signal unit tests | 4 | 4 | 4 | 4 |
| Scenario Q (signal, killed while waiting) | 1 | 1 | 1 | 1 |
| **Totals** | **69** | **68** | **64** | **64** |

## Final completion wave — encryption, key expiry, version gates, fencing, telemetry, seeded kills, dashboard

Delivered 2026-11-07, in this order, each item gated on the full
four-provider suite before the next began (gate totals as each item
landed: 421 → 441 → 497 → 513 → 514 → 518 → 524).

**Payload encryption at rest (ADR-014/017).** Each store takes an
optional `HistoryEncryption` (default off — existing plaintext
histories keep loading). When on, every record's payload is sealed as
an envelope: AES-256-GCM (12-byte nonce, 16-byte tag, AAD binding the
record format), stored as `{v, enc, kid, nonce, ct, sum}` where `kid`
is the first 16 hex of the key's SHA-256 and `sum` is the existing
history checksum computed **over the envelope** — seal over
ciphertext, so integrity is verified before any decryption is
attempted. Snapshots ride the same envelope. The key comes only from
the `DE_HISTORY_ENCRYPTION_KEY` environment variable (base64,
32 bytes); unset means off, malformed means a loud refusal at
startup. Cross-mode opens are refused both ways (encrypted store over
a plaintext history, and the reverse), a wrong key is refused by its
key id, a flipped ciphertext bit is refused by the checksum, and a
forged envelope with a recomputed checksum is refused by GCM
authentication. Proof: the host scenario suite runs with encryption on
for every provider (20 derived classes), plus store-level encryption
tests and process-level encrypted crash scenarios per provider.
Named adaptation: the store-conformance and snapshot suites have no
encrypted variants — their damage helpers splice plaintext records,
which an encrypted history refuses by design; the encrypted damage
model (tamper, wrong key, cross-mode, snapshot envelope) is covered
by the dedicated encryption tests instead.

**§8.5 idempotency-key expiry.** Keys are ADR-012 derivations recorded
in history, and history is never purged — what expires is the
*enforcement registry* each store derives beside the history: seq →
key plus the instance's completion time, folded from history on load,
maintained inside each append's commit unit, and persisted (file: a
sealed `history.keys.json`; SQL Server: `dbo.HistoryKeys` +
`dbo.HistoryInstanceState`; PostgreSQL and SQLite: the same tables in
their stores). A store configured with a `KeyRetentionPolicy`
(retention window) offers `PurgeExpiredKeys(now)`: for instances
completed longer than the window, registry rows are deleted and a
purge marker is recorded so a later load does not resurrect them —
safe because completion is terminal, so no append can ever need those
keys again, and the history itself stays fully valid and readable
(it is never touched). The host exposes the sweep as
`WorkflowHost.PurgeExpiredKeys(utcNow)` — an explicit call, no
background thread. Without a configured policy the purge refuses.
Per provider: registry fidelity against history, dedup intact before
expiry, purge after it, uncompleted instances untouched, refusal
without a policy, no resurrection across reload.

**Code-version gates (P4 core).** A new history event,
`VersionMarker`, records gate resolutions. `WorkflowContext.GetVersion
(changeId, minSupported, maxSupported)`: on the live pass it appends a
marker at `maxSupported`; on replay it consumes markers in order —
recorded version inside the range returns it, outside refuses with a
named `VersionGateException`; a marker whose changeId does not match
the gate at that position is a determinism violation. A gate the old
code never had (an early-instance history with recorded commands after
the gate's position but no marker) resolves to the documented default
−1 and records nothing, so pre-gate code keeps its old behaviour
explicitly instead of silently adopting the new default. Markers fold
through snapshots like any other fact. Proof per provider: gate unit
tests (live resolution, in-range/out-of-range resume, marker folding)
and scenarios R1–R4 — gated resume records the marker and charges
once, an ungated edit to a live workflow body is a determinism
violation on replay, a host whose range no longer covers the recorded
marker refuses before executing anything.

**Fencing (P3 core).** Opt-in per store (`FencingOptions`: lease
duration, owner id). Load acquires the instance's lease — a stored
record of owner, **monotonic token**, and expiry (file: sealed
`history.lease.json` under a lock-file guard; SQL Server:
`dbo.HistoryLeases`; PostgreSQL: `public.history_leases`; SQLite:
`HistoryLeases`) — bumping the token on every takeover, renewing
without a bump for the same owner, and refusing a live foreign lease
with `StateOwnedByAnotherHostException`. Every append re-verifies
owner + token + unexpired lease *inside the append's own transaction*;
a stale writer is refused with `StaleFencingTokenException`. Proof per
provider: takeover after expiry bumps the token and the stale owner's
next append is refused while the history stays sealed-valid; expiry
alone (no rival) fences the original owner. **What this does not
solve, stated plainly:** it is cooperative fencing inside one store —
a writer that bypasses the store API is caught only by the seal and
end-of-history checks; there is no cross-database consensus and no
distributed runtime here; lease expiry runs on the store's clock, so
clock skew shifts takeover timing; on the file provider the lease
check and the append are two writes, not one transaction (the
end-check narrows, not eliminates, that window).

**OpenTelemetry.** `EngineDiagnostics` owns an `ActivitySource` and a
`Meter`, both named `DurableExecution`: spans for workflow runs
(definition, instance, outcome tags), per-activity executions
(including retries), and every store append (store, event type,
instance tags); counters for store appends, workflow runs, and
activity executions. No new packages — the BCL diagnostics APIs.
Overhead, honestly: with no listener attached the calls are near-free
(an enabled check and a no-op span); with a listener/exporter attached
the cost is the standard OpenTelemetry cost of span and measurement
creation — it is not claimed to be zero. Proof: an in-memory
listener test asserting the span names/tags and measurement names/tags
for a real two-activity run.

**Seeded randomized kills (§7 harness growth).** The scenario host
takes `CRASH_SEED` (+ `CRASH_RUN` for the run index): a decorating
store kills the process before append N, N drawn from
`Random(seed * 31 + run)` in [1, 6] of the reference workflow's 8
appends — kills land at append boundaries; mid-write tearing remains
scenario D's ground. The test runs seeds 1–6 on every provider,
asserting run 0 is always killed at the drawn point and that the
resume loop converges to exactly one applied charge with a sealed,
complete history. Reproduction is the schedule itself: the same seed
and run index draw the same kill point, printed in the host's output.

**Dashboard.** `src/DurableExecution.Dashboard` — a minimal ASP.NET
Core operator app over any provider (`DE_DASHBOARD_PROVIDER`,
`DE_DASHBOARD_STATE_ROOT`, the providers' usual connection variables;
encryption follows `DE_HISTORY_ENCRYPTION_KEY`). The instance list
derives each status from its sealed history through a validating
load; the detail page renders that verified event list; a history the
store refuses renders as CORRUPT with the refusal text and no event
table — never a partial view. Sending a signal appends
`SignalReceived` through the store's own append path; a completed
instance refuses signals loudly (nothing may follow
`WorkflowCompleted`). HTTP tests host the app's real route table on
TestServer (real HTTP semantics, no sockets — this sandbox's
permission layer denies loopback TCP from test processes; the
standalone app was additionally smoke-verified over real HTTP: list,
signal POST, detail). `run-tests.sh` now passes the test assembly's
runtimeconfig to the console runner so the ASP.NET Core framework
resolves under it.

Updated totals (P2 matrix plus the final wave):

| Evidence | File | SQL Server | PostgreSQL | SQLite |
|---|---|---|---|---|
| P2 totals | 69 | 68 | 64 | 64 |
| Encryption: encrypted host-suite variants | 28 | 28 | 28 | 28 |
| Encryption: store-level + process scenarios | 11 | 11 | 11 | 11 |
| Key-retention (§8.5) tests | 5 | 5 | 5 | 5 |
| Version-gate tests + scenarios R1–R4 | 14 | 14 | 14 | 14 |
| Fencing tests | 4 | 4 | 4 | 4 |
| Seeded-kill scenarios (seeds 1–6) | 1 | 1 | 1 | 1 |
| Telemetry (provider-agnostic, file-backed) | 1 | — | — | — |
| Dashboard HTTP tests (file provider) | 6 | — | — | — |
| **Totals** | **139** | **131** | **127** | **127** |

## Foundation sections → implemented / not yet

| Foundation | State in P0 |
|---|---|
| §2 Guarantee contract | Normative in README + code docs; enforced by scenarios B and E (both gateway modes asserted) |
| §4 Architecture / stage A | Library core present: orchestrator context, checkpoint-by-append, recovery by replay. The `IHistoryStore` seam has four conformance-proven providers (file, SQL Server, PostgreSQL, SQLite); stage B/C remain interfaces only |
| §5.3 Execution model (P0 subset) | Sequential durable awaits, recorded-result replay, and the `WhenAll` fan-out implemented (`CallAllAsync`): branches are scheduled synchronously in list order — per-branch activity identity, input fingerprint and ADR-012 key settled before any branch executes — then run concurrently, each completion recorded as it lands, results combined in schedule order regardless of completion order; proven by scenarios L/M/N and the fan-out unit tests. P2 added the durable-wait mechanisms on the same command model: retry policies (per call or per fan-out branch), durable timers, and signals — see the P2 section. P0 form keeps the activity body with the call; name→registry resolution and the §5.1 two-argument surface are not built. Roslyn analyzers (ADR-006, Proposed) not built; replay validation is the active enforcement layer |
| §6.1 Event model | Schema v1 emits: `WorkflowStarted`, `ActivityScheduled`, `ActivityAttemptStarted`, `ActivityAttemptFailed` (P2 retries), `ActivityCompleted`, `WorkflowCompleted`, `TimerScheduled`, `TimerFired` (P2 timers), `SignalReceived` (P2 signals), `VersionMarker` (final wave, P4 gates). Remaining §6.1 types (sub-workflow events) are reserved for their phases. Payload encryption (ADR-014/017) is built — see the final-wave section; payloads still sit inside completion records, encrypted as whole records |
| §6.2 Checkpoint atomicity | Append+fsync per event against the file provider; one serializable transaction per append against the database providers (P1). Snapshots ride the same atomicity: atomic rename on the file provider, the append's own transaction on the database providers. Fencing verification rides the append transaction on the database providers |
| §6.3 Deterministic replay & validation | Implemented and asserted (see mapping above) |
| §6.5 Provider conformance | One suite, executed per provider (file, SQL Server, PostgreSQL, SQLite), 524 tests green with all four — see the P1, P2, and final-wave sections for the matrices and the named adaptations |
| §7 Fault testing | Harness present (fixed kill points across all four providers), extended by P2 with the between-attempts kill (O), the kill-while-waiting (P, Q), and by the final wave with seeded randomized kill scheduling (seeds 1–6 per provider, deterministic reproduction by seed + run index) |
| §8 Idempotency & side effects | Stable keys + propagation via `ActivityContext` implemented. §8.5 key-retention window is built (derived key registry + explicit purge, see the final-wave section). Side-effect classes, engine-managed transactional path, outbox, dedup helper, reconciliation *hooks* (`IReconciliationCheck`) are not built — reconciliation exists as the harness's resumable command (scenario E), not yet as a runtime API |
| §9 Leases/fencing (stage B) | Fencing core built (final wave): per-instance leases with monotonic tokens on all four providers, append-time verification, stale-writer refusal — cooperative, inside one store; its limits are stated in the final-wave section. The stage-B distributed runtime and cross-database consensus are not built |
| §10 Versioning | Version gates built (final wave, P4 core): `GetVersion` with `VersionMarker` events, range refusals, pre-gate default. A golden-history corpus is not built |
| §11 Dashboard / OTEL | Built (final wave): `EngineDiagnostics` ActivitySource + Meter with an in-memory listener test; the ASP.NET Core dashboard with list / verified-history detail / signal sending and HTTP tests |
| §12 Tenancy/security | Namespace id is carried in key derivation and shaped in the schema (ADR-010); single-namespace runtime. Payload encryption built (final wave); the multi-namespace isolation suite and crypto-erasure of whole histories are not built |
| §13 Benchmarks | Not run — targets are P6 evidence, not P0 claims |

## Deviation note — SQL Server in the §14 P0 scope line (closed in P1)

§14's P0 scope line names "SQL Server persistence (first provider)". The
P0 stage delivered the persistence seam (`IHistoryStore`) and the file
provider — the provider whose behaviour the accepted crash-recovery proof
(§19, condition 7) actually exercised — and did **not** deliver a SQL
Server provider, because a provider advances only on reproducible test
evidence (§7.2), §6.5 makes the conformance suite the gate for any
provider, and no SQL Server was available in the build environment to
produce evidence against. That sequencing deviation was recorded openly
rather than absorbed silently; the §14 P0 *acceptance criteria* (which
are store-agnostic) were met in full.

**Closed 2026-11-07:** the SQL Server provider is delivered as the first
P1 item, ahead of PostgreSQL and the SQLite development provider, gated
by the §6.5 conformance suite running against a real SQL Server — see
the P1 section above.

## Named gaps — what is actually left

- Real power loss: process kills are not power cuts; fsync-per-append and
  atomic renames are the strongest claims made, and directory-entry
  durability under power loss is a filesystem/hardware property. Proving
  it needs a machine whose power can actually be cut — it cannot be
  produced in this build sandbox, and no lesser test is presented as it.
- Cross-database consensus and the stage-B/C distributed runtime:
  fencing (above) is cooperative inside one store. Coordinating writers
  across databases or machines — consensus, leader election, a hosted
  runtime service — is a separate stage and is not claimed by anything
  in this repository.
- Sub-workflow event types: still reserved in the §6.1 schema; child
  workflows are not built.
- Side-effect machinery beyond keys and reconciliation-by-command:
  side-effect classes, the engine-managed transactional path, an outbox,
  and reconciliation hooks (`IReconciliationCheck`) are not built.
- A golden-history corpus for versioning, the multi-namespace isolation
  suite, crypto-erasure of whole histories, and §13 benchmarks: not
  built, not claimed.
