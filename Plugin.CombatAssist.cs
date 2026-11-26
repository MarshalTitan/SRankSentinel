using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

namespace SRankSentinel;

public sealed partial class Plugin
{
    private readonly FullCombatAssist fullAssist = new();
    private ICombatAssistProvider? assistProvider;
    private readonly IPartyList assistParty;
    private readonly ITargetManager assistTargets;
    private string assistReadiness = "Waiting for an initially tagged S/SS";

    private bool SoloAssistAllowed => FullCombatAssist.IsSolo(assistParty.Length, assistParty.IsAlliance,
        condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.ParticipatingInCrossWorldPartyOrAlliance]);

    private void NewAssistHunt()
    {
        fullAssist?.NewHunt();
        assistReadiness = "Waiting for the initial confirmed tag";
    }

    // Runs before the regular 250 ms hunt tick. Never route, act, record credit, or advance the
    // hunt here. Monitor the boss actor even when an incidental enemy keeps the player in combat.
    private void CheckAssistFrame(DateTime now)
    {
        (assistProvider as CombatAssistIpc)?.RetryRelease(now);
        if (!fullAssist.Active && !fullAssist.AwaitingRaise) return;
        try
        {
            if (!config.Enabled || !config.FullCombatAssist || !SoloAssistAllowed || current is null ||
                killConfirmed || travel.IsBusy || current.TerritoryId != clientState.TerritoryType ||
                !travel.CurrentWorld.Equals(current.World, StringComparison.OrdinalIgnoreCase) ||
                Math.Max(1, travel.CurrentInstance) != current.Instance ||
                objects.SearchById(identifiedMarkGameObjectId) is not IBattleChara target || target.IsDead ||
                target.CurrentHp == 0 || target.MaxHp == 0)
            {
                fullAssist.End("Solo fight context ended");
                return;
            }
            if (!fullAssist.ObserveBoss(CombatController.IsMarkInCombat(target), CombatController.HpPercent(target)))
            {
                assistReadiness = fullAssist.Detail;
                // Drop the boss target we handed off, including ordinary auto-attacks. Never
                // clear a different target the player has selected in the meantime.
                if (assistTargets.Target?.GameObjectId == target.GameObjectId) assistTargets.Target = null;
                log.Information("Solo assist suspended for {Mark}: {Reason}", current.CreatureName, assistReadiness);
                return;
            }
            if (fullAssist.Active && combat.IsPlayerDead) fullAssist.SuspendForDeath();
            else if (fullAssist.Active && (!CanActAfterRevival() || assistTargets.Target?.GameObjectId != target.GameObjectId))
                fullAssist.End("Player or selected boss no longer ready");
        }
        catch (Exception ex)
        {
            fullAssist.End("Boss observation unavailable");
            log.Warning("Solo combat assist stopped: {Reason}", ex.Message);
        }
    }

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
        if (!config.FullCombatAssist || !config.Enabled || fullAssist.ResetSuspected ||
            (afterRaise && (!fullAssist.AwaitingRaise || raiseAcceptanceSubmittedUtc == DateTime.MinValue))) return false;
        if (!SoloAssistAllowed) { assistReadiness = "Available only while solo"; return false; }
        var frame = AssistContext(target);
        if (!frame.Eligible) return false;
        var provider = assistProvider ??= new CombatAssistIpc(pi, reason => log.Warning("{Reason}", reason));
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
        bool same = target is not null && target.GameObjectId == identifiedMarkGameObjectId && !travel.IsBusy &&
            current is not null && travel.CurrentWorld.Equals(current.World, StringComparison.OrdinalIgnoreCase) &&
            Math.Max(1, travel.CurrentInstance) == current.Instance;
        if (same) MarkWasIdentified(target!); // keep the existing kill/reward recency evidence fresh
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
        if (same && !EnsureHuntJob(now)) return;
        if (!fullAssist.Active && same && TryBeginCombatAssist(target!, afterRaise: true))
        {
            ResetRaiseTracking();
            return;
        }
        if (fullAssist.Active && same && fullAssist.Maintain(AssistContext(target!), assistProvider!))
            return;

        if (fullAssist.Active || fullAssist.AwaitingRaise) fullAssist.End("Returning to safe tagging");
        assistReadiness = fullAssist.Detail;
        ObserveRaiseRecoveryCompletion(now); // preserve configured Raise retag on failed handoff
        if (same && target is not null)
        {
            // Existing stable reset detector owns rearming. Do not mark a checkpoint consumed here.
            if (!ObservePullCycleReset(target, now)) BeginGroundRetreat(target, postTag: true);
        }
        else SetState(SentinelState.LocateMark, "Combat assist lost exact context; resuming normal reacquisition");
    }
}
