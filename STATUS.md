# STATUS — P0 foundation stage

Recorded 2026-11-07. This note maps the Engineering Foundation (v0.2) to what
this repository implements and proves today, and names everything it does
not. The evidence rule (§7.2) governs every line: implemented-and-proven
means the cited suite runs it green from a clean checkout via
`./run-tests.sh`.

**Latest recorded runs (2026-11-07, after P1's first item): 47 tests,
47 passed, 0 failed** with `./run-tests.sh` on the file provider alone
(the P0 evidence, unchanged), and **93 tests, 93 passed, 0 failed** with
`DE_SQLSERVER_CONNECTION` set against a test SQL Server — the same suite
run per provider under the §6.5 conformance gate (see the P1 section
below for the coverage matrix).

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

## Foundation sections → implemented / not yet

| Foundation | State in P0 |
|---|---|
| §2 Guarantee contract | Normative in README + code docs; enforced by scenarios B and E (both gateway modes asserted) |
| §4 Architecture / stage A | Library core present: orchestrator context, checkpoint-by-append, recovery by replay. The `IHistoryStore` seam has two conformance-proven providers (file, SQL Server); stage B/C remain interfaces only |
| §5.3 Execution model (P0 subset) | Sequential durable awaits, recorded-result replay, and the `WhenAll` fan-out implemented (`CallAllAsync`): branches are scheduled synchronously in list order — per-branch activity identity, input fingerprint and ADR-012 key settled before any branch executes — then run concurrently, each completion recorded as it lands, results combined in schedule order regardless of completion order; proven by scenarios L/M/N and the fan-out unit tests. P0 form keeps the activity body with the call; name→registry resolution and the §5.1 two-argument surface are not built. Roslyn analyzers (ADR-006, Proposed) not built; replay validation is the active enforcement layer |
| §6.1 Event model | Schema v1 emits the P0 subset: `WorkflowStarted`, `ActivityScheduled`, `ActivityAttemptStarted`, `ActivityCompleted`, `WorkflowCompleted`. Remaining §6.1 types are reserved for their phases. Payload separation/encryption (ADR-014/017) not built — payloads currently sit inside completion records |
| §6.2 Checkpoint atomicity | Append+fsync per event against the file provider; one serializable transaction per append against the SQL Server provider (P1) |
| §6.3 Deterministic replay & validation | Implemented and asserted (see mapping above) |
| §6.5 Provider conformance | Implemented with P1's first item: one suite, executed per provider (file + SQL Server), 93 tests green with both — see the P1 section for the matrix and the named adaptations |
| §7 Fault testing | Harness present in minimal P0 form (fixed kill points), now across both providers. Seeded randomized kill-point scheduling is P1 harness growth, not yet built |
| §8 Idempotency & side effects | Stable keys + propagation via `ActivityContext` implemented. Side-effect classes, engine-managed transactional path, outbox, dedup helper, reconciliation *hooks* (`IReconciliationCheck`) are not built — reconciliation exists as the harness's resumable command (scenario E), not yet as a runtime API. §8.5 key-retention window policy is design only |
| §9 Leases/fencing (stage B) | Not built. Single-writer OS lock (scenario K); the SQL provider adds the in-database analogue (session application lock, cooperative — see P1 section). Cross-machine fencing is P3 |
| §10 Versioning | Detection-and-halt only (safe failure). Version gates and golden-history corpus are P4 |
| §11 Dashboard / OTEL | Not built (P5) |
| §12 Tenancy/security | Namespace id is carried in key derivation and shaped in the schema (ADR-010); single-namespace runtime. Isolation suite, payload encryption, crypto-erasure: not built |
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

## Named gaps — untouched by design

- PostgreSQL and SQLite providers (later P1 items; each ships only
  through the same §6.5 gate).
- Snapshots (P1): history is still replayed in full; the SQL provider
  does not change that.
- Seeded randomized kill-point scheduling (§7 harness growth, P1).
- Real power loss: process kills are not power cuts; fsync-per-append and
  atomic renames are the strongest claims made, and directory-entry
  durability under power loss is a filesystem/hardware property.
- Code-version migration: changed code is detected and halted (safe
  failure); version gates that let changed code legitimately continue old
  histories are P4.
- Gateway idempotency-key expiry (§8.5 retention window): policy documented
  in the foundation; no expiring-key stub or routing behaviour is built.
- Cross-device fencing: single-machine OS lock and the SQL provider's
  cooperative in-database lock only; lease fencing is P3.
- Timers, signals, retries: P2 by the roadmap — explicitly out of P0 scope.
