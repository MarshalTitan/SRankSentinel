namespace SRankSentinel;

public sealed partial class Plugin
{
    private int worldVisitAttempts;
    private DateTime worldVisitSetupDeadlineUtc;
    private DateTime? worldVisitLastBusyUtc;
    private VisitMenuAction lastWorldVisitMenuAction;
    private string ownedWorldVisitDestination = string.Empty;
    private int playerReadyInstance = -1;
    private readonly HashSet<(string World, uint Territory)> observedSingleInstances = new();

    private static bool IsWorldVisitState(SentinelState value) => value is
        SentinelState.WorldVisit or SentinelState.SelectLogicalDataCenter or SentinelState.SelectWorld or
        SentinelState.ConfirmWorldVisit or SentinelState.WaitForWorld;

    private void BeginRegionalWorldVisit(DateTime now)
    {
        worldVisitAttempts = 1;
        ownedWorldVisitDestination = current?.World ?? string.Empty;
        BeginWorldVisitAttempt(now);
    }

    private void BeginWorldVisitAttempt(DateTime earliestAction)
    {
        worldVisitLastBusyUtc = null;
        lastWorldVisitMenuAction = VisitMenuAction.WaitForSelector;
        worldVisitSetupDeadlineUtc = earliestAction.AddSeconds(Math.Clamp(config.TravelTimeoutSeconds, 15, 30));
        nextActionUtc = earliestAction;
        var origin = travel.CurrentVisitWorld;
        var destination = travel.ResolveVisitWorld(ownedWorldVisitDestination);
        log.Information("World Visit attempt {Attempt}/{Limit}: {CurrentWorld} ({CurrentDC}) -> {TargetWorld} ({TargetDC}); Ul'dah hub",
            worldVisitAttempts, WorldVisitPolicy.MaximumAttempts, origin?.Name ?? "unknown", origin?.LogicalDc ?? "unknown",
            ownedWorldVisitDestination, destination?.LogicalDc ?? "unknown");
        SetState(SentinelState.WorldVisit, $"Opening World Visit for {ownedWorldVisitDestination} ({destination?.LogicalDc})");
    }

    private void ReleaseWorldVisitMenus()
    {
        if (ownedWorldVisitDestination.Length != 0)
            travel.CloseWorldVisitMenus(ownedWorldVisitDestination);
        ownedWorldVisitDestination = string.Empty;
    }

    private bool CompleteWorldVisitIfArrived(string destination)
    {
        if (travel.IsBusy || !travel.IsInUldah(clientState.TerritoryType) ||
            string.IsNullOrWhiteSpace(destination) ||
            !travel.CurrentWorld.Equals(destination, StringComparison.OrdinalIgnoreCase))
            return false;
        CompleteWorldVisit(destination);
        return true;
    }

    private void WorldVisitAttemptFailed(DateTime now, string reason, bool retryable = true)
    {
        vnav.StopSafe();
        travel.CloseWorldVisitMenus(ownedWorldVisitDestination);
        log.Warning("World Visit attempt {Attempt}/{Limit} to {World} failed: {Reason}",
            worldVisitAttempts, WorldVisitPolicy.MaximumAttempts, ownedWorldVisitDestination, reason);
        if (retryable && WorldVisitPolicy.AfterFailedAttempt(worldVisitAttempts, travel.IsBusy) == VisitFailureAction.Retry)
        {
            worldVisitAttempts++;
            BeginWorldVisitAttempt(now.AddSeconds(3));
            return;
        }
        FailWorldVisit($"World Visit to {ownedWorldVisitDestination} unresolved: {reason}");
    }

