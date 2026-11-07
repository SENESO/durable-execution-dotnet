using DurableExecution.Execution;
using DurableExecution.History;
using DurableExecution.IO;
using DurableExecution.PostgreSql;
using DurableExecution.Sqlite;
using DurableExecution.SqlServer;

namespace DurableExecution.TestHost;

/// <summary>
/// Scenario host for the crash-injection suite (foundation §7). A thin shell
/// over the execution core: it wires a workflow definition, the stub gateway,
/// and seeded crash points into a real process that the test suite starts,
/// kills, and restarts. Configuration arrives via environment variables so a
/// scenario is fully described by (state dir, crash mode, gateway mode,
/// workflow variant, inputs).
///
/// Exit codes: 0 ok · 3 corrupt history refused · 4 non-determinism violation ·
/// 5 state owned by another host · 6 corrupt auxiliary state / key conflict.
/// A crash point kills the process outright (SIGKILL-equivalent), so the
/// shell observes a killed exit, never a graceful one.
/// </summary>
public static class Program
{
    private static string _crashMode = "";

    /// <summary>Serializes the fan-out branches' effects: the counters and the gateway ledger are single-threaded state, exactly as a real shared downstream would serialize on its own locks. Branch timing (the delays) stays outside this gate — that is what is parallel.</summary>
    private static readonly object FanGate = new();

