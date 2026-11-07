# STATUS — P0 foundation stage

Recorded 2026-11-07. This note maps the Engineering Foundation (v0.2) to what
this repository implements and proves today, and names everything it does
not. The evidence rule (§7.2) governs every line: implemented-and-proven
means the cited suite runs it green from a clean checkout via
`./run-tests.sh`.

**Latest recorded run: 34 tests, 34 passed, 0 failed** (13 scenario tests
across real process kills; 21 unit tests in-process).

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

Unit coverage beside the scenarios: history framing/validation rules
(torn vs corrupt, gap, duplicate schedule, orphan completion, event after
completion, sequence continuation across store instances), ADR-012 key
properties (stability, hex shape, distinctness per sequence/activity/
instance/namespace), input fingerprints, and in-process host semantics
(resume, recorded-key reuse, violations, ownership).

## Foundation sections → implemented / not yet

| Foundation | State in P0 |
|---|---|
| §2 Guarantee contract | Normative in README + code docs; enforced by scenarios B and E (both gateway modes asserted) |
| §4 Architecture / stage A | Library core present: orchestrator context, checkpoint-by-append, recovery by replay. Stage B/C are interfaces only (`IHistoryStore` seam) |
| §5.3 Execution model (P0 subset) | Sequential durable awaits + recorded-result replay implemented. P0 form keeps the activity body with the call; name→registry resolution and the §5.1 two-argument surface are not built. `WhenAll` fan-out is in the §5.3 P0 bullet list but is **not** in the §14 P0 acceptance set and is not implemented — recorded here as a P0-scope remainder, next after the acceptance set. Roslyn analyzers (ADR-006, Proposed) not built; replay validation is the active enforcement layer |
| §6.1 Event model | Schema v1 emits the P0 subset: `WorkflowStarted`, `ActivityScheduled`, `ActivityAttemptStarted`, `ActivityCompleted`, `WorkflowCompleted`. Remaining §6.1 types are reserved for their phases. Payload separation/encryption (ADR-014/017) not built — payloads currently sit inside completion records |
| §6.2 Checkpoint atomicity | Append+fsync per event against the file provider. Database-transaction checkpoints arrive with the SQL providers (P1) |
| §6.3 Deterministic replay & validation | Implemented and asserted (see mapping above) |
| §6.5 Provider conformance | Not yet — the suite exists per provider starting P1. The file provider is validated by the scenario suite + store unit tests |
| §7 Fault testing | Harness present in minimal P0 form (fixed kill points). Seeded randomized kill-point scheduling is P1 harness growth |
| §8 Idempotency & side effects | Stable keys + propagation via `ActivityContext` implemented. Side-effect classes, engine-managed transactional path, outbox, dedup helper, reconciliation *hooks* (`IReconciliationCheck`) are not built — reconciliation exists as the harness's resumable command (scenario E), not yet as a runtime API. §8.5 key-retention window policy is design only |
| §9 Leases/fencing (stage B) | Not built. Single-writer OS lock only (scenario K); cross-machine fencing is P3 |
| §10 Versioning | Detection-and-halt only (safe failure). Version gates and golden-history corpus are P4 |
| §11 Dashboard / OTEL | Not built (P5) |
| §12 Tenancy/security | Namespace id is carried in key derivation and shaped in the schema (ADR-010); single-namespace runtime. Isolation suite, payload encryption, crypto-erasure: not built |
| §13 Benchmarks | Not run — targets are P6 evidence, not P0 claims |

## Deviation note — SQL Server in the §14 P0 scope line

§14's P0 scope line names "SQL Server persistence (first provider)". This
stage delivers the persistence seam (`IHistoryStore`) and the file provider —
the provider whose behaviour the accepted crash-recovery proof (§19,
condition 7) actually exercised — and does **not** deliver a SQL Server
provider. Reason, under the project's own law: a provider advances only on
reproducible test evidence (§7.2), §6.5 makes the conformance suite the gate
for any provider, and that suite is P1's acceptance work; no SQL Server was
available in the build environment to produce evidence against. The SQL
Server provider is therefore the first P1 item, ahead of PostgreSQL and the
SQLite development provider. This is a sequencing deviation from the §14
scope line, recorded openly rather than absorbed silently; the §14 P0
*acceptance criteria* (which are store-agnostic) are met in full.

## Named gaps — untouched in P0, by design

- SQL Server persistence and transactional atomicity (P1, see deviation note).
- Real power loss: process kills are not power cuts; fsync-per-append and
  atomic renames are the strongest claims made, and directory-entry
  durability under power loss is a filesystem/hardware property.
- Code-version migration: changed code is detected and halted (safe
  failure); version gates that let changed code legitimately continue old
  histories are P4.
- Gateway idempotency-key expiry (§8.5 retention window): policy documented
  in the foundation; no expiring-key stub or routing behaviour is built.
- Cross-device fencing: single-machine OS lock only; lease fencing is P3.
- Timers, signals, retries: P2 by the roadmap — explicitly out of P0 scope.
- Snapshots: P1. History is replayed in full in P0.
