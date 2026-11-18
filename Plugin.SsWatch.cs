namespace SRankSentinel;

public sealed partial class Plugin
{
    private readonly SsWatchLifetime ssWatchLifetime = new();
    private bool ssWatchExpiryReleased;
    private const string SsCountdownSeparator = " | SS watch: ";

    private void ResetSsWatchLifetime()
    {
        ssWatchLifetime.Reset();
        ssWatchExpiryReleased = false;
    }

    private void EnsureSsWatchDeadline(DateTime now, DateTime? evidenceUtc = null)
    {
        var hint = evidenceUtc ?? (current is not null && HuntCatalog.IsAnySsName(current.CreatureName)
            ? current.ReceivedAtUtc : stateSinceUtc);
        if (state == SentinelState.SsWatch && SsWatchLifetime.IsTimestamp(stateSinceUtc) && stateSinceUtc < hint)
            hint = stateSinceUtc;
        var began = ssWatchLifetime.Begin(hint, now, config.SsChainTimeoutSeconds);
        var repaired = ssWatchLifetime.Repair(ssWatchDeadlineUtc);
        ssWatchDeadlineUtc = ssWatchLifetime.DeadlineUtc;
        if (began || repaired)
            log.Information("SS hard watchdog {Action}: SS={Ss}, world={World}, territory={Territory}, instance={Instance}, start={Start:o}, deadline={Deadline:o}, remaining={Remaining}s; announcements and queue freshness cannot extend it",
                began ? "started" : "repaired invalid/unbounded deadline",
                activeSsProfile?.SsName ?? current?.CreatureName ?? "unknown", current?.World ?? "unknown",
                current?.TerritoryId ?? 0, current?.Instance ?? 0, ssWatchLifetime.StartedUtc,
                ssWatchDeadlineUtc, ssWatchLifetime.RemainingSeconds(now));
    }

    private bool TryBuildWatchedSs(out HuntAlertSnapshot opportunity)
    {
        opportunity = null!;
        if (current is null || activeSsProfile is not { } profile)
            return false;
        opportunity = CanonicalizeObservedInstance(new("sswatch", current.World, profile.SsName,
            current.TerritoryId, profile.SsDataId, current.PreferredAetheryteId, Math.Max(1, current.Instance),
            current.MapX, current.MapY, ssWatchLifetime.StartedUtc));
        return true;
    }

    private bool IsInSsOpportunityContext(HuntAlertSnapshot opportunity) =>
        !travel.IsBusy && clientState.TerritoryType == opportunity.TerritoryId &&
        travel.CurrentWorld.Equals(opportunity.World, StringComparison.OrdinalIgnoreCase) &&
        (IsObservedSingleInstance(opportunity.World, opportunity.TerritoryId) ||
         Math.Max(1, travel.CurrentInstance) == opportunity.Instance);

    private bool TickSsReservationWatchdog(DateTime now)
    {
        if (current is null || (ssWatchExpiryReleased && state != SentinelState.SsWatch) ||
            !(state == SentinelState.SsWatch || (!killConfirmed && !markEverIdentified &&
                HuntCatalog.IsAnySsName(current.CreatureName))))
            return false;
        activeSsProfile ??= HuntCatalog.GetSsProfileForSsName(current.CreatureName);
        EnsureSsWatchDeadline(now);
        if (!TryBuildWatchedSs(out var opportunity))
        {
            vnav.StopSafe("invalid SS watch context");
            ssWatchExpiryReleased = true;
            FailCurrent("SS reservation had no valid SS identity; releasing its watchdog safely");
            return true;
        }

        var visible = IsInSsOpportunityContext(opportunity)
            ? FindBattleNpc(opportunity.MarkDataId, opportunity.CreatureName) : null;
        var living = visible is { IsDead: false, CurrentHp: > 0 };
        var dead = visible is not null && (visible.IsDead || visible.CurrentHp == 0);
        switch (ssWatchLifetime.Decide(now, living, dead))
        {
            case SsWatchDecision.TrackLivingEntity:
                log.Information("SS entity positively found; leaving finite reservation for normal live tracking: {Ss} on {World}, object={Object}, HP={Hp}, remaining={Remaining}s",
                    opportunity.CreatureName, opportunity.World, visible!.GameObjectId,
                    visible.CurrentHp, ssWatchLifetime.RemainingSeconds(now));
                StartSsAlertDirect(opportunity with { HuntType = "ssrank" }, "positive SS watchdog entity scan");
                RemoveSsOpportunityDuplicates(opportunity);
                return true;
            case SsWatchDecision.ConfirmVisibleDeath:
                CompleteWatchedSsDeath(opportunity, "The exact SS object is visibly dead");
                return true;
            case SsWatchDecision.Expire:
                vnav.StopSafe("SS hard reservation deadline expired");
                ResetSsStagingTracking();
                var removed = RemoveSsOpportunityDuplicates(opportunity);
                ssWatchExpiryReleased = true;
                ClearPendingSsChainEvidence();
                nextActionUtc = now;
                log.Warning("SS reservation expired without a usable living entity: {Ss} on {World}, territory={Territory}, instance={Instance}, start={Start:o}, deadline={Deadline:o}, announced={Announced}, removedDuplicates={Removed}, remainingQueue={Queued}; returning to Ul'dah and normal queue selection",
                    opportunity.CreatureName, opportunity.World, opportunity.TerritoryId, opportunity.Instance,
                    ssWatchLifetime.StartedUtc, ssWatchDeadlineUtc, ssSpawnAnnounced, removed, pendingAlerts.Count);
                FailCurrent($"{opportunity.CreatureName} SS reservation expired at its hard deadline; no living entity was detected",
                    HuntExitRequestSource.RecoveryBudget);
                return true;
            default:
                return false;
        }
    }

