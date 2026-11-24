using Dalamud.Game.ClientState.Objects.Types;

namespace SRankSentinel;

public sealed partial class Plugin
{
    private readonly FullCombatAssist fullAssist = new();
    private ICombatAssistProvider? assistProvider;
    private string assistReadiness = "Waiting for an initially tagged S/SS";

    private AssistFrame AssistContext(IBattleChara target) => new(
        new(target.GameObjectId, target.BaseId, objects.LocalPlayer?.ClassJob.RowId ?? 0, pullCycle),
        config.Enabled && config.FullCombatAssist, IsExactCheckpointMark(target) &&
            current is not null && !IsReservedSsWatch(current) &&
            HuntCatalog.ResolveStrict(current.TerritoryId, current.CreatureName) is not null,
        !target.IsDead && target.CurrentHp > 0, CombatController.IsMarkInCombat(target),
        CombatController.HpPercent(target), ActiveDistanceProfile.EngageHpPercent,
        pullCycleTagged && tagCycle.Consumed.HasFlag(TagMilestone.Initial), CanActAfterRevival());

    private bool TryBeginCombatAssist(IBattleChara target, bool afterRaise = false)
    {
        if (!config.FullCombatAssist || !config.Enabled) return false;
        var frame = AssistContext(target);
        if (!frame.Eligible) return false;
        var provider = assistProvider ??= new CombatAssistIpc(pi);
        // No movement or target changes on the unavailable path.
        var reason = provider.Unavailable(frame.Fight);
        if (reason is not null)
        {
            assistReadiness = reason;
            log.Information("Full combat assist unavailable for {Mark}: {Reason}; using safe tagging", target.Name.TextValue, reason);
            return false;
        }
        if (!combat.TargetMark(target)) return false;
        vnav.StopOwnedMovementSafe("handing off from confirmed tag to BossMod");
        bool granted = afterRaise
            ? fullAssist.TryResume(frame, raiseAcceptanceSubmittedUtc != DateTime.MinValue, provider)
            : fullAssist.TryEnter(frame, provider);
        assistReadiness = fullAssist.Detail;
        if (!granted) return false;
        SetState(SentinelState.AssistedCombat, "Assisted combat: Wrath rotation + BossMod mechanic avoidance");
        return true;
    }

    private void TickAssistedCombat(DateTime now)
    {
        // Return before any ordinary checkpoint, incidental, parking or retreat code runs.
        var target = FindMark();
        bool same = target is not null && target.GameObjectId == identifiedMarkGameObjectId &&
            current is not null && travel.CurrentWorld.Equals(current.World, StringComparison.OrdinalIgnoreCase) &&
            travel.TryReadLiveInstance(out var live) && Math.Max(1, live) == current.Instance;
        if (same && (target!.IsDead || target.CurrentHp == 0))
        {
            fullAssist.End("Exact mark died");
            ConfirmKill("Exact assisted S/SS visibly died");
            return;
        }
        if (combat.IsPlayerDead)
        {
            if (fullAssist.Active) fullAssist.SuspendForDeath();
            TickRaiseAcceptance(now, "Assisted combat suspended; waiting for Raise");
            return;
        }
        if (!fullAssist.Active && !CanActAfterRevival())
        {
            status = "Combat assist suspended; waiting for an interactable player";
            return;
        }
        if (!fullAssist.Active && same && TryBeginCombatAssist(target!, afterRaise: true))
        {
            ResetRaiseTracking();
            return;
        }
        if (fullAssist.Active && same && fullAssist.Maintain(AssistContext(target!), assistProvider!) &&
            combat.TargetMark(target!))
            return;

        fullAssist.End("Returning to safe tagging");
        ObserveRaiseRecoveryCompletion(now); // preserve configured Raise retag on failed handoff
        if (same && target is not null)
        {
            // Existing stable reset detector owns rearming. Do not mark a checkpoint consumed here.
            if (!ObservePullCycleReset(target, now)) BeginGroundRetreat(target, postTag: true);
        }
        else SetState(SentinelState.LocateMark, "Combat assist lost exact context; resuming normal reacquisition");
    }
}
