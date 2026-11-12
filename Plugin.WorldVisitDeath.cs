using Dalamud.Game.ClientState.Conditions;

namespace SRankSentinel;

public sealed partial class Plugin
{
    private WorldVisitCancellationObservation? lastWorldVisitCancellationObservation;
    private bool worldVisitCancellationTimeoutLogged;

    // Called only after the shared positive-death gate records the kill and its History.
    private bool BeginWorldVisitDeathCancellation(HuntAlertSnapshot dead, DateTime now)
    {
        if (state != SentinelState.WorldVisit || worldTravel is not { } transfer)
            return false;

        transfer.BeginDeathCancellation(objects.LocalPlayer?.CurrentWorld.RowId ?? 0, now);
        lastWorldVisitCancellationObservation = null;
        worldVisitCancellationTimeoutLogged = false;
        status = $"{dead.CreatureName} on {dead.World} was reported killed during World Visit";
        log.Information(
            "{Status}: territory={Territory}, instance={Instance}, source={Source}, current={Current}, ownedRequest={Owned}, attempts={Attempts}",
            status, dead.TerritoryId, dead.Instance, travel.ReadVisitWorldName(transfer.DeathCancellation!.SourceId),
            travel.CurrentWorld, transfer.AwaitingArrival, transfer.Attempts);
        return true;
    }

    private void TickWorldVisitDeathCancellation(DateTime now, LifestreamTravel transfer,
        WorldVisitDeathCancellation cancellation, LifestreamReadiness provider, uint worldId, bool interactable, bool atHub)
    {
        var source = travel.ReadVisitWorldName(cancellation.SourceId);
        var destination = transfer.Destination.Name;
        var zoning = travel.IsBetweenAreas;
        var queue = zoning ? default : travel.ReadWorldVisitQueue(source, destination);
        var observation = new WorldVisitCancellationObservation(provider, worldId, atHub, interactable, zoning,
            condition[ConditionFlag.WaitingToVisitOtherWorld], condition[ConditionFlag.ReadyingVisitOtherWorld], queue);
        if (lastWorldVisitCancellationObservation != observation)
        {
            lastWorldVisitCancellationObservation = observation;
            log.Information(
                "Dead-hunt World Visit {Source} -> {Destination}: current={Current}, Lifestream={Provider}, waiting={Waiting}, readying={Readying}, zoning={Zoning}, WorldTravelFinderStatus={StatusVisible}, displayed={ShownSource} -> {ShownDestination}, routeMatches={Matches}, cancelAvailable={CancelAvailable}",
                source, destination, travel.CurrentWorld, provider, observation.Waiting, observation.Readying,
                zoning, queue.Visible, queue.Source, queue.Destination, queue.RouteMatches, queue.CancelEnabled);
        }

        var result = cancellation.Tick(now, observation, lifestream.TryAbort,
            () => travel.IsInUldah(clientState.TerritoryType) && travel.TryCancelWorldVisitQueue(source, destination));
        switch (result)
        {
            case WorldVisitDeathStep.AbortAttempted:
                log.Information("Lifestream.Abort for dead-hunt World Visit {Source} -> {Destination}: dispatched={Dispatched}, priorState={Provider}",
                    source, destination, cancellation.AbortSent, provider);
                status = $"Stopping Lifestream travel to {destination} after the hunt was killed";
                break;
            case WorldVisitDeathStep.CancelAttempted:
                log.Information("Game World Visit Cancel for {Source} -> {Destination}: dispatched={Dispatched}",
                    source, destination, cancellation.CancelSent);
                status = $"Cancelling {source} → {destination} World Visit";
                break;
            case WorldVisitDeathStep.Zoning:
                status = $"Waiting for World travel to settle; skipping the killed hunt on {destination}";
                break;
            case WorldVisitDeathStep.TimedOut:
                status = $"World Visit cancellation could not be confirmed; waiting for the old visit to clear";
                if (!worldVisitCancellationTimeoutLogged)
                {
                    worldVisitCancellationTimeoutLogged = true;
                    log.Warning("Dead-hunt World Visit cancellation timed out for {Source} -> {Destination}; abortSent={Abort}, cancelSent={Cancel}; waiting for manual cancellation or settled arrival",
                        source, destination, cancellation.AbortSent, cancellation.CancelSent);
                }
                break;
            case WorldVisitDeathStep.SettledAtSource:
            case WorldVisitDeathStep.SettledAtDestination:
                var settled = result == WorldVisitDeathStep.SettledAtDestination
                    ? $"World Visit finished on {destination}; skipping the killed hunt and selecting the next hunt"
                    : transfer.AwaitingArrival
                        ? $"World Visit cancellation confirmed on {source}; selecting next hunt"
                        : "Dead destination discarded before World Visit; selecting next hunt";
                worldTravel = null;
                ClearPendingSsChainEvidence();
                nextActionUtc = now;
                SetState(SentinelState.ResetToUldah, settled, HuntExitRequestSource.ConfirmedDeath);
                break;
            default:
                status = cancellation.CancelSent
                    ? $"Waiting for {source} → {destination} World Visit cancellation to clear"
                    : $"Hunt on {destination} was killed; waiting to clear the old World Visit";
                break;
        }
    }
}
