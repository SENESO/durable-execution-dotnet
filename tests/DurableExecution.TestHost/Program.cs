using DurableExecution.Execution;
using DurableExecution.History;
using DurableExecution.IO;

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

        try
        {
            var store = new FileHistoryStore(stateDir, new TornWriteInjector());
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
        string definitionId = variant switch { "v2" => "order/v2", "loop" => "loop/v1", _ => "order/v1" };
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