    public static async Task<int> Main(string[] args)
    {
        string stateDir = Environment.GetEnvironmentVariable("PROOF_STATE") ?? "./p0-state";
        _crashMode = Environment.GetEnvironmentVariable("CRASH_MODE") ?? "";
        string gatewayMode = Environment.GetEnvironmentVariable("GATEWAY_MODE") ?? "idempotent";
        string variant = Environment.GetEnvironmentVariable("WORKFLOW_VARIANT") ?? "default";
        string instanceId = Environment.GetEnvironmentVariable("INSTANCE_ID") ?? "order-1001";
        int amount = int.TryParse(Environment.GetEnvironmentVariable("AMOUNT"), out var amt) ? amt : 1000;
        string[] loopItems = (Environment.GetEnvironmentVariable("ITEMS") ?? "a,b,c").Split(',');
        int holdSeconds = int.TryParse(Environment.GetEnvironmentVariable("HOLD_SECONDS"), out var hs) ? hs : 0;
        int snapshotInterval = int.TryParse(Environment.GetEnvironmentVariable("SNAPSHOT_INTERVAL"), out var si)
            ? si : FileHistoryStore.DefaultSnapshotInterval;

        try
        {
            // The history provider is a scenario dimension (§6.5 conformance):
            // file by default, SQL Server when the suite provisions a scratch
            // database and passes its connection string in the environment.
            // Everything else — state directory, lock, counters, gateway —
            // is identical, so a scenario's evidence differs only in the
            // persistence medium it claims to cover.
            IHistoryStore store;
            // Payload encryption is a store option supplied the deployment
            // way: the key arrives in DE_HISTORY_ENCRYPTION_KEY and nowhere
            // else; a malformed key refuses the run at startup.
            var encryption = HistoryEncryption.FromEnvironment();
            string historyProvider = Environment.GetEnvironmentVariable("HISTORY_PROVIDER") ?? "file";
            if (historyProvider == "sqlserver")
            {
                string connection = Environment.GetEnvironmentVariable("HISTORY_SQLSERVER_CONNECTION")
                    ?? throw new InvalidOperationException(
                        "HISTORY_SQLSERVER_CONNECTION is required for the sqlserver provider");
                store = new SqlHistoryStore(connection, instanceId, new TornWriteInjector(), snapshotInterval, encryption);
            }
            else if (historyProvider == "postgresql")
            {
                string connection = Environment.GetEnvironmentVariable("HISTORY_POSTGRES_CONNECTION")
                    ?? throw new InvalidOperationException(
                        "HISTORY_POSTGRES_CONNECTION is required for the postgresql provider");
                store = new PostgresHistoryStore(connection, instanceId, new TornWriteInjector(), snapshotInterval, encryption);
            }
            else if (historyProvider == "sqlite")
            {
                store = new SqliteHistoryStore(stateDir, new TornWriteInjector(), snapshotInterval, encryption);
            }
            else
            {
                store = new FileHistoryStore(stateDir, new TornWriteInjector(), snapshotInterval, encryption);
            }
            if (int.TryParse(Environment.GetEnvironmentVariable("CRASH_SEED"), out int crashSeed))
            {
                // Seeded randomized kill scheduling: this run's kill point
                // is a pure function of (seed, run index), printed before
                // it fires, so any failing seed reproduces exactly.
                int crashRun = int.TryParse(Environment.GetEnvironmentVariable("CRASH_RUN"), out var cr) ? cr : 0;
                store = new SeededKillStore(store, crashSeed, crashRun);
            }
            if (args.Contains("send-signal"))
            {
                // An external sender, in its own process, while no host
                // runs: the signal goes straight into history through the
                // store (the single-writer lock is free). It will be
                // delivered from history when the workflow next waits.
                store.Load();
                store.Append(HistoryEvent.SignalReceived(instanceId, "approve", amount));
                (store as IDisposable)?.Dispose();
                Console.WriteLine($"[signal] recorded 'approve' (payload {amount}) in history");
                return 0;
            }

            using var host = new WorkflowHost(store, stateDir);

            if (holdSeconds > 0)
            {
                Console.WriteLine($"[lock] holding single-writer lock for {holdSeconds}s");
                Thread.Sleep(TimeSpan.FromSeconds(holdSeconds));
                return 0;
            }

            var counters = Counters.Load(stateDir);
            var gateway = StubGateway.Load(stateDir, gatewayMode, counters);

            if (args.Contains("reconcile"))
                return Reconcile(stateDir, counters, gateway);

            if (_crashMode == "timer-kill-while-waiting")
            {
                // The timer scenario's kill point is inside the engine's
                // wait, where no workflow code runs to fire it — a watchdog
                // stands in for the machine dying mid-wait. The kill is
                // keyed to the workflow's start mark (appended immediately
                // before the timer is scheduled) plus a short margin, NOT
                // to a fixed delay after process start: provider startup
                // times differ, and a wall-clock kill can land before the
                // timer is even scheduled, which is a different scenario.
                _ = Task.Run(async () =>
                {
                    string mark = Path.Combine(stateDir, "timer.log");
                    while (!File.Exists(mark))
                        await Task.Delay(25);
                    await Task.Delay(250);
                    HardKill("timer: kill while the workflow waits on a durable timer");
                });
            }

            return await RunWorkflow(host, counters, gateway, variant, instanceId, amount, loopItems);
        }
        catch (HistoryCorruptionException e)
        {
            Console.WriteLine($"[history] CORRUPT HISTORY: {e.Message} — refusing to recover silently");
            return 3;
        }
        catch (NonDeterminismViolationException e)
        {
            Console.WriteLine($"[replay] NONDETERMINISM VIOLATION: {e.Message} — halting");
            return 4;
        }
        catch (VersionGateException e)
        {
            Console.WriteLine($"[version] VERSION GATE REFUSAL: {e.Message}");
            return 7;
        }
        catch (StateOwnedByAnotherHostException e)
        {
            Console.WriteLine($"[lock] {e.Message}");
            return 5;
        }
        catch (AuxStateException e)
        {
            Console.WriteLine($"[state] CORRUPT AUX STATE: {e.Message} — exit 6");
            return 6;
        }
        catch (IdempotencyKeyConflictException e)
        {
            Console.WriteLine($"[gateway] IDEMPOTENCY KEY CONFLICT: {e.Message} — refusing, exit 6");
            return 6;
        }
    }

    private static void HardKill(string where)
    {
        Console.WriteLine($"[CRASH] hard kill injected: {where}");
        Console.Out.Flush();
        System.Diagnostics.Process.GetCurrentProcess().Kill();
    }

    /// <summary>
    /// The seeded random killer: wraps the run's store and hard-kills the
    /// process immediately before the Nth append, where N is drawn from
    /// <c>Random(seed * 31 + runIndex)</c> in [1..6] — a pure function of
    /// the run's env, so the kill schedule of any seed is reproducible
    /// exactly by re-running the same (seed, run) pair. Kills land at
    /// append boundaries; the mid-write tear is scenario D's injector.
    /// The seed and run are printed on the kill line before the process
    /// dies, so a red run names its own reproduction.
    /// </summary>
    private sealed class SeededKillStore : IHistoryStore, IHistorySnapshotStore, IHistoryKeyRetention, IDisposable
    {
        private readonly IHistoryStore _inner;
        private readonly int _seed;
        private readonly int _run;
        private readonly int _killBeforeAppend;
        private int _appends;

