namespace SRankSentinel;

[Serializable]
public sealed record HuntHistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AlertKey { get; set; } = string.Empty;
    public string CreatureName { get; set; } = string.Empty;
    public string World { get; set; } = string.Empty;
    public uint TerritoryId { get; set; }
    public int Instance { get; set; }
    public string Rank { get; set; } = "S";
    public string Source { get; set; } = string.Empty;
    // Feed receipt times, not an invented exact server spawn time.
    public DateTime ReportedAtUtc { get; set; }
    public DateTime? TaggedAtUtc { get; set; }
    public DateTime? KilledAtUtc { get; set; }
    public DateTime? CreditedAtUtc { get; set; }
    public string Reward { get; set; } = string.Empty;
}

/// <summary>Passive bounded history. No queue, targeting, or travel decisions depend on it.</summary>
internal sealed class HuntHistoryJournal(Configuration config)
{
    internal const int Capacity = 500;
    internal static readonly TimeSpan RewardWindow = TimeSpan.FromSeconds(20);
    private readonly object sync = new();
    // Unconfirmed credit is deliberately session-local: reloading cannot turn an old tag into credit.
    private readonly HashSet<string> taggedThisSession = [];
    private readonly HashSet<string> completedTaggedPulls = [];
    private readonly Dictionary<string, (DateTime At, string Reward)> pendingRewards = [];
    // Clearing the visible list must not discard a tag/receipt still awaiting its kill or reward.
    private readonly List<HuntHistoryEntry> clearedActiveSpawns = [];
    private IEnumerable<HuntHistoryEntry> CreditCandidates => (config.SpawnHistory ?? []).Concat(clearedActiveSpawns);

    internal bool Clear(bool credited)
    {
        lock (sync)
        {
            var entries = credited ? config.CreditedHistory : config.SpawnHistory;
            if (entries is null || entries.Count == 0) return false;
            if (!credited)
                clearedActiveSpawns.AddRange(entries.Where(entry => entry.CreditedAtUtc is null && taggedThisSession.Contains(entry.Id)));
            entries.Clear();
            Trim();
            return true;
        }
    }

    internal (HuntHistoryEntry[] Spawns, HuntHistoryEntry[] Credits) Snapshot()
    {
        lock (sync)
            return ((config.SpawnHistory ?? []).OrderByDescending(x => x.ReportedAtUtc).Select(x => x with { }).ToArray(),
                (config.CreditedHistory ?? []).OrderByDescending(x => x.CreditedAtUtc).Select(x => x with { }).ToArray());
    }

    internal bool Spawn(HuntAlertSnapshot alert, string source)
    {
        lock (sync)
        {
            config.SpawnHistory ??= [];
            var existing = Find(alert);
            if (existing is not null) return false;
            config.SpawnHistory.Add(new()
            {
                AlertKey = alert.Key, CreatureName = alert.CreatureName, World = alert.World,
                TerritoryId = alert.TerritoryId, Instance = alert.Instance,
                Rank = alert.HuntType == "ssrank" ? "SS" : "S", Source = source,
                ReportedAtUtc = alert.ReceivedAtUtc,
            });
            Trim();
            return true;
        }
    }

    internal bool Tag(HuntAlertSnapshot alert, DateTime now)
    {
        lock (sync)
        {
            Spawn(alert, "Local observation");
            var entry = Find(alert)!;
            taggedThisSession.Add(entry.Id);
            pendingRewards.Remove(entry.Id);
            entry.TaggedAtUtc = now;
            return true;
        }
    }

    internal bool Kill(HuntAlertSnapshot alert, bool taggedFinalPull, DateTime now)
    {
        lock (sync)
        {
            Spawn(alert, "Local observation");
            var entry = Find(alert)!;
            entry.KilledAtUtc ??= now;
            if (taggedFinalPull && taggedThisSession.Contains(entry.Id))
                completedTaggedPulls.Add(entry.Id);
            else
            {
                completedTaggedPulls.Remove(entry.Id);
                pendingRewards.Remove(entry.Id);
            }
            TryCredit(entry, now);
            return true;
        }
    }

    internal bool Reward(string world, uint territory, int instance, HuntAlertSnapshot? current,
        bool currentPullTagged, DateTime now, string reward)
    {
        lock (sync)
        {
            var candidates = CreditCandidates.Where(entry =>
                entry.CreditedAtUtc is null && taggedThisSession.Contains(entry.Id) &&
                entry.World.Equals(world, StringComparison.OrdinalIgnoreCase) &&
                entry.TerritoryId == territory && entry.Instance == Math.Max(1, instance) &&
                (entry.KilledAtUtc is { } killed && now >= killed && now - killed <= RewardWindow &&
                    completedTaggedPulls.Contains(entry.Id) ||
                 entry.KilledAtUtc is null && currentPullTagged && current is not null &&
                    Find(current)?.Id == entry.Id)).ToArray();
            // Do not guess which hunt earned a generic currency line if the context is ambiguous.
            if (candidates.Length != 1) return false;
            var entry = candidates[0];
            pendingRewards[entry.Id] = (now, reward);
            return TryCredit(entry, now);
        }
    }

    private bool TryCredit(HuntHistoryEntry entry, DateTime now)
    {
        if (entry.CreditedAtUtc is not null || entry.KilledAtUtc is not { } killed ||
            !completedTaggedPulls.Contains(entry.Id) || !pendingRewards.TryGetValue(entry.Id, out var receipt) ||
            (killed - receipt.At).Duration() > RewardWindow || now - receipt.At > RewardWindow)
            return false;
        entry.CreditedAtUtc = receipt.At;
        entry.Reward = receipt.Reward;
        config.CreditedHistory ??= [];
        config.CreditedHistory.Add(entry with { });
        pendingRewards.Remove(entry.Id);
        Trim();
        return true;
    }

    private HuntHistoryEntry? Find(HuntAlertSnapshot alert) => CreditCandidates
        .LastOrDefault(entry => entry.AlertKey == alert.Key &&
            (entry.ReportedAtUtc - alert.ReceivedAtUtc).Duration() < TimeSpan.FromHours(2));

    private void Trim()
    {
        if (config.SpawnHistory is { Count: > Capacity })
            config.SpawnHistory.RemoveRange(0, config.SpawnHistory.Count - Capacity);
        if (config.CreditedHistory is { Count: > Capacity })
            config.CreditedHistory.RemoveRange(0, config.CreditedHistory.Count - Capacity);
        if (clearedActiveSpawns.Count > Capacity)
            clearedActiveSpawns.RemoveRange(0, clearedActiveSpawns.Count - Capacity);
        var retained = CreditCandidates.Select(entry => entry.Id).ToHashSet();
        taggedThisSession.IntersectWith(retained);
        completedTaggedPulls.IntersectWith(retained);
        foreach (var key in pendingRewards.Keys.Where(key => !retained.Contains(key)).ToArray())
            pendingRewards.Remove(key);
    }
}
