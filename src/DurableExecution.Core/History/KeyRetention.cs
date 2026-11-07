using System.Text.Json.Nodes;

namespace DurableExecution.History;

/// <summary>
/// The §8.5 retention policy for idempotency keys: how long a completed
/// instance's keys stay on record after its completion before they become
/// purgeable. Configured per store; a store without a policy refuses
/// <see cref="IHistoryKeyRetention.PurgeExpiredKeys"/> rather than
/// guessing one — there is no default and no background sweeper.
/// </summary>
public sealed record KeyRetentionPolicy
{
    public KeyRetentionPolicy(TimeSpan retainAfterCompletion)
    {
        if (retainAfterCompletion < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retainAfterCompletion),
                "key retention cannot be negative");
        RetainAfterCompletion = retainAfterCompletion;
    }

    /// <summary>How long keys are retained after the instance's <c>WorkflowCompleted</c> timestamp.</summary>
    public TimeSpan RetainAfterCompletion { get; }
}

/// <summary>
/// The §8.5 key registry surface of a history store. What is stored
/// where, exactly:
///
/// <list type="bullet">
/// <item><b>History</b> (never purged): the sealed records.
/// <c>ActivityScheduled</c> records carry each command's idempotency
/// key; <c>WorkflowCompleted</c> carries the completion timestamp.</item>
/// <item><b>Key registry</b> (derived state, purgeable): per instance,
/// the map command-sequence → idempotency key for every scheduled
/// command, the instance's completion timestamp once completed, and a
/// purge marker. On the file provider it is a sealed
/// <c>history.keys.json</c> beside the log (envelope-encrypted when the
/// store has a cipher); on the database providers it is a pair of
/// tables beside the history tables (<c>HistoryKeys</c> +
/// <c>HistoryInstanceState</c>); on SQLite, the same tables inside
/// <c>history.db</c>. On database providers the registry holds the
/// derived key hashes themselves — one-way hashes of public inputs,
/// inside the same database trust boundary as the history.</item>
/// </list>
///
/// The registry is a pure fold of history: loading folds the recorded
/// events into it, and each append updates it in the same commit unit
/// as its record (the append's own transaction on database providers;
/// an atomic rewrite after the append on the file provider — a crash
/// between the two leaves the registry behind, and the next load folds
/// history forward into it again). The one fold exception is the purge
/// marker: once an instance's keys are purged, the fold never
/// resurrects them. That is safe because completion is terminal —
/// <c>WorkflowCompleted</c> is unique and last, so a purged instance
/// can never schedule another key — and it is why the marker is
/// authoritative state, sealed on the file provider like the rest.
///
/// Purging touches the registry only. The history stays byte-identical:
/// it still loads, validates, snapshots, and reports the instance
/// completed. What expires is the engine's guarantee surface — the
/// record a gateway or operator consults to deduplicate — not the
/// workflow's record of what happened.
/// </summary>
public interface IHistoryKeyRetention
{
    /// <summary>The keys currently on record for the instance, by command sequence.</summary>
    IReadOnlyDictionary<long, string> RecordedKeys { get; }

    /// <summary>The instance's recorded completion time, or null while it has not completed.</summary>
    DateTimeOffset? CompletedAtUtc { get; }

    /// <summary>True while this exact key is on record for the instance (the dedup lookup surface).</summary>
    bool KeyOnRecord(string idempotencyKey);

    /// <summary>
    /// The retention sweep: when the instance completed longer ago than
    /// the store's <see cref="KeyRetentionPolicy"/> allows (measured
    /// against <paramref name="utcNow"/>), deletes its registry keys,
    /// marks the registry purged, and returns how many keys were
    /// removed. An instance that has not completed, or whose retention
    /// has not elapsed, purges nothing and returns 0. History is never
    /// touched. This is an explicit, host-driven call — the engine runs
    /// no background thread and hides no sweep behaviour.
    /// </summary>
    /// <exception cref="InvalidOperationException">The store has no retention policy configured.</exception>
    int PurgeExpiredKeys(DateTimeOffset utcNow);
}

/// <summary>
/// The foldable registry state, shared by every provider: which keys are
/// on record, when the instance completed, and whether its keys were
/// purged. Pure in-memory logic; each provider persists it in its own
/// medium and commit unit.
/// </summary>
internal sealed class KeyRegistryState
{
    private readonly Dictionary<long, string> _keys = new();

    public IReadOnlyDictionary<long, string> Keys => _keys;

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public bool KeysPurged { get; private set; }

    /// <summary>Folds one validated history event into the registry. Returns true when the state changed.</summary>
    public bool Apply(HistoryEvent historyEvent)
    {
        switch (historyEvent.Type)
        {
            case HistoryEventTypes.ActivityScheduled:
                if (KeysPurged)
                    return false; // the purge marker is authoritative: folded history never resurrects keys
                long seq = historyEvent.CommandSequence!.Value;
                string key = historyEvent.GetString("idempotencyKey")!;
                if (_keys.TryGetValue(seq, out var existing) && existing == key)
                    return false;
                _keys[seq] = key;
                return true;

            case HistoryEventTypes.WorkflowCompleted:
                var completedAt = DateTimeOffset.Parse(historyEvent.GetString("ts")!);
                if (CompletedAtUtc == completedAt)
                    return false;
                CompletedAtUtc = completedAt;
                return true;

            default:
                return false;
        }
    }

    /// <summary>Restores persisted state (a store's load of its registry medium).</summary>
    public void Restore(IEnumerable<KeyValuePair<long, string>> keys, DateTimeOffset? completedAtUtc, bool keysPurged)
    {
        _keys.Clear();
        foreach (var kv in keys)
            _keys[kv.Key] = kv.Value;
        CompletedAtUtc = completedAtUtc;
        KeysPurged = keysPurged;
    }

    /// <summary>The purge decision: keys are purgeable only for a completed, unpurged instance past its retention.</summary>
    public bool IsPurgeable(DateTimeOffset utcNow, KeyRetentionPolicy policy) =>
        CompletedAtUtc is not null && !KeysPurged &&
        utcNow >= CompletedAtUtc.Value + policy.RetainAfterCompletion;

    /// <summary>Marks the purge done and clears the keys. Returns how many keys were removed.</summary>
    public int MarkPurged()
    {
        int count = _keys.Count;
        _keys.Clear();
        KeysPurged = true;
        return count;
    }
}
