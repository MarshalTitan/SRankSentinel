using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace SRankSentinel;

internal readonly record struct TagAttemptResult(
    bool Submitted,
    bool ClientAccepted,
    ushort SequenceBefore,
    ushort SequenceAfter,
    bool DeferredForTarget);

internal readonly record struct TagActionState(
    ushort LastUsedSequence,
    ushort LastHandledSequence,
    bool QueuedForTarget);

internal enum RaiseInteractionState
{
    NoDialog,
    PromptUnrecognized,
    AffirmativeUnavailable,
    Submitted,
    Declined,
}

internal readonly record struct RaiseInteraction(
    RaiseInteractionState State,
    string Prompt)
{
    public bool Recognized => State is RaiseInteractionState.AffirmativeUnavailable or
        RaiseInteractionState.Submitted or RaiseInteractionState.Declined;
}

internal readonly record struct TrashActionAttempt(
    bool SupportedJob,
    bool Targeted,
    bool Submitted,
    uint ActionId);

internal sealed class CombatController(
    IGameGui gameGui,
    ICondition condition,
    IObjectTable objects,
    ITargetManager targets)
{
    public bool IsPlayerDead => condition[ConditionFlag.Unconscious];

    public static bool IsMarkInCombat(IBattleChara mark) =>
        mark.StatusFlags.HasFlag(Dalamud.Game.ClientState.Objects.Enums.StatusFlags.InCombat);

    public static float HpPercent(IBattleChara mark) =>
        mark.MaxHp == 0 ? 100f : mark.CurrentHp * 100f / mark.MaxHp;

    public unsafe uint ResolveTagActionId()
    {
        var actionId = GetBaseRangedTagAction(objects.LocalPlayer?.ClassJob.RowId ?? 0);
        if (actionId == 0)
            return 0;

        var manager = ActionManager.Instance();
        if (manager is null)
            return actionId;

        var adjustedActionId = manager->GetAdjustedActionId(actionId);
        return adjustedActionId == 0 ? actionId : adjustedActionId;
    }

    public bool TargetMark(IBattleChara mark)
    {
        targets.Target = mark;
        return targets.Target?.GameObjectId == mark.GameObjectId;
    }

    public unsafe TagAttemptResult TrySingleTag(uint actionId, IBattleChara mark)
    {
        if (actionId == 0 || IsPlayerDead || mark.IsDead || mark.CurrentHp == 0)
            return new TagAttemptResult(false, false, 0, 0, false);

        TargetMark(mark);

        var manager = ActionManager.Instance();
        if (manager is null || manager->GetActionStatus(ActionType.Action, actionId, mark.GameObjectId) != 0)
            return new TagAttemptResult(false, false, 0, 0, false);

        var sequenceBefore = manager->LastUsedActionSequence;
        var accepted = manager->UseAction(ActionType.Action, actionId, mark.GameObjectId);
        var queuedForTarget = manager->ActionQueued &&
                              manager->QueuedActionType == ActionType.Action &&
                              manager->QueuedActionId == actionId &&
                              (ulong)manager->QueuedTargetId == mark.GameObjectId;
        var castingForTarget = manager->CastActionType == ActionType.Action &&
                               manager->CastActionId == actionId &&
                               (ulong)manager->CastTargetId == mark.GameObjectId;
        return new TagAttemptResult(
            true,
            accepted,
            sequenceBefore,
            manager->LastUsedActionSequence,
            queuedForTarget || castingForTarget);
    }

    public unsafe TagActionState ReadTagActionState(uint actionId, ulong targetId)
    {
        var manager = ActionManager.Instance();
        if (manager is null)
            return default;

        var queuedForTarget = manager->ActionQueued &&
                              manager->QueuedActionType == ActionType.Action &&
                              manager->QueuedActionId == actionId &&
                              (ulong)manager->QueuedTargetId == targetId;
        return new TagActionState(
            manager->LastUsedActionSequence,
            manager->LastHandledActionSequence,
            queuedForTarget);
    }

    private static uint GetBaseRangedTagAction(uint classJobId) => classJobId switch
    {
        1 or 19 => 24,       // Gladiator/Paladin: Shield Lob
        3 or 21 => 46,       // Marauder/Warrior: Tomahawk
        4 or 22 => 90,       // Lancer/Dragoon: Piercing Talon
        5 or 23 => 97,       // Archer/Bard: Heavy Shot
        6 or 24 => 119,      // Conjurer/White Mage: Stone
        7 or 25 => 141,      // Thaumaturge/Black Mage: Fire
        26 or 27 => 163,     // Arcanist/Summoner: Ruin
        28 => 178,           // Scholar: Ruin
        29 or 30 => 2247,    // Rogue/Ninja: Throwing Dagger
        31 => 2866,          // Machinist: Split Shot
        32 => 3624,          // Dark Knight: Unmend
        33 => 3596,          // Astrologian: Malefic
        34 => 7486,          // Samurai: Enpi
        35 => 7503,          // Red Mage: Jolt
        36 => 11385,         // Blue Mage: Water Cannon (must be equipped)
        37 => 16143,         // Gunbreaker: Lightning Shot
        38 => 15989,         // Dancer: Cascade
        39 => 24386,         // Reaper: Harpe
        40 => 24283,         // Sage: Dosis
        41 => 34632,         // Viper: Writhing Snap
        42 => 34650,         // Pictomancer: Fire in Red
        _ => 0,              // Pugilist/Monk and non-combat jobs have no safe native ranged tag.
    };

    public unsafe RaiseInteraction TryAcceptRaise()
    {
        if (!IsPlayerDead)
            return new RaiseInteraction(RaiseInteractionState.NoDialog, string.Empty);

        var addon = gameGui.GetAddonByName<AddonSelectYesno>("SelectYesno");
        if (addon is null || !addon->IsReady || !addon->IsVisible || addon->PromptText is null)
            return new RaiseInteraction(RaiseInteractionState.NoDialog, string.Empty);

        var prompt = addon->PromptText->NodeText.ToString();
        if (!IsRaisePrompt(prompt))
            return new RaiseInteraction(RaiseInteractionState.PromptUnrecognized, prompt);
        if (addon->YesButton is null || !addon->YesButton->IsEnabled)
            return new RaiseInteraction(RaiseInteractionState.AffirmativeUnavailable, prompt);

        var value = new AtkValue { Type = AtkValueType.Int, Int = 0 };
        ((AtkUnitBase*)addon)->FireCallback(1, &value, true);
        return new RaiseInteraction(RaiseInteractionState.Submitted, prompt);
    }

    public unsafe RaiseInteraction TryDeclineRaise()
    {
        if (!IsPlayerDead)
            return new RaiseInteraction(RaiseInteractionState.NoDialog, string.Empty);

        var addon = gameGui.GetAddonByName<AddonSelectYesno>("SelectYesno");
        if (addon is null || !addon->IsReady || !addon->IsVisible || addon->PromptText is null)
            return new RaiseInteraction(RaiseInteractionState.NoDialog, string.Empty);

        var prompt = addon->PromptText->NodeText.ToString();
        if (!IsRaisePrompt(prompt))
            return new RaiseInteraction(RaiseInteractionState.PromptUnrecognized, prompt);

        var value = new AtkValue { Type = AtkValueType.Int, Int = 1 };
        ((AtkUnitBase*)addon)->FireCallback(1, &value, true);
        return new RaiseInteraction(RaiseInteractionState.Declined, prompt);
    }

    public unsafe TrashActionAttempt TryBasicWarTrashAction(IBattleChara target)
    {
        var classJobId = objects.LocalPlayer?.ClassJob.RowId ?? 0;
        if (classJobId is not (3 or 21) || IsPlayerDead || target.IsDead || target.CurrentHp == 0)
            return new TrashActionAttempt(false, false, false, 0);

        var targeted = TargetMark(target);
        if (!targeted)
            return new TrashActionAttempt(true, false, false, 0);

        var player = objects.LocalPlayer;
        var dx = (player?.Position.X ?? 0f) - target.Position.X;
        var dz = (player?.Position.Z ?? 0f) - target.Position.Z;
        var centerDistance = MathF.Sqrt(dx * dx + dz * dz);
        var meleeRange = target.HitboxRadius + (player?.HitboxRadius ?? 0f) + 3f;
        var baseActionId = centerDistance <= meleeRange ? 31u : 46u; // Heavy Swing / Tomahawk
        var manager = ActionManager.Instance();
        if (manager is null)
            return new TrashActionAttempt(true, true, false, baseActionId);

        var adjustedActionId = manager->GetAdjustedActionId(baseActionId);
        var actionId = adjustedActionId == 0 ? baseActionId : adjustedActionId;
        if (manager->GetActionStatus(ActionType.Action, actionId, target.GameObjectId) != 0)
            return new TrashActionAttempt(true, true, false, actionId);

        return new TrashActionAttempt(
            true,
            true,
            manager->UseAction(ActionType.Action, actionId, target.GameObjectId),
            actionId);
    }

    private static bool IsRaisePrompt(string prompt) =>
        prompt.Contains("Raise", StringComparison.OrdinalIgnoreCase) ||
        prompt.Contains("resurrect", StringComparison.OrdinalIgnoreCase) ||
        prompt.Contains("revive", StringComparison.OrdinalIgnoreCase);

    public unsafe bool UseReturn()
    {
        if (!IsPlayerDead)
            return false;

        var manager = ActionManager.Instance();
        if (manager is null || GetReturnActionStatus() != 0)
            return false;

        return manager->UseAction(ActionType.Action, 6);
    }

    public unsafe uint GetReturnActionStatus()
    {
        if (!IsPlayerDead)
            return uint.MaxValue;

        var manager = ActionManager.Instance();
        return manager is null
            ? uint.MaxValue
            : manager->GetActionStatus(ActionType.Action, 6);
    }
}