        public SeededKillStore(IHistoryStore inner, int seed, int run)
        {
            _inner = inner;
            _seed = seed;
            _run = run;
            _killBeforeAppend = new Random(unchecked(seed * 31 + run)).Next(1, 7);
        }

        public HistoryLoadResult Load() => _inner.Load();

        public HistorySnapshotLoadResult LoadFromSnapshot() =>
            ((IHistorySnapshotStore)_inner).LoadFromSnapshot();

        public HistoryEvent Append(HistoryEvent historyEvent)
        {
            int n = ++_appends;
            if (n == _killBeforeAppend)
            {
                Console.WriteLine(
                    $"[CRASH] seeded kill: seed={_seed} run={_run} — hard kill before append #{n} ({historyEvent.Type})");
                Console.Out.Flush();
                System.Diagnostics.Process.GetCurrentProcess().Kill();
            }
            return _inner.Append(historyEvent);
        }

        public IReadOnlyDictionary<long, string> RecordedKeys => ((IHistoryKeyRetention)_inner).RecordedKeys;
        public DateTimeOffset? CompletedAtUtc => ((IHistoryKeyRetention)_inner).CompletedAtUtc;
        public bool KeyOnRecord(string idempotencyKey) => ((IHistoryKeyRetention)_inner).KeyOnRecord(idempotencyKey);
        public int PurgeExpiredKeys(DateTimeOffset utcNow) => ((IHistoryKeyRetention)_inner).PurgeExpiredKeys(utcNow);

        public void Dispose() => (_inner as IDisposable)?.Dispose();
    }

    /// <summary>Tears the write of the first command's completion, then dies — the §7.1 mid-write kill point.</summary>
    private sealed class TornWriteInjector : IHistoryFaultInjector
    {
        public bool ShouldTear(HistoryEvent historyEvent) =>
            _crashMode == "during-write" &&
            historyEvent.Type == HistoryEventTypes.ActivityCompleted &&
            historyEvent.CommandSequence == 1;

        public void OnTornWrite() =>
            HardKill("during write of ActivityCompleted(1) — unterminated record left on disk");
    }

