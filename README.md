# Durable Execution Platform for .NET — P0

> Name and licence settled on 2026-11-07 (closing the open decisions in
> Engineering Foundation §18 / ADR-009): the project is the **Durable
> Execution Platform for .NET**, released under the **Apache License 2.0**
> (see [LICENSE](LICENSE)).

An open-source durable execution platform native to .NET: you write a
long-running process as ordinary C# code, and the engine owns continuity —
if the host crashes or restarts, the process resumes from its recorded
history instead of starting over or stopping half-done.

This repository is **stage A (the embeddable library) at phase P0**: the
durable execution core. It is built under one governing rule, taken from the
Engineering Foundation (§7.2): **no phase advances on "the code works" — a
phase advances only on reproducible test evidence.** The evidence command is
`./run-tests.sh`; the current results and the exact scope of what is proven
live in [STATUS.md](STATUS.md).

## The guarantee contract

This contract is normative (Foundation §2, ADR-003/005). It is stated here in
substance because every other claim in this repository is bounded by it.

**The engine guarantees:**

1. **Durable execution history.** Every orchestration decision and every
   activity result is appended to a durable, append-only history before
   dependent work proceeds. History is the single source of truth.
2. **Correct state recovery.** After a crash or restart, a workflow resumes
   from its recorded history with its logical state intact, via deterministic
   replay.
3. **At-least-once activity execution.** An activity that was scheduled but
   whose result was not durably recorded may be executed again. Activity
   execution is *at-least-once*, never "exactly-once."
4. **Stable idempotency keys.** Every logical activity invocation is a
   command with a stable identity; its idempotency key is derived from that
   identity, is identical on attempt 1 and attempt 9, and is passed to the
   activity for its outbound calls.

**The engine does not and cannot guarantee:**

1. **No exactly-once external side effects.** If an external system executes
   a call and the host crashes before recording the result, the engine cannot
   know whether the effect happened; on recovery the activity may run again.
2. Preventing a duplicate external effect requires the external system to
   cooperate — by honouring the idempotency key, or by offering
   reconciliation. The engine supplies the key and the reconciliation path;
   the guarantee is joint.
3. If an external system supports neither idempotency nor reconciliation,
   duplicate effects cannot be prevented by this engine — or by any engine.
   The test suite asserts this boundary instead of hiding it: scenario E runs
   a non-idempotent gateway, the duplicate charge really happens, and
   reconciliation compensates it with a real refund.

## What P0 contains

- **Sealed append-only history** (`FileHistoryStore`): one JSON record per
  line, newline-framed, each record carrying a SHA-256 checksum over its
  canonical content. A record counts as committed only if newline-terminated:
  an unterminated tail is a *torn write* (truncated, reported, never
  committed); a terminated but damaged record is *corruption* and is refused.
  Loading validates schema and structure end to end (gapless sequences,
  one schedule per command, sequential attempt numbers, completions matched
  to their schedules, a single final workflow completion).
- **Replay engine with determinism validation** (`WorkflowHost`,
  `WorkflowContext`): resume re-checks the workflow's definition id and input
  fingerprint before any command runs, and each command's activity identity
  and input fingerprint at its recorded position. A contradiction halts with
  `NonDeterminismViolationException` — loud, never silent divergence.
- **Command/attempt separation** (ADR-012): a command is scheduled once;
  retries are attempts (`ActivityAttemptStarted`) under the same identity and
  the same key. The key reaches the activity through `ActivityContext`; on
  replay the *recorded* key is passed verbatim.
- **Fan-out** (`CallAllAsync`, the §5.3 `WhenAll`): branches are scheduled
  synchronously in list order — each branch's position, input fingerprint
  and idempotency key settled before any branch runs — then executed
  concurrently, each completion recorded as it lands; results combine in
  schedule order regardless of completion order. A faulted branch keeps the
  other branches' recorded completions and re-executes alone on resume
  under its recorded key; a changed branch definition is refused by the
  same fingerprint checks as a sequential call.
- **Crash-safe auxiliary writes**: atomic file replacement (temporary file +
  fsync + rename) for counters and ledgers, and a single-writer OS lock per
  instance state — a second host is refused.
- **The crash-injection suite** (§7): a scriptable payment-gateway stub in
  idempotent and non-idempotent modes, a resumable reconciliation command,
  and 11 scenarios (A–K) executed across real process kills, plus three
  fan-out scenarios (L–N) over parallel branches, with strict assertions
  over counters, history, the gateway call log, and the gateway ledger.
  Counters are cross-checked against recorded attempts — evidence is
  verified, not eyeballed.

The P0 reference workflow is deliberately minimal (Foundation §14):

```csharp
var charge = await ctx.CallActivityAsync<ChargeResult>("ChargeCard", input);
await ctx.CallActivityAsync("ReserveStock", input.OrderId);
return OrderResult.Completed;
```

No timers, no signals, no retries in P0 — by the roadmap's definition, not
by omission.

## Layout

```
src/DurableExecution.Core/        the library (stage A core)
  History/                        event model, store seam, file provider
  Execution/                      host, workflow context, identity, fingerprints
  IO/                             atomic durable file writes
tests/DurableExecution.TestHost/  scenario host process the suite kills/restarts
tests/DurableExecution.Tests/     xUnit: Scenarios (A–K, fan-out L–N) + Unit (store, keys, host, fan-out)
```

`IHistoryStore` is the seam the distributed runtime (stage B) and the future
database providers consume; every provider must pass the shared conformance
contract (Foundation §6.5) before it may ship.

## Running the tests

```bash
./run-tests.sh
```

Builds the solution in Release and runs the whole suite from a clean
checkout. Latest recorded result (2026-11-07): **47 passed, 0 failed** —
see STATUS.md for the breakdown and the scenario mapping.

## Not yet here

SQL Server / PostgreSQL providers, snapshots, timers, retries, signals,
distributed leases and fencing, version gates, payload encryption and
crypto-erasure, the dashboard. Each is a named phase or a named gap with its
acceptance criteria in the Engineering Foundation; STATUS.md tracks them
individually. Nothing in this list is claimed by this code.