    private void TickWorldVisit(DateTime now)
    {
        var destination = ownedWorldVisitDestination;
        if (CompleteWorldVisitIfArrived(destination))
            return;
        if (!travel.IsWorldVisitCompatible(destination))
        {
            WorldVisitAttemptFailed(now, "destination is outside the current World Visit region", false);
            return;
        }
        if (now >= worldVisitSetupDeadlineUtc)
        {
            WorldVisitAttemptFailed(now, "native World Visit window did not open");
            return;
        }
        if (now < nextActionUtc || travel.IsBusy)
            return;

        if (travel.WorldSelectionIsOpen() || travel.SelectWorldVisitMenu())
        {
            vnav.StopSafe();
            nextActionUtc = now.AddMilliseconds(250);
            SetState(SentinelState.SelectLogicalDataCenter,
                $"Opening logical DC selector for {destination}");
            return;
        }

        if (condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Mounted])
            UseGeneralAction(23);
        else if (!travel.InteractWithNearbyAetheryte() && vnav.IsReadySafe() &&
                 !vnav.IsPathRunningSafe() && !vnav.IsPathfindInProgressSafe())
        {
            vnav.MoveToSafe(NativeTravel.UldahAetherytePosition, false);
            status = "Walking normally to the Ul'dah World Visit aetheryte";
        }
        nextActionUtc = now.AddSeconds(2);
    }

    private void TickSelectLogicalDataCenter(DateTime now) => TickWorldVisitMenu(now, false);
    private void TickSelectWorld(DateTime now) => TickWorldVisitMenu(now, true);

    private void TickWorldVisitMenu(DateTime now, bool submitWorld)
    {
        var destination = ownedWorldVisitDestination;
        if (CompleteWorldVisitIfArrived(destination))
            return;
        if (now >= worldVisitSetupDeadlineUtc)
        {
            LogWorldVisitMenuLayout(destination);
            if (lastWorldVisitMenuAction == VisitMenuAction.WaitForRegionalLayout)
                WorldVisitAttemptFailed(now, $"native World Visit UI layout could not be read: {status}", false);
            else if (lastWorldVisitMenuAction is VisitMenuAction.SelectLogicalDataCenter or VisitMenuAction.WaitForWorldList)
                WorldVisitAttemptFailed(now, $"native World Visit UI did not refresh: {status}");
            else
                WorldVisitAttemptFailed(now, status);
            return;
        }
        if (now < nextActionUtc || travel.IsBusy)
            return;
        nextActionUtc = now.AddMilliseconds(500);
        (VisitMenuAction Action, string Detail) step;
        try { step = travel.StepWorldVisitMenu(destination, submitWorld); }
        catch (Exception ex)
        {
            log.Warning(ex, "Native World Visit menu could not be read safely");
            LogWorldVisitMenuLayout(destination);
            WorldVisitAttemptFailed(now, "native World Visit UI layout could not be read", false);
            return;
        }
        lastWorldVisitMenuAction = step.Action;
        status = step.Detail;
        switch (step.Action)
        {
            case VisitMenuAction.AlreadyCurrent:
                CompleteWorldVisitIfArrived(destination);
                return;
            case VisitMenuAction.Ineligible:
                WorldVisitAttemptFailed(now, step.Detail, false);
                return;
            case VisitMenuAction.SelectLogicalDataCenter:
                if (submitWorld)
                    SetState(SentinelState.SelectLogicalDataCenter, step.Detail);
                else
                {
                    log.Information("World Visit selector: {Detail}", step.Detail);
                    nextActionUtc = now.AddSeconds(1.25);
                }
                return;
            case VisitMenuAction.SelectWorld:
                SetState(SentinelState.SelectWorld, step.Detail);
                return;
            case VisitMenuAction.WorldSubmitted:
                log.Information("World Visit row: {Detail}", step.Detail);
                SetState(SentinelState.ConfirmWorldVisit, $"Confirming World Visit to {destination}");
                return;
        }
    }

    private void LogWorldVisitMenuLayout(string destination)
    {
        try { log.Warning("World Visit UI before close: {Layout}", travel.DescribeWorldVisitMenu(destination)); }
        catch (Exception ex) { log.Warning(ex, "World Visit UI diagnostic read failed for {World}", destination); }
    }

    private void TickConfirmWorldVisit(DateTime now)
    {
        var destination = ownedWorldVisitDestination;
        if (CompleteWorldVisitIfArrived(destination))
            return;
        if (travel.IsBusy)
        {
            worldVisitLastBusyUtc = now;
            SetState(SentinelState.WaitForWorld, $"Native World Visit in progress; waiting for {destination}");
            return;
        }
        if (now >= worldVisitSetupDeadlineUtc)
        {
            WorldVisitAttemptFailed(now, "the exact destination confirmation did not become available");
            return;
        }
        if (travel.ConfirmWorldVisit(destination))
        {
            worldVisitLastBusyUtc = null;
            SetState(SentinelState.WaitForWorld, $"World Visit requested; waiting for {destination}");
        }
    }

    private void TickWaitForWorld(DateTime now)
    {
        var destination = ownedWorldVisitDestination;
        var busy = travel.IsBusy;
        if (busy)
            worldVisitLastBusyUtc = now;
        var result = WorldVisitPolicy.ObserveArrival(
            travel.CurrentWorld.Equals(destination, StringComparison.OrdinalIgnoreCase),
            travel.IsInUldah(clientState.TerritoryType), busy,
            (now - stateSinceUtc).TotalSeconds,
            worldVisitLastBusyUtc is { } lastBusy ? (now - lastBusy).TotalSeconds : null,
            config.TravelTimeoutSeconds);
        if (result == VisitArrivalAction.Arrived)
            CompleteWorldVisit(destination);
        else if (result == VisitArrivalAction.AttemptFailed)
            WorldVisitAttemptFailed(now, busy
                ? "native World Visit queue exceeded the travel timeout"
                : "the game rejected or ended the visit without arrival");
    }

    private void LogProviderRegion(string source, string action, string world, bool eligible)
    {
        if (source is not ("Sonar" or "HuntAlerts"))
            return;
        var origin = travel.CurrentVisitWorld;
        var target = travel.ResolveVisitWorld(world);
        log.Information("Provider regional observation: {Provider} {Action} on {World}/{LogicalDC} (region {Region}); character on {CurrentWorld}/{CurrentDC}; eligible={Eligible}",
            source, action, world, target?.LogicalDc ?? "unknown", target?.RegionId ?? 0,
            origin?.Name ?? "unknown", origin?.LogicalDc ?? "unknown", eligible);
    }

    private bool IsObservedSingleInstance(string world, uint territory) =>
        observedSingleInstances.Contains((world.ToUpperInvariant(), territory));

    private HuntAlertSnapshot CanonicalizeObservedInstance(HuntAlertSnapshot alert) =>
        WorldVisitPolicy.CanonicalizeInstance(alert, IsObservedSingleInstance(alert.World, alert.TerritoryId));

    private void RecordLiveInstanceTopology(int instance)
    {
        if (current is not { } alert)
            return;
        var context = (alert.World.ToUpperInvariant(), alert.TerritoryId);
        if (instance != 0)
        {
            observedSingleInstances.Remove(context);
            return;
        }
        observedSingleInstances.Add(context);
        var normalized = CanonicalizeObservedInstance(alert);
        if (normalized.Key != alert.Key)
        {
            log.Information("Single-instance territory confirmed on {World}: territory {Territory}, provider instance {Reported} normalized to 1",
                alert.World, alert.TerritoryId, alert.Instance);
            if (faloopReportIdsByAlertKey.Remove(alert.Key, out var reportId))
                faloopReportIdsByAlertKey.TryAdd(normalized.Key, reportId);
            current = normalized;
        }
        // A stale instance alias of the active mark is the same hunt only after
        // the client positively reports single-instance topology in this World/zone.
        var queued = pendingAlerts.ToArray();
        var duplicates = queued.Where(candidate => CanonicalizeObservedInstance(candidate).Key == normalized.Key).ToArray();
        if (duplicates.Length == 0)
            return;
        pendingAlerts.Clear();
        foreach (var candidate in queued)
            if (CanonicalizeObservedInstance(candidate).Key != normalized.Key)
                pendingAlerts.Enqueue(candidate);
        foreach (var duplicate in duplicates.Where(candidate => candidate.Key != normalized.Key))
            faloopReportIdsByAlertKey.Remove(duplicate.Key);
        PersistQueue();
    }
}