    private static async Task<int> RunWorkflow(
        WorkflowHost host, Counters counters, StubGateway gateway,
        string variant, string instanceId, int amount, string[] loopItems)
    {
        string definitionId = variant switch { "v2" => "order/v2", "loop" => "loop/v1", "fanout" => "fanout/v1", "retry" => "retry/v1", "timer" => "timer/v1", "signal" => "signal/v1", _ => "order/v1" };
        object workflowInput = variant == "loop" ? new { source = "static-list" } : new { amount };

        Console.WriteLine($"[run] workflow starting/replaying (variant={variant}, gateway={(gateway.IsIdempotent ? "idempotent" : "nonidempotent")})...");

        WorkflowRunOutcome outcome;
        if (variant == "loop")
        {
            outcome = await host.RunAsync(definitionId, instanceId, workflowInput, async ctx =>
            {
                var items = ItemsStore.Load(CurrentStateDir());
                int call = 0;
                foreach (string item in loopItems)
                {
                    call++;
                    await Call(ctx, "ProcessItem", new { item }, async actx =>
                    {
                        counters.Bump("processExecutions");
                        if (items.TryGet(actx.IdempotencyKey, out var previous))
                        {
                            Console.WriteLine($"[items] key already applied ('{previous}') -> effect NOT duplicated");
                        }
                        else
                        {
                            items.Apply(actx.IdempotencyKey, item);
                            Console.WriteLine($"[items] applied '{item}' under key {actx.IdempotencyKey[..16]}...");
                        }
                        if (_crashMode == $"in-item-window:{actx.CommandSequence}")
                            HardKill($"inside ProcessItem({item}), after effect, before recording");
                        await Task.CompletedTask;
                        return "done-" + item;
                    });
                    _ = call;
                }
                Console.WriteLine($"[run] WORKFLOW COMPLETED (loop): processExecutions={counters.Get("processExecutions")}, distinctEffects={items.Items.Count}");
                return null;
            });
        }
        else if (variant == "retry")
        {
            outcome = await host.RunAsync(definitionId, instanceId, workflowInput, async ctx =>
            {
                string txn = await ctx.CallActivityAsync<string>("ChargeCard", new { amount }, async actx =>
                {
                    counters.Bump("retryExecutions");
                    File.AppendAllText(
                        Path.Combine(CurrentStateDir(), "attempts.log"),
                        $"{actx.Attempt}:{actx.IdempotencyKey}\n");
                    // THE BETWEEN-ATTEMPTS KILL POINT: attempt 1's failure
                    // is durable, attempt 2 has started, its own failure is
                    // not yet recorded — the host dies inside the retry loop.
                    if (_crashMode == "retry-kill-on-attempt-2" && actx.Attempt == 2)
                        HardKill("retry: kill between attempts (attempt 1 failed durably, attempt 2 in flight)");
                    if (actx.Attempt < 3)
                        throw new InvalidOperationException($"transient gateway fault (attempt {actx.Attempt})");
                    await Task.CompletedTask;
                    return gateway.Charge(actx.IdempotencyKey, amount, $"{instanceId}-retry");
                }, new RetryPolicy(3, TimeSpan.FromMilliseconds(50)));
                Console.WriteLine($"[run] WORKFLOW COMPLETED (retry): retryExecutions={counters.Get("retryExecutions")}, " +
                                  $"chargesApplied={counters.Get("chargesApplied")}");
                return txn;
            });
        }
        else if (variant == "timer")
        {
            outcome = await host.RunAsync(definitionId, instanceId, workflowInput, async ctx =>
            {
                // Wall-clock marks, appended per run: the scenario asserts
                // the fire time against the FIRST run's start (the recorded
                // due time), not against the resume's start.
                AppendTimerMark("start");
                await ctx.DelayAsync(TimeSpan.FromMilliseconds(3000));
                AppendTimerMark("fired");
                string txn = await Call(ctx, "ChargeCard", new { amount }, ChargeCard(counters, gateway, amount, instanceId));
                Console.WriteLine($"[run] WORKFLOW COMPLETED (timer): chargeExecutions={counters.Get("chargeExecutions")}, " +
                                  $"chargesApplied={counters.Get("chargesApplied")}");
                return txn;
            });
        }
        else if (variant == "signal")
        {
            outcome = await host.RunAsync(definitionId, instanceId, workflowInput, async ctx =>
            {
                Console.WriteLine("[signal] waiting for 'approve'");
                Console.Out.Flush();
                int approvedAmount = await ctx.WaitForSignalAsync<int>("approve");
                string txn = await Call(ctx, "ChargeCard", new { amount = approvedAmount },
                    ChargeCard(counters, gateway, approvedAmount, instanceId));
                Console.WriteLine($"[run] WORKFLOW COMPLETED (signal): approvedAmount={approvedAmount}, " +
                                  $"chargeExecutions={counters.Get("chargeExecutions")}, chargesApplied={counters.Get("chargesApplied")}");
                return txn;
            });
        }
        else if (variant == "fanout")
        {
            int branch2Amount = int.TryParse(Environment.GetEnvironmentVariable("FAN_BRANCH2_AMOUNT"), out var b2)
                ? b2 : amount + 100;
            int[] branchAmounts = { amount, branch2Amount, amount + 200 };
            // Branch timing is scenario scaffolding only: it stages which
            // branches are in flight when a crash point fires. It is not
            // workflow structure — activities, inputs and order are the same
            // in every mode; only the clock differs. The kill-mid margins
            // are wide on purpose: the staging claims branch 1's completion
            // is durable before branch 2's kill fires, and on a loaded
            // machine (the suite runs providers in parallel) a short margin
            // lets the kill catch branch 1 still in flight — the engine
            // then correctly re-executes it, and the scenario's strict
            // per-branch execution counts no longer describe the staging.
            int[] branchDelays = _crashMode switch
            {
                "fanout-kill-mid" => new[] { 0, 1500, 5000 },
                "fanout-kill-early" => new[] { 400, 250, 2000 },
                _ => new[] { 0, 0, 0 },
            };

            outcome = await host.RunAsync(definitionId, instanceId, workflowInput, async ctx =>
            {
                var calls = new List<ActivityCall<string>>();
                for (int i = 1; i <= 3; i++)
                {
                    int branch = i;
                    calls.Add(new ActivityCall<string>("ChargeCard", new { amount = branchAmounts[branch - 1] },
                        async actx =>
                        {
                            if (branchDelays[branch - 1] > 0)
                                await Task.Delay(branchDelays[branch - 1]);
                            if (_crashMode == "fanout-kill-early" && branch == 2)
                                HardKill("fan-out: kill with every branch in flight, none completed");
                            string txn;
                            lock (FanGate)
                            {
                                counters.Bump($"fanExecutions{branch}");
                                txn = gateway.Charge(actx.IdempotencyKey, branchAmounts[branch - 1], $"{instanceId}-fan{branch}");
                            }
                            // THE WINDOW, per branch (§8.1): this branch's
                            // charge is real, but its result is not in
                            // history yet.
                            if (_crashMode == "fanout-kill-mid" && branch == 2)
                                HardKill("fan-out: kill inside branch 2, after its charge, before its recording");
                            return txn;
                        }));
                }
                string[] results = await ctx.CallAllAsync(calls);
                if (_crashMode == "fanout-after-join")
                    HardKill("fan-out: kill after the join, before workflow completion is recorded");
                return string.Join(",", results);
            });
            if (outcome == WorkflowRunOutcome.Completed)
                Console.WriteLine($"[run] WORKFLOW COMPLETED (fanout): fanExecutions=" +
                    $"{counters.Get("fanExecutions1")}/{counters.Get("fanExecutions2")}/{counters.Get("fanExecutions3")}, " +
                    $"chargesApplied={counters.Get("chargesApplied")}");
        }
        else
        {
            string charge = "";
            Func<WorkflowContext, Task<object?>> body = async ctx =>
            {
                if (variant == "v2")
                {
                    await Call(ctx, "ReserveStock", new { orderId = instanceId }, ReserveStock(counters));
                    charge = await Call(ctx, "ChargeCard", new { amount }, ChargeCard(counters, gateway, amount, instanceId));
                }
                else if (variant == "versioned" || variant == "versioned-capped")
                {
                    // The P4 shape: same definition id (order/v1), code
                    // changed under a gate. The gate sits after the first
                    // command; from version 2 on, ReserveStock is called
                    // with a priority input the old code never sent.
                    charge = await Call(ctx, "ChargeCard", new { amount }, ChargeCard(counters, gateway, amount, instanceId));
                    int version = variant == "versioned"
                        ? ctx.GetVersion("reserve-v2", 1, 2)
                        : ctx.GetVersion("reserve-v2", 1, 1);
                    Console.WriteLine($"[version] gate 'reserve-v2' resolved to version {version}");
                    object reserveInput = variant == "versioned" && version >= 2
                        ? new { orderId = instanceId, priority = "high" }
                        : new { orderId = instanceId };
                    await Call(ctx, "ReserveStock", reserveInput, ReserveStock(counters));
                }
                else if (variant == "versioned-early")
                {
                    // The gate was inserted BEFORE the first command and the
                    // body ignores it (the pre-gate code path): over history
                    // recorded by ungated code it must resolve to the
                    // default version and record nothing.
                    int version = ctx.GetVersion("reserve-v2", 1, 2);
                    Console.WriteLine($"[version] gate 'reserve-v2' resolved to version {version}");
                    charge = await Call(ctx, "ChargeCard", new { amount }, ChargeCard(counters, gateway, amount, instanceId));
                    await Call(ctx, "ReserveStock", new { orderId = instanceId }, ReserveStock(counters));
                }
                else if (variant == "mutated")
                {
                    // The ungated change: same definition id, but the
                    // recorded first command is silently swapped. Replay
                    // must refuse it as a determinism violation.
                    await Call(ctx, "ReserveStock", new { orderId = instanceId }, ReserveStock(counters));
                    charge = await Call(ctx, "ChargeCard", new { amount }, ChargeCard(counters, gateway, amount, instanceId));
                }
                else
                {
                    charge = await Call(ctx, "ChargeCard", new { amount }, ChargeCard(counters, gateway, amount, instanceId));
                    await Call(ctx, "ReserveStock", new { orderId = instanceId }, ReserveStock(counters));
                }
                return charge;
            };
            outcome = await host.RunAsync(definitionId, instanceId, workflowInput, body);
            if (outcome == WorkflowRunOutcome.Completed)
            {
                Console.WriteLine($"[run] WORKFLOW COMPLETED: charge={charge}");
                Console.WriteLine($"[run] counters: chargeExecutions={counters.Get("chargeExecutions")}, " +
                                  $"chargesApplied={counters.Get("chargesApplied")}, reserveExecutions={counters.Get("reserveExecutions")}, " +
                                  $"refundsApplied={counters.Get("refundsApplied")}");
            }
        }

        if (host.LastSnapshotUsed)
            Console.WriteLine($"[history] snapshot used through {host.LastSnapshotThroughN}; tail replayed from there");
        if (host.LastLoad?.TornTailTruncated == true)
            Console.WriteLine("[history] torn tail write truncated; unterminated record discarded (never committed)");
        if (outcome == WorkflowRunOutcome.AlreadyCompleted)
            Console.WriteLine("[run] workflow already completed in history — nothing to do.");
        return 0;
    }

