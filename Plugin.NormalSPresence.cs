using Dalamud.Game.Chat;
using Dalamud.Game.ClientState;

namespace SRankSentinel;

public sealed partial class Plugin
{
    private readonly OrdinarySPresenceGate normalSPresence = new();

    private void MaintainNormalSPresenceReadiness()
    {
        // Run before global loading/death/dependency guards can skip the hunt tick.
        if (state == SentinelState.CheckNormalSPresence && (!config.Enabled || !CanActAfterRevival()))
            normalSPresence.PauseObservation();
    }

    private bool IsOrdinarySPresenceContext() => current is not null && !killConfirmed &&
        state is not (SentinelState.SsWatch or SentinelState.PostKillSsGrace) &&
        !ssChainObserved && !IsReservedSsWatch(current) &&
        !HuntCatalog.IsAnySsName(current.CreatureName) &&
        !HuntCatalog.IsAnyPrecursorName(current.CreatureName) &&
        HuntCatalog.ResolveStrict(current.TerritoryId, current.CreatureName) is not null;

    private void OnNormalSZoneInit(ZoneInitEventArgs zone)
    {
        // ZoneInit includes same-territory instance changes. Buffer during loading; the
        // settled World/territory/live instance must still match before evidence is usable.
        normalSPresence.ZoneArrived(zone.TerritoryType.RowId, zone.Instance, DateTime.UtcNow);
    }

    private void OnNormalSPresenceLog(ILogMessage message)
    {
        if (!config.Enabled || !IsOrdinarySPresenceContext() ||
            !normalSPresence.ObserveNativeMessage(message.LogMessageId, DateTime.UtcNow))
            return;
        // The native message object is callback-scoped; retain only the ID/timestamp.
        LogNormalSPresence("native powerful-mark message buffered; awaiting settled context", false, false);
    }

    private void BeginNormalSPresenceCheck(DateTime now)
    {
        if (!IsOrdinarySPresenceContext())
        {
            BeginMeshWait("Zoning and player readiness confirmed");
            return;
        }
        vnav.StopSafe("ordinary S arrival presence observation");
        SetState(SentinelState.CheckNormalSPresence,
            $"Entered territory {current!.TerritoryId} for {current.CreatureName}; checking for zone-wide S-rank presence");
        TickNormalSPresence(now);
    }

    private void TickNormalSPresence(DateTime now)
    {
        if (current is null)
            return;
        var ordinary = IsOrdinarySPresenceContext();
        var instance = -1;
        var ready = CanActAfterRevival() && travel.TryReadLiveInstance(out instance);
        var contextMatches = ready && clientState.TerritoryType == current.TerritoryId &&
            travel.CurrentWorld.Equals(current.World, StringComparison.OrdinalIgnoreCase) &&
            Math.Max(1, instance) == Math.Max(1, current.Instance);
        // FindMark alone checks territory, not World/instance. Never use it across contexts.
        var exact = contextMatches ? FindMark() : null;
        var wasReady = normalSPresence.ReadyUtc;
        var decision = normalSPresence.Decide(now, ordinary, ready, travel.CurrentWorld,
            clientState.TerritoryType, instance, current.Instance, exact is not null, killConfirmed);
        if (wasReady == DateTime.MinValue && normalSPresence.ReadyUtc != DateTime.MinValue)
            LogNormalSPresence("territory ready; five-second observation started", false, false);
        switch (decision)
        {
            case OrdinarySPresenceDecision.ConfirmedDeath:
                // Provider deaths change state through ConfirmKill; never resume a dead alert.
                return;
            case OrdinarySPresenceDecision.ExactEntity:
                mark = exact;
                MarkWasIdentified(exact!);
                LogNormalSPresence("exact entity found before presence check completed", true, false);
                if (exact!.IsDead || exact.CurrentHp == 0)
                    ConfirmKill($"{current.CreatureName} visibly dead during arrival presence observation");
                else
                    BeginMeshWait($"Exact {current.CreatureName} entity found; continuing normal hunt");
                return;
            case OrdinarySPresenceDecision.PresenceConfirmed:
                LogNormalSPresence("powerful-mark presence confirmed; continuing normal hunt", false, false);
                BeginMeshWait("Powerful-mark presence confirmed; continuing normal hunt");
                return;
            case OrdinarySPresenceDecision.AbandonUnresolved:
                AbandonNormalSPresence();
                return;
            case OrdinarySPresenceDecision.ArrivalUnavailable:
                LogNormalSPresence("native arrival capture unavailable; preserving conservative LocateMark recovery", false, false);
                BeginMeshWait("Arrival presence capture unavailable; keeping normal entity search");
                return;
            case OrdinarySPresenceDecision.Bypass:
                BeginMeshWait("SS context bypasses ordinary S arrival presence observation");
                return;
            case OrdinarySPresenceDecision.WaitForReady:
                playerReadySinceUtc = DateTime.MinValue;
                SetState(SentinelState.WaitForPlayerReady, "Waiting for the correct loaded World, territory and live instance before presence observation");
                return;
            default:
                status = $"{current.CreatureName}: checking zone-wide powerful-mark presence — {normalSPresence.RemainingSeconds(now):0.0}s remaining";
                return;
        }
    }

    private void AbandonNormalSPresence()
    {
        LogNormalSPresence("five-second observation expired without presence or entity", false, true);
        FailCurrent("No powerful-mark presence or entity observed within 5s; abandoning ordinary S-rank alert as unresolved",
            HuntExitRequestSource.RecoveryBudget);
    }

    private void LogNormalSPresence(string result, bool exactEntity, bool fastAbandon) =>
        log.Information("Ordinary S arrival presence: {Result}; mark={Mark}, world={World}, territory={Territory}, instance={Instance}, arrival={Arrival:o}, ready={Ready:o}, presence={Presence:o}, exactEntity={ExactEntity}, fastAbandon={FastAbandon}",
            result, current?.CreatureName ?? "(none)", current?.World ?? "(none)", current?.TerritoryId ?? 0, current?.Instance ?? 0,
            normalSPresence.ArrivalUtc, normalSPresence.ReadyUtc, normalSPresence.PresenceUtc, exactEntity, fastAbandon);
}
