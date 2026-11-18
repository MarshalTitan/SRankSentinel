using SRankSentinel;

internal static class SsWatchTests
{
    private static readonly DateTime Start = new(2026, 11, 18, 8, 0, 0, DateTimeKind.Utc);
    private static HuntAlertSnapshot Opportunity => new("sswatch", "Faerie", "Arch Aethereater", 1189,
        1, 1, 1, 14, 34, Start);

    internal static void PrecursorStartsHardClock()
    {
        var watch = new SsWatchLifetime();
        Check(watch.Begin(Start, Start, 300), "first precursor must start the watch");
        Check(watch.StartedUtc == Start && watch.DeadlineUtc == Start.AddMinutes(5), "hard deadline must be five minutes from precursor");
        Check(watch.RemainingSeconds(Start) == 300, "initial countdown must be real");
    }

    internal static void AnnouncementCannotExtend()
    {
        var watch = Running();
        Check(!watch.Begin(Start.AddMinutes(2), Start.AddMinutes(2), 300), "announcement must not restart the watch");
        Check(watch.Repair(DateTime.MaxValue), "legacy infinite announcement deadline must be rejected");
        Check(watch.DeadlineUtc == Start.AddMinutes(5) && watch.RemainingSeconds(Start.AddMinutes(2)) == 180,
            "announcement must retain the original expiry");
    }

    internal static void RepeatedAnnouncementsCannotExtend()
    {
        var watch = Running();
        foreach (var seconds in new[] { 60, 120, 180, 240, 299, 360, 21600 })
        {
            watch.Begin(Start.AddSeconds(seconds), Start.AddSeconds(seconds), int.MaxValue);
            watch.Repair(Start.AddSeconds(seconds + 300));
            Check(watch.DeadlineUtc == Start.AddSeconds(300), "repeated reports cannot slide the deadline");
        }
        Check(watch.Decide(Start.AddHours(6), false, false) == SsWatchDecision.Expire, "six-hour announcement stall must be impossible");
    }

    internal static void QueuedPromotionKeepsOrigin()
    {
        var promoted = SsWatchLifetime.Announce(Opportunity);
        promoted = SsWatchLifetime.Announce(promoted);
        var restored = PersistedHuntAlert.From(promoted).ToSnapshot();
        Check(restored.ReceivedAtUtc == Start && restored.HuntType == "ssrank" && restored.Key == Opportunity.Key,
            "queued promotion/persistence must retain first reservation origin and identity");
        var watch = new SsWatchLifetime();
        watch.Begin(restored.ReceivedAtUtc, Start.AddMinutes(3), 300);
        Check(watch.RemainingSeconds(Start.AddMinutes(3)) == 120, "travel and queued announcements cannot grant a fresh five minutes");
    }

    internal static void LivingEntityLeavesPassiveWatch()
    {
        var watch = Running();
        Check(watch.Decide(Start.AddSeconds(299), true, false) == SsWatchDecision.TrackLivingEntity,
            "real living SS takes normal tracking before expiry");
        Check(watch.Decide(Start.AddSeconds(301), true, false) == SsWatchDecision.TrackLivingEntity,
            "positive living entity must still veto automatic abandonment at a timing boundary");
        Check(HuntProgressPolicy.DecideHuntExit(HuntExitRequestSource.RecoveryBudget, true, true, true) ==
            HuntExitDecision.BlockForVisibleLiveEntity, "normal living-entity protection must remain effective");
    }

    internal static void NoEntityExpiresWithoutKill()
    {
        var watch = Running();
        Check(watch.Decide(Start.AddSeconds(299), false, false) == SsWatchDecision.Wait, "opportunity retains its priority before expiry");
        Check(watch.Decide(Start.AddSeconds(300), false, false) == SsWatchDecision.Expire, "no report/object/kill must still release watch at five minutes");
    }

    internal static void AnnouncedFlagCannotBypassExpiry()
    {
        foreach (var announced in new[] { false, true })
        {
            var watch = Running();
            var snapshot = announced ? SsWatchLifetime.Announce(Opportunity) : Opportunity;
            watch.Begin(snapshot.ReceivedAtUtc, Start.AddSeconds(299), 300);
            Check(watch.Decide(Start.AddSeconds(300), false, false) == SsWatchDecision.Expire,
                "announced SS and precursor reservations must expire identically");
        }
    }