    /// <summary>
    /// One durable call plus the after-completion kill point: the crash lands
    /// after the completion record is durable, before the workflow proceeds —
    /// the checkpoint-boundary kill of §7.1.
    /// </summary>
    private static async Task<T> Call<T>(
        WorkflowContext ctx, string activity, object? input, Func<ActivityContext, Task<T>> body)
    {
        // The command sequence of this call is the count of prior calls + 1;
        // the context assigns the same sequence in program order.
        long seq = CallSequence.Next();
        T result = await ctx.CallActivityAsync(activity, input, body);
        if (_crashMode == $"after-complete:{seq}")
            HardKill($"after ActivityCompleted({seq}) became durable");
        return result;
    }

    private static class CallSequence
    {
        private static long _next;
        public static long Next() => ++_next;
    }

    private static void AppendTimerMark(string mark) =>
        File.AppendAllText(
            Path.Combine(CurrentStateDir(), "timer.log"),
            $"{mark} {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}\n");

    private static Func<ActivityContext, Task<string>> ChargeCard(
        Counters counters, StubGateway gateway, int amount, string instanceId) =>
        async ctx =>
        {
            counters.Bump("chargeExecutions");
            string txn = gateway.Charge(ctx.IdempotencyKey, amount, $"{instanceId}-cmd{ctx.CommandSequence}");
            // THE WINDOW (§8.1): the charge is real, but no result is in history yet.
            if (_crashMode == "in-charge-window")
                HardKill("inside ChargeCard, after charge, before recording");
            await Task.CompletedTask;
            return txn;
        };

