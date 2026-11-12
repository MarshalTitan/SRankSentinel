using Dalamud.Game.ClientState.Conditions;

namespace SRankSentinel;

public sealed partial class Plugin
{
    private const string LifestreamRequiredMessage =
        "Lifestream is required for SRankSentinel World travel. Install or enable Lifestream to continue.";
    private const string LifestreamBusyMessage = "Waiting for Lifestream to finish its current travel";
    private LifestreamTravel? worldTravel;
    private LifestreamReadiness? previousLifestreamReadiness;
    private bool worldTravelBusyTimeoutLogged;
    private int playerReadyInstance = -1;
    private readonly HashSet<(string World, uint Territory)> observedSingleInstances = new();

    // vnavmesh's Stop IPC is global, so even a manual skip, a kill notice or
    // unloading Sentinel must not stop a path that Lifestream now owns.
    private bool CanStopSentinelMovement() =>
        worldTravel?.AwaitingArrival != true && lifestream.ReadReadiness() != LifestreamReadiness.Busy;

    private bool CanRunHuntTick(DateTime now)
    {
        var provider = lifestream.ReadReadiness();
        var previous = previousLifestreamReadiness;
        previousLifestreamReadiness = provider;
        if (provider == LifestreamReadiness.Unavailable)
        {
            if (previous != provider)
            {
                vnav.StopOwnedMovementSafe("Lifestream became unavailable");
                log.Warning(LifestreamRequiredMessage);
            }
            status = LifestreamRequiredMessage;
            return false;
        }

        if (state == SentinelState.WorldVisit)
        {
            if (previous != provider && worldTravel is { } transfer)
                log.Information("Lifestream readiness {Provider}: target={Target} ({DC}), current={Current}, requests={Attempts}",
                    provider, transfer.Destination.Name, transfer.Destination.LogicalDc, travel.CurrentWorld, transfer.Attempts);
            TickLifestreamWorldVisit(now, provider);
            return false;
        }
        if (provider == LifestreamReadiness.Busy)
        {
            status = LifestreamBusyMessage;
            return false;
        }

        if (status is LifestreamRequiredMessage or LifestreamBusyMessage)
            status = "Lifestream is ready";
        return true;
    }

    private void BeginLifestreamWorldVisit(DateTime now)
    {
        if (current is null || !travel.IsWorldVisitCompatible(current.World) ||
            travel.ResolveVisitWorld(current.World) is not { } target)
        {
            FailWorldVisit("World Visit target is outside the current region");
            return;
        }

        if (worldTravel?.Destination.Id != target.Id)
        {
            worldTravel = new(target, now, TimeSpan.FromSeconds(Math.Max(15, config.TravelTimeoutSeconds)));
            worldTravelBusyTimeoutLogged = false;
        }
        else
            worldTravel.ResumeFromHub(now);

        SetState(SentinelState.WorldVisit, $"Preparing Lifestream World travel to {target.Name} ({target.LogicalDc})");
    }