    internal static void ExactExternalDeathNeedsNoObject()
    {
        var now = Start.AddMinutes(2);
        foreach (var provider in new[] { "Sonar", "HuntAlerts" })
            Check(SsWatchLifetime.MatchesDeath(Opportunity, "Faerie", 1189, 1, now, now, true, false, false),
                provider + " fully matched death must clear an undetected SS reservation");
        Check(SsWatchLifetime.MatchesDeath(Opportunity, "Faerie", 0, 0, now, now, true, false, true),
            "Sonar's missing routing fields require positive physical context, not object detection");
        Check(Running().Decide(now, false, true) == SsWatchDecision.ConfirmVisibleDeath, "exact dead object is also positive completion evidence");
    }

    internal static void RejectWrongDeathIdentity()
    {
        var now = Start.AddMinutes(2);
        bool Match(string world = "Faerie", uint territory = 1189, int instance = 1,
            bool name = true, DateTime? when = null, bool physical = false) =>
            SsWatchLifetime.MatchesDeath(Opportunity, world, territory, instance, when ?? now, now, name, false, physical);
        Check(!Match(world: "Zalera") && !Match(territory: 1190) && !Match(instance: 2) && !Match(name: false),
            "wrong World/territory/instance/mark cannot clear the watched SS");
        Check(!Match(when: Start.AddMinutes(-2)) && !Match(when: now.AddMinutes(2)), "stale/future deaths must be rejected");
        Check(!Match(territory: 0, instance: 0) && !Match(world: ""), "missing identity needs physical context proof");
        Check(SsWatchLifetime.MatchesDeath(Opportunity, "Faerie", 1189, 2, now, now, true, true, false),
            "canonical live single instance permits a stale provider instance alias");
    }

    internal static void ConflictingLiveDeathIsRejected()
    {
        Check(HuntProgressPolicy.DecideHuntExit(HuntExitRequestSource.ExternalDeathEvidence, true, true, true) ==
            HuntExitDecision.BlockForVisibleLiveEntity, "a correctly routed provider death cannot overrule a visibly living SS");
        Check(Running().Decide(Start.AddSeconds(301), true, true) == SsWatchDecision.TrackLivingEntity,
            "living observation wins over conflicting death and expired clock");
    }

    internal static void InvalidDeadlineRepair()
    {
        foreach (var invalid in new[] { DateTime.MinValue, DateTime.MaxValue, Start.AddDays(1) })
        {
            var watch = Running();
            Check(watch.Repair(invalid) && watch.DeadlineUtc == Start.AddMinutes(5), "invalid legacy deadline repaired to original finite bound");
            Check(watch.Decide(Start.AddHours(6), false, false) == SsWatchDecision.Expire, "repair must not grant a new window to an old watch");
        }
        var fresh = new SsWatchLifetime();
        fresh.Begin(DateTime.MinValue, Start, int.MaxValue);
        fresh.Repair(DateTime.MaxValue);
        Check(fresh.DeadlineUtc == Start.AddMinutes(5), "missing anchor and corrupt duration still get a bounded repair");
    }

    internal static void WithdrawalCannotReopen()
    {
        var watch = Running();
        watch.Repair(Start.AddSeconds(80));
        watch.Begin(Start.AddSeconds(90), Start.AddSeconds(90), 300);
        watch.Repair(DateTime.MaxValue);
        Check(watch.DeadlineUtc == Start.AddSeconds(80) && watch.RemainingSeconds(Start.AddSeconds(90)) == 0,
            "announcement cannot reopen a withdrawn or shortened opportunity");
    }

    internal static void DuplicateCleanupIsContextBound()
    {
        var opportunity = Opportunity;
        var queued = new[] { opportunity, SsWatchLifetime.Announce(opportunity), opportunity with { World = "Zalera" },
            opportunity with { Instance = 2 }, opportunity with { CreatureName = "Ker", MarkDataId = 2 } };
        var survivors = queued.Where(alert => !SsWatchLifetime.SameContext(opportunity, alert,
            alert.CreatureName == opportunity.CreatureName, false)).ToArray();
        Check(survivors.Length == 3 && survivors.All(alert => alert.Key != opportunity.Key),
            "expiry removes reserved/promoted duplicates without removing another context");
    }

    private static SsWatchLifetime Running()
    {
        var watch = new SsWatchLifetime();
        watch.Begin(Start, Start, 300);
        return watch;
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