    private int RemoveSsOpportunityDuplicates(HuntAlertSnapshot opportunity)
    {
        var single = IsObservedSingleInstance(opportunity.World, opportunity.TerritoryId);
        bool Matches(HuntAlertSnapshot alert) => SsWatchLifetime.SameContext(opportunity, alert,
            HuntCatalog.NamesMatch(opportunity.CreatureName, alert.CreatureName), single);
        var removed = pendingAlerts.Where(Matches).ToArray();
        var survivors = pendingAlerts.Where(alert => !Matches(alert)).ToArray();
        pendingAlerts.Clear();
        foreach (var survivor in survivors)
            pendingAlerts.Enqueue(survivor);
        foreach (var alert in removed)
            faloopReportIdsByAlertKey.Remove(alert.Key);
        var unresolved = unresolvedFaloopAlerts.Where(pair => Matches(pair.Value.Alert)).Select(pair => pair.Key).ToArray();
        foreach (var key in unresolved)
        {
            unresolvedFaloopAlerts.Remove(key);
            faloopReportIdsByAlertKey.Remove(key);
        }
        PersistQueue();
        return removed.Length + unresolved.Length;
    }

    private bool IgnoreExpiredSsWatchReport(HuntAlertSnapshot alert) =>
        ssWatchExpiryReleased && TryBuildWatchedSs(out var expired) &&
        SsWatchLifetime.SameContext(expired, alert,
            HuntCatalog.NamesMatch(expired.CreatureName, alert.CreatureName),
            IsObservedSingleInstance(expired.World, expired.TerritoryId));

    private bool TryConfirmWatchedSsDeath(string world, uint territory, int instance, DateTime when,
        bool sameSsName, string source)
    {
        if (state != SentinelState.SsWatch || !sameSsName || current is null || activeSsProfile is null)
            return false;
        var now = DateTime.UtcNow;
        EnsureSsWatchDeadline(now);
        if (!TryBuildWatchedSs(out var opportunity) || !SsWatchLifetime.MatchesDeath(opportunity,
                world, territory, instance, when, now, sameSsName,
                IsObservedSingleInstance(opportunity.World, opportunity.TerritoryId), IsInSsOpportunityContext(opportunity)))
            return false;

        var visible = IsInSsOpportunityContext(opportunity)
            ? FindBattleNpc(opportunity.MarkDataId, opportunity.CreatureName) : null;
        if (visible is { IsDead: false, CurrentHp: > 0 })
        {
            log.Warning("Rejected conflicting {Source} SS death: exact {Ss} on {World} is visibly alive, object={Object}, HP={Hp}",
                source, opportunity.CreatureName, opportunity.World, visible.GameObjectId, visible.CurrentHp);
            StartSsAlertDirect(opportunity with { HuntType = "ssrank" }, "visible SS vetoed provider death");
            return true;
        }

        CompleteWatchedSsDeath(opportunity, $"{source} positively matched {opportunity.CreatureName} on {opportunity.World} as killed during SS watch");
        return true;
    }

    private void CompleteWatchedSsDeath(HuntAlertSnapshot opportunity, string reason)
    {
        vnav.StopSafe("positive watched SS death");
        ResetSsStagingTracking();
        RemoveSsOpportunityDuplicates(opportunity);
        // The current identity may still be the completed normal S. Adopt the SS with the
        // existing reset routine so its History cannot inherit the normal S's confirmed tag.
        StartAlert(opportunity with { HuntType = "ssrank" }, "positive SS reservation death identity");
        ConfirmKill(reason); // Shared final visible-live veto and History/queue bookkeeping.
        if (killConfirmed)
        {
            activeSsProfile = null;
            ResetSsWatchLifetime();
        }
    }

    private void AppendSsWatchCountdown(DateTime now)
    {
        var suffixAt = status.IndexOf(SsCountdownSeparator, StringComparison.Ordinal);
        if (suffixAt >= 0)
            status = status[..suffixAt];
        status += $"{SsCountdownSeparator}{SsWatchRemaining(now)} (hard limit; separate from queued-alert freshness)";
    }
}