    private void TickLifestreamWorldVisit(DateTime now, LifestreamReadiness provider)
    {
        if (current is null || worldTravel is not { } transfer)
            return;

        var player = objects.LocalPlayer;
        var actualWorldId = player?.CurrentWorld.RowId ?? 0;
        var gameBusy = travel.IsBusy || !clientState.IsLoggedIn;
        var interactable = !gameBusy && player is { IsTargetable: true } && !combat.IsPlayerDead &&
            !condition[ConditionFlag.Occupied] && !condition[ConditionFlag.OccupiedInEvent] &&
            !condition[ConditionFlag.OccupiedInQuestEvent] && !condition[ConditionFlag.OccupiedInCutSceneEvent] &&
            !condition[ConditionFlag.InCombat];
        var atHub = travel.IsInUldah(clientState.TerritoryType);
        var target = transfer.Destination;
        var actualWorld = travel.CurrentWorld;
        if (provider == LifestreamReadiness.Idle && !gameBusy && !IsAlertFresh(current, now))
        {
            worldTravel = null;
            FailWorldVisit($"Hunt on {target.Name} expired or was invalidated during Lifestream World travel");
            return;
        }
        var result = transfer.Tick(now, provider, actualWorldId, interactable, gameBusy, atHub, destination =>
        {
            vnav.StopOwnedMovementSafe("handing World travel to Lifestream");
            return lifestream.TryChangeWorld(destination);
        });

        switch (result)
        {
            case LifestreamTravelStep.DependencyUnavailable:
                status = LifestreamRequiredMessage;
                return;
            case LifestreamTravelStep.WaitForTravel:
            case LifestreamTravelStep.TravelStillBusyAfterTimeout:
                status = $"Waiting for {(provider == LifestreamReadiness.Busy ? "Lifestream" : "the game")} to finish World travel to {target.Name}";
                if (result == LifestreamTravelStep.TravelStillBusyAfterTimeout && !worldTravelBusyTimeoutLogged)
                {
                    worldTravelBusyTimeoutLogged = true;
                    log.Warning("World travel timeout for {Target} ({DC}) while still in progress; current={Current}, Lifestream={Provider}, gameBusy={GameBusy}, attempts={Attempts}; waiting without interruption",
                        target.Name, target.LogicalDc, actualWorld, provider, gameBusy, transfer.Attempts);
                }
                return;
            case LifestreamTravelStep.WaitForArrival:
                status = $"Waiting for game-confirmed arrival on {target.Name}";
                return;
            case LifestreamTravelStep.WaitForPlayer:
                status = $"Waiting for the player to be ready for World travel to {target.Name}";
                return;
            case LifestreamTravelStep.ReturnToHub:
                ResetReturnRecoveryTracking();
                SetState(SentinelState.ResetToUldah, $"Returning to Ul'dah on {actualWorld} before retrying {target.Name}");
                return;
            case LifestreamTravelStep.RequestAccepted:
            case LifestreamTravelStep.RequestRejected:
                var accepted = result == LifestreamTravelStep.RequestAccepted;
                worldTravelBusyTimeoutLogged = false;
                log.Information("Lifestream.ChangeWorld {Result}: target={Target} ({DC}), current={Current}, attempt={Attempt}/{Limit}, busyBeforeRequest={Provider}",
                    accepted ? "accepted" : "rejected", target.Name, target.LogicalDc, actualWorld, transfer.Attempts,
                    LifestreamTravel.MaximumAttempts, provider);
                status = accepted ? $"Lifestream accepted World travel to {target.Name}; awaiting arrival" :
                    $"Lifestream rejected World travel to {target.Name}; checking readiness before continuing";
                return;
            case LifestreamTravelStep.RetryScheduled:
                log.Warning("Lifestream became idle without arrival on {Target} ({DC}); current={Current}, attempts={Attempts}; scheduling one retry",
                    target.Name, target.LogicalDc, actualWorld, transfer.Attempts);
                status = $"Retrying Lifestream World travel to {target.Name}";
                return;
            case LifestreamTravelStep.Arrived:
                log.Information("World arrival confirmed from game state: {Target} ({DC}), current={Current}, Lifestream={Provider}, attempts={Attempts}",
                    target.Name, target.LogicalDc, actualWorld, provider, transfer.Attempts);
                worldTravel = null;
                CompleteWorldVisit(target.Name);
                return;
            case LifestreamTravelStep.Unresolved:
                log.Warning("Lifestream World travel unresolved: target={Target} ({DC}), current={Current}, Lifestream={Provider}, interactable={Interactable}, attempts={Attempts}/{Limit}",
                    target.Name, target.LogicalDc, actualWorld, provider, interactable, transfer.Attempts, LifestreamTravel.MaximumAttempts);
                worldTravel = null;
                FailWorldVisit($"Lifestream World travel to {target.Name} unresolved after {transfer.Attempts} request(s)");
                return;
        }
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
