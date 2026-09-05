using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace SRankSentinel;

internal sealed class NativeTravel(
    IGameGui gameGui,
    IObjectTable objects,
    ITargetManager targets,
    ICondition condition)
{
    public const uint UldahAetheryteId = 9;

    public bool IsBetweenAreas =>
        condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51];

    public bool IsBusy =>
        IsBetweenAreas ||
        condition[ConditionFlag.WaitingToVisitOtherWorld] ||
        condition[ConditionFlag.ReadyingVisitOtherWorld];

    public string CurrentWorld =>
        objects.LocalPlayer?.CurrentWorld.Value.Name.ToString() ?? string.Empty;

    public unsafe int CurrentInstance
    {
        get
        {
            var uiState = UIState.Instance();
            return uiState is null ? 0 : (int)uiState->PublicInstance.InstanceId;
        }
    }

    public bool IsInUldah(uint territory) => territory is 130 or 131;

    public unsafe bool Teleport(uint aetheryteId)
    {
        if (!CanUseTravelAction(5))
            return false;

        var telepo = Telepo.Instance();
        if (telepo is null)
            return false;

        telepo->UpdateAetheryteList();
        foreach (var destination in telepo->TeleportList)
        {
            if (destination.AetheryteId == aetheryteId && destination.SubIndex == 0)
                return telepo->Teleport(aetheryteId, 0);
        }

        return false;
    }

    public unsafe bool InteractWithNearbyAetheryte(float maximumDistance = 30f)
    {
        if (IsBusy || condition[ConditionFlag.Mounted] || objects.LocalPlayer is not { } player)
            return false;

        var aetheryte = objects
            .Where(o => o.ObjectKind == ObjectKind.Aetheryte && o.IsTargetable)
            .OrderBy(o => HorizontalDistance(player.Position, o.Position))
            .FirstOrDefault(o => HorizontalDistance(player.Position, o.Position) <= maximumDistance);
        if (aetheryte is null)
            return false;

        targets.Target = aetheryte;
        var targetSystem = TargetSystem.Instance();
        if (targetSystem is null)
            return false;

        targetSystem->InteractWithObject((GameObject*)aetheryte.Address, false);
        return true;
    }

    public unsafe bool SelectWorldVisitMenu() =>
        TrySelectStringEntry(text => text.Contains("Visit Another World", StringComparison.OrdinalIgnoreCase));

    public unsafe bool SelectInstanceTravelMenu() =>
        TrySelectStringEntry(text =>
            text.Contains("Travel to Instanced Area", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Change instance", StringComparison.OrdinalIgnoreCase));

    public unsafe bool SelectInstance(int instance)
    {
        if (instance is < 1 or > 9)
            return false;

        var glyph = (char)(0xE0B0 + instance);
        return TrySelectStringEntry(text => text.Contains(glyph));
    }

    public unsafe bool SelectWorld(string world)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>("WorldTravelSelect");
        if (!IsReady(addon))
            return false;

        var availableWorlds = GetAvailableWorlds();
        var index = availableWorlds.FindIndex(value => value.Equals(world, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return false;

        FireCallback(addon, 0, index + 2);
        return true;
    }

    public unsafe bool ConfirmWorldVisit(string world)
    {
        var addon = gameGui.GetAddonByName<AddonSelectYesno>("SelectYesno");
        if (addon is null || !IsReady((AtkUnitBase*)addon) || addon->YesButton is null || !addon->YesButton->IsEnabled)
            return false;

        var prompt = addon->PromptText is null ? string.Empty : addon->PromptText->NodeText.ToString();
        if (!prompt.Contains(world, StringComparison.OrdinalIgnoreCase) &&
            !prompt.Contains("World", StringComparison.OrdinalIgnoreCase))
            return false;

        FireCallback((AtkUnitBase*)addon, 0);
        return true;
    }

    public unsafe bool WorldSelectionIsOpen() => IsReady(gameGui.GetAddonByName<AtkUnitBase>("WorldTravelSelect"));

    private unsafe bool TrySelectStringEntry(Func<string, bool> predicate)
    {
        var addon = gameGui.GetAddonByName<AddonSelectString>("SelectString");
        if (addon is null || !IsReady((AtkUnitBase*)addon))
            return false;

        var menu = &addon->PopupMenu.PopupMenu;
        if (menu->EntryNames is null || menu->EntryCount <= 0)
            return false;

        for (var i = 0; i < menu->EntryCount; i++)
        {
            var text = menu->EntryNames[i].ToString();
            if (!predicate(text))
                continue;

            FireCallback((AtkUnitBase*)addon, i);
            return true;
        }

        return false;
    }

    private static unsafe List<string> GetAvailableWorlds()
    {
        var result = new List<string>();
        var module = RaptureAtkModule.Instance();
        if (module is null)
            return result;

        var strings = module->AtkArrayDataHolder.StringArrays[(int)StringArrayType.WorldTranslate];
        if (strings is null)
            return result;

        for (var i = 3; i <= 10; i++)
        {
            var value = strings->StringArray[i];
            if (!value.HasValue)
                break;

            var name = value.Value.ToString().Trim();
            if (string.IsNullOrEmpty(name))
                break;
            result.Add(name);
        }

        return result;
    }

    private unsafe bool CanUseTravelAction(uint actionId)
    {
        if (IsBusy || condition[ConditionFlag.InCombat] || condition[ConditionFlag.Unconscious] ||
            objects.LocalPlayer is not { IsTargetable: true })
            return false;

        var manager = FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance();
        return manager is not null &&
               manager->GetActionStatus(FFXIVClientStructs.FFXIV.Client.Game.ActionType.Action, actionId) == 0;
    }

    private static unsafe bool IsReady(AtkUnitBase* addon) =>
        addon is not null && addon->IsReady && addon->IsVisible;

    private static unsafe void FireCallback(AtkUnitBase* addon, params int[] arguments)
    {
        var values = stackalloc AtkValue[arguments.Length];
        for (var i = 0; i < arguments.Length; i++)
        {
            values[i].Type = AtkValueType.Int;
            values[i].Int = arguments[i];
        }

        addon->FireCallback((uint)arguments.Length, values, true);
    }

    private static float HorizontalDistance(System.Numerics.Vector3 a, System.Numerics.Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