    private static Func<ActivityContext, Task<string>> ReserveStock(Counters counters) =>
        async ctx =>
        {
            counters.Bump("reserveExecutions");
            await Task.CompletedTask;
            return "reserved";
        };

    private static string CurrentStateDir() =>
        Environment.GetEnvironmentVariable("PROOF_STATE") ?? "./p0-state";

    /// <summary>
    /// Reconciliation: find duplicate charges for one logical purchase
    /// (grouped by merchant reference + amount) and compensate each extra with
    /// a real refund through the gateway, under a refund key derived from the
    /// charge — so reconciliation itself is crash-safe and resumable: killed
    /// after a refund, re-running it never refunds twice.
    /// </summary>
    private static int Reconcile(string stateDir, Counters counters, StubGateway gateway)
    {
        int refundedNow = 0;
        foreach (var group in gateway.State.Charges.GroupBy(c => (c.Ref, c.Amount)))
        {
            var list = group.ToList();
            if (list.Count <= 1) continue;
            Console.WriteLine($"[reconcile] merchantRef {group.Key.Ref}, amount {group.Key.Amount}: {list.Count} charges found for ONE logical purchase");
            foreach (var extra in list.Skip(1))
            {
                if (gateway.State.Refunds.Any(r => r.ForTxn == extra.Txn)) continue; // already compensated
                string refundKey = StubGateway.RefundKeyFor(extra.Ref, extra.Txn);
                gateway.Refund(extra.Txn, extra.Amount, refundKey, extra.Ref);
                refundedNow++;
                Console.WriteLine($"[reconcile] duplicate {extra.Txn} refunded via gateway (compensation — the duplicate DID happen; it was not prevented)");
                if (_crashMode == "reconcile-after-refund")
                    HardKill("inside reconcile, after gateway refund, before recording");
            }
        }
        int totalDuplicates = gateway.State.Charges
            .GroupBy(c => (c.Ref, c.Amount)).Sum(g => Math.Max(0, g.Count() - 1));
        counters.Set("refundsApplied", gateway.State.Refunds.Count);
        AtomicFile.WriteAllText(
            Path.Combine(stateDir, "reconciliation.json"),
            System.Text.Json.JsonSerializer.Serialize(new { duplicatesRefunded = totalDuplicates }));
        Console.WriteLine(refundedNow == 0 && totalDuplicates == 0
            ? "[reconcile] no duplicates found"
            : $"[reconcile] done: {totalDuplicates} duplicate(s) compensated in total");
        return 0;
    }
}
