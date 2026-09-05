using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace SRankSentinel;

internal sealed class CombatController(IGameGui gameGui, ICondition condition)
{
    public bool IsPlayerDead => condition[ConditionFlag.Unconscious];

    public static bool IsMarkInCombat(IBattleChara mark) =>
        mark.StatusFlags.HasFlag(Dalamud.Game.ClientState.Objects.Enums.StatusFlags.InCombat);

    public static float HpPercent(IBattleChara mark) =>
        mark.MaxHp == 0 ? 100f : mark.CurrentHp * 100f / mark.MaxHp;

    public unsafe bool UseSingleTag(uint actionId, IBattleChara mark)
    {
        if (actionId == 0 || IsPlayerDead || mark.IsDead || mark.CurrentHp == 0)
            return false;

        var manager = ActionManager.Instance();
        if (manager is null || manager->GetActionStatus(ActionType.Action, actionId, mark.GameObjectId) != 0)
            return false;

        return manager->UseAction(ActionType.Action, actionId, mark.GameObjectId);
    }

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
