using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace SRankSentinel;

internal readonly record struct TagAttemptResult(bool Attempted, bool Accepted);

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

    public unsafe uint ResolveTagActionId(bool automatic, uint configuredActionId)
    {
        var actionId = automatic ? GetBaseRangedTagAction(objects.LocalPlayer?.ClassJob.RowId ?? 0) : configuredActionId;
        if (actionId == 0)
            return 0;

        var manager = ActionManager.Instance();
        if (manager is null)
            return actionId;

        var adjustedActionId = manager->GetAdjustedActionId(actionId);
        return adjustedActionId == 0 ? actionId : adjustedActionId;
    }

    public void TargetMark(IBattleChara mark) => targets.Target = mark;

    public unsafe TagAttemptResult TrySingleTag(uint actionId, IBattleChara mark)
    {
        if (actionId == 0 || IsPlayerDead || mark.IsDead || mark.CurrentHp == 0)
            return new TagAttemptResult(false, false);

        TargetMark(mark);

        var manager = ActionManager.Instance();
        if (manager is null || manager->GetActionStatus(ActionType.Action, actionId, mark.GameObjectId) != 0)
            return new TagAttemptResult(false, false);

        // Once UseAction is invoked, the caller permanently closes the attack gate for this mark.
        // The return value only tells us whether the client accepted that single attempt.
        var accepted = manager->UseAction(ActionType.Action, actionId, mark.GameObjectId);
        return new TagAttemptResult(true, accepted);
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

    public unsafe bool TryAcceptRaise()
    {
        if (!IsPlayerDead)
            return false;

        var addon = gameGui.GetAddonByName<AddonSelectYesno>("SelectYesno");
        if (addon is null || !addon->IsReady || !addon->IsVisible ||
            addon->PromptText is null || addon->YesButton is null || !addon->YesButton->IsEnabled)
            return false;

        var prompt = addon->PromptText->NodeText.ToString();
        if (!prompt.Contains("Raise", StringComparison.OrdinalIgnoreCase) &&
            !prompt.Contains("resurrect", StringComparison.OrdinalIgnoreCase))
            return false;

        var value = new AtkValue { Type = AtkValueType.Int, Int = 0 };
        ((AtkUnitBase*)addon)->FireCallback(1, &value, true);
        return true;
    }

    public unsafe bool UseReturn()
    {
        if (!IsPlayerDead)
            return false;

        var manager = ActionManager.Instance();
        if (manager is null || manager->GetActionStatus(ActionType.Action, 6) != 0)
            return false;

        return manager->UseAction(ActionType.Action, 6);
    }
}
