using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using SentinelHunts.Core;

namespace SentinelHunts.Services;

internal readonly record struct ActionAttempt(bool Attempted, bool Accepted);

internal sealed class ActionService(
    IGameGui gameGui,
    ICondition condition,
    IObjectTable objects,
    ITargetManager targets)
{
    private uint nextComboAction = 31;

    public unsafe bool TryMount() => UseGeneralAction(9);
    public unsafe bool TryDismount() => UseGeneralAction(23);

    public unsafe ActionAttempt TryTomahawk(IBattleChara mark)
    {
        const uint tomahawk = 46;
        if (condition[ConditionFlag.Unconscious] || mark.IsDead || mark.CurrentHp == 0 ||
            objects.LocalPlayer?.ClassJob.RowId is not (3 or 21))
            return new(false, false);

        targets.Target = mark;
        var manager = ActionManager.Instance();
        if (manager is null || manager->GetActionStatus(ActionType.Action, tomahawk, mark.GameObjectId) != 0)
            return new(false, false);

        return new(true, manager->UseAction(ActionType.Action, tomahawk, mark.GameObjectId));
    }

    public unsafe bool TryClearThreat(IBattleChara threat)
    {
        if (objects.LocalPlayer is not { } player || condition[ConditionFlag.Unconscious])
            return false;

        targets.Target = threat;
        var hpPercent = player.MaxHp == 0 ? 100f : player.CurrentHp * 100f / player.MaxHp;
        if (hpPercent < 45f && TryUseAction(3552, player.GameObjectId)) // Equilibrium
            return true;
        if (hpPercent < 70f && TryUseAction(25751, player.GameObjectId)) // Bloodwhetting
            return true;

        if (SafeParkingPlanner.HorizontalDistance(player.Position, threat.Position) > 5f + threat.HitboxRadius)
            return TryUseAction(46, threat.GameObjectId); // Tomahawk

        if (!TryUseAction(nextComboAction, threat.GameObjectId))
            return false;

        nextComboAction = nextComboAction switch
        {
            31 => 37, // Heavy Swing -> Maim
            37 => 42, // Maim -> Storm's Path
            _ => 31,
        };
        return true;
    }

    public unsafe bool TryAcceptRaise()
    {
        if (!condition[ConditionFlag.Unconscious])
            return false;

        var addon = gameGui.GetAddonByName<AddonSelectYesno>("SelectYesno");
        if (addon is null || !addon->IsReady || !addon->IsVisible || addon->PromptText is null ||
            addon->YesButton is null || !addon->YesButton->IsEnabled)
            return false;

        var prompt = addon->PromptText->NodeText.ToString();
        if (!prompt.Contains("Raise", StringComparison.OrdinalIgnoreCase) &&
            !prompt.Contains("resurrect", StringComparison.OrdinalIgnoreCase))
            return false;

        var value = new AtkValue { Type = AtkValueType.Int, Int = 0 };
        ((AtkUnitBase*)addon)->FireCallback(1, &value, true);
        return true;
    }

    public unsafe bool TryReturnWhileDead()
    {
        if (!condition[ConditionFlag.Unconscious])
            return false;
        if (TryConfirmReturnToUldah())
            return true;
        var manager = ActionManager.Instance();
        return manager is not null && manager->GetActionStatus(ActionType.Action, 6) == 0 &&
               manager->UseAction(ActionType.Action, 6);
    }

    private unsafe bool TryConfirmReturnToUldah()
    {
        var addon = gameGui.GetAddonByName<AddonSelectYesno>("SelectYesno");
        if (addon is null || !addon->IsReady || !addon->IsVisible || addon->PromptText is null ||
            addon->YesButton is null || !addon->YesButton->IsEnabled)
            return false;

        var prompt = addon->PromptText->NodeText.ToString().Replace('’', '\'').Replace('‘', '\'');
        if (!prompt.Contains("Return to", StringComparison.OrdinalIgnoreCase) ||
            (!prompt.Contains("Ul'dah", StringComparison.OrdinalIgnoreCase) &&
             !prompt.Contains("Steps of Nald", StringComparison.OrdinalIgnoreCase)))
            return false;

        var value = new AtkValue { Type = AtkValueType.Int, Int = 0 };
        ((AtkUnitBase*)addon)->FireCallback(1, &value, true);
        return true;
    }

    private static unsafe bool TryUseAction(uint actionId, ulong targetId)
    {
        var manager = ActionManager.Instance();
        return manager is not null && manager->GetActionStatus(ActionType.Action, actionId, targetId) == 0 &&
               manager->UseAction(ActionType.Action, actionId, targetId);
    }

    private static unsafe bool UseGeneralAction(uint actionId)
    {
        var manager = ActionManager.Instance();
        return manager is not null && manager->UseAction(ActionType.GeneralAction, actionId);
    }
}
