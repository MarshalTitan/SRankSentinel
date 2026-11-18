namespace SRankSentinel;

internal enum SsWatchDecision { Wait, TrackLivingEntity, ConfirmVisibleDeath, Expire }

// One precursor/reservation opportunity, independent of ordinary queued-alert freshness.
// Announcements update evidence, never the first accepted start or its hard upper bound.
internal sealed class SsWatchLifetime
{
    internal const int MaximumSeconds = 300;
    internal DateTime StartedUtc { get; private set; } = DateTime.MinValue;
    internal DateTime DeadlineUtc { get; private set; } = DateTime.MinValue;
    private DateTime hardDeadlineUtc = DateTime.MinValue;

    internal bool Begin(DateTime evidenceUtc, DateTime now, int configuredSeconds)
    {
        if (StartedUtc != DateTime.MinValue)
            return false;
        StartedUtc = IsTimestamp(evidenceUtc) && evidenceUtc <= now ? evidenceUtc : now;
        hardDeadlineUtc = StartedUtc.AddSeconds(Math.Clamp(configuredSeconds, 1, MaximumSeconds));
        DeadlineUtc = hardDeadlineUtc;
        return true;
    }

    internal bool Repair(DateTime observedDeadlineUtc)
    {
        if (StartedUtc == DateTime.MinValue)
            throw new InvalidOperationException("An SS watch needs a start before deadline validation.");
        var repaired = !IsTimestamp(observedDeadlineUtc) || observedDeadlineUtc > hardDeadlineUtc;
        // A valid earlier deadline (e.g. a withdrawn precursor chain) can shorten this watch.
        DeadlineUtc = IsTimestamp(observedDeadlineUtc)
            ? new(Math.Min(DeadlineUtc.Ticks, Math.Min(observedDeadlineUtc.Ticks, hardDeadlineUtc.Ticks)), DateTimeKind.Utc)
            : new(Math.Min(DeadlineUtc.Ticks, hardDeadlineUtc.Ticks), DateTimeKind.Utc);
        return repaired;
    }

    internal int RemainingSeconds(DateTime now) =>
        (int)Math.Clamp(Math.Ceiling((DeadlineUtc - now).TotalSeconds), 0, MaximumSeconds);

    internal SsWatchDecision Decide(DateTime now, bool exactLivingEntity, bool exactDeadEntity) =>
        exactLivingEntity ? SsWatchDecision.TrackLivingEntity :
        exactDeadEntity ? SsWatchDecision.ConfirmVisibleDeath :
        now >= DeadlineUtc ? SsWatchDecision.Expire : SsWatchDecision.Wait;

    internal void Reset() => StartedUtc = DeadlineUtc = hardDeadlineUtc = DateTime.MinValue;

    internal static bool IsTimestamp(DateTime value) => value != DateTime.MinValue && value != DateTime.MaxValue;

    internal static HuntAlertSnapshot Announce(HuntAlertSnapshot reservation) =>
        reservation with { HuntType = "ssrank" };

    internal static bool IsFresh(DateTime started, DateTime now, int seconds) =>
        IsTimestamp(started) && now < started.AddSeconds(Math.Clamp(seconds, 1, MaximumSeconds));

    internal static bool SameContext(HuntAlertSnapshot opportunity, HuntAlertSnapshot alert,
        bool sameMark, bool confirmedSingleInstance) =>
        sameMark && opportunity.World.Equals(alert.World, StringComparison.OrdinalIgnoreCase) &&
        opportunity.TerritoryId == alert.TerritoryId &&
        (confirmedSingleInstance || Math.Max(1, opportunity.Instance) == Math.Max(1, alert.Instance));

    internal static bool MatchesDeath(HuntAlertSnapshot opportunity, string world, uint territory, int instance,
        DateTime when, DateTime now, bool sameMark, bool confirmedSingleInstance, bool physicalContextProven) =>
        IsTimestamp(opportunity.ReceivedAtUtc) && IsTimestamp(when) && when <= now.AddMinutes(1) &&
        WorldVisitPolicy.MatchesDeath(opportunity, string.IsNullOrWhiteSpace(world) && physicalContextProven
                ? opportunity.World : world, territory, instance, when, sameMark, confirmedSingleInstance) &&
        ((!string.IsNullOrWhiteSpace(world) && territory != 0 && instance > 0) || physicalContextProven);
}
