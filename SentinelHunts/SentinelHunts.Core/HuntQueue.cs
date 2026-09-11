namespace SentinelHunts.Core;

public enum QueueMutation
{
    Added,
    Merged,
    RemovedByKill,
    IgnoredOldKill,
    IgnoredKilledReport,
}

public sealed class HuntQueue
{
    private readonly List<HuntQueueEntry> entries = [];
    private readonly Dictionary<HuntKey, DateTimeOffset> killedAt = [];

    public IReadOnlyList<HuntQueueEntry> Entries => entries;

    public QueueMutation Apply(HuntEvent huntEvent)
    {
        if (huntEvent.Kind == HuntEventKind.Killed)
            return ApplyKill(huntEvent);

        if (killedAt.TryGetValue(huntEvent.Key, out var death) && death >= huntEvent.ObservedAt)
            return QueueMutation.IgnoredKilledReport;

        var existing = entries.FirstOrDefault(entry => entry.Key == huntEvent.Key);
        if (existing is not null)
        {
            existing.Merge(huntEvent);
            return QueueMutation.Merged;
        }

        entries.Add(new HuntQueueEntry(huntEvent));
        return QueueMutation.Added;
    }

    public HuntQueueEntry? TakeNext(Func<HuntDefinition, bool> enabled, DateTimeOffset now, TimeSpan freshness)
    {
        Prune(enabled, now, freshness);
        if (entries.Count == 0)
            return null;

        var next = entries[0];
        entries.RemoveAt(0);
        return next;
    }

    public bool Remove(HuntKey key) => entries.RemoveAll(entry => entry.Key == key) > 0;

    public int Prune(Func<HuntDefinition, bool> enabled, DateTimeOffset now, TimeSpan freshness)
    {
        var removed = entries.RemoveAll(entry =>
        {
            var definition = HuntCatalog.FindByDataId(entry.Key.MarkDataId);
            return definition is null || !enabled(definition) || now - entry.LastObservedAt > freshness;
        });

        foreach (var key in killedAt.Where(pair => now - pair.Value > freshness).Select(pair => pair.Key).ToArray())
            killedAt.Remove(key);

        return removed;
    }

    public void Clear() => entries.Clear();

    private QueueMutation ApplyKill(HuntEvent huntEvent)
    {
        if (killedAt.TryGetValue(huntEvent.Key, out var knownDeath) && knownDeath > huntEvent.ObservedAt)
            return QueueMutation.IgnoredOldKill;

        killedAt[huntEvent.Key] = huntEvent.ObservedAt;
        var removed = entries.RemoveAll(entry =>
            entry.Key == huntEvent.Key && huntEvent.ObservedAt >= entry.FirstObservedAt);
        return removed > 0 ? QueueMutation.RemovedByKill : QueueMutation.IgnoredOldKill;
    }
}
