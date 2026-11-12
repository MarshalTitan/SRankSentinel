using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System.Numerics;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace SRankSentinel;

internal sealed partial class NativeTravel(
    IGameGui gameGui,
    IObjectTable objects,
    ITargetManager targets,
    ICondition condition,
    IDataManager data)
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

    // World Visit is available at the main Steps of Nald aetheryte (territory 130).
    public bool IsInUldah(uint territory) => territory == 130;

    public unsafe bool CanTeleportTo(uint aetheryteId)
    {
        var telepo = Telepo.Instance();
        if (telepo is null || aetheryteId == 0)
            return false;

        telepo->UpdateAetheryteList();
        foreach (var destination in telepo->TeleportList)
            if (destination.AetheryteId == aetheryteId && destination.SubIndex == 0)
                return true;

        return false;
    }

    public unsafe bool Teleport(uint aetheryteId)
    {
        if (!CanUseTravelAction(5))
            return false;

        var telepo = Telepo.Instance();
        if (telepo is null || !CanTeleportTo(aetheryteId))
            return false;
        return telepo->Teleport(aetheryteId, 0);
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

        targetSystem->InteractWithObject((CSGameObject*)aetheryte.Address, false);
        return true;
    }

    internal sealed record InstanceAetheryteTarget(ulong ObjectId, uint BaseId, string Name, Vector3 Position);

    public InstanceAetheryteTarget? ResolveInstanceAetheryte(InstanceAetheryteTarget? intended, Vector3 arrivalOrigin)
    {
        if (objects.LocalPlayer is null || IsBusy)
            return null;
        // Pin the nearest local crystal on arrival. A reload may replace its object ID,
        // but must retain the same data ID and location; never chase another crystal.
        var local = objects.Where(o => o.ObjectKind == ObjectKind.Aetheryte && o.IsTargetable &&
            Vector3.Distance(o.Position, arrivalOrigin) <= 25f);
        var resolved = intended is null
            ? local.OrderBy(o => Vector3.Distance(o.Position, arrivalOrigin)).FirstOrDefault()
            : local.FirstOrDefault(o => o.GameObjectId == intended.ObjectId && o.BaseId == intended.BaseId)
              ?? local.FirstOrDefault(o => o.BaseId == intended.BaseId &&
                  Vector3.Distance(o.Position, intended.Position) <= 2f);
        return resolved is null ? null :
            new(resolved.GameObjectId, resolved.BaseId, resolved.Name.TextValue, resolved.Position);
    }

    public bool InstanceTargetIsSelected(InstanceAetheryteTarget? intended) =>
        intended is not null && targets.Target?.GameObjectId == intended.ObjectId;

    public unsafe bool InteractWithInstanceAetheryte(InstanceAetheryteTarget intended, float range)
    {
        if (IsBusy || SelectionMenuIsOpen() || condition[ConditionFlag.Mounted] ||
            condition[ConditionFlag.InFlight] || objects.LocalPlayer is not { } player)
            return false;
        var target = objects.FirstOrDefault(o => o.GameObjectId == intended.ObjectId &&
            o.BaseId == intended.BaseId && o.ObjectKind == ObjectKind.Aetheryte && o.IsTargetable &&
            Vector3.Distance(o.Position, intended.Position) <= 1f &&
            Vector3.Distance(player.Position, o.Position) <= range);
        var system = TargetSystem.Instance();
        if (target is null || system is null)
            return false;
        targets.Target = target;
        system->InteractWithObject((CSGameObject*)target.Address, false);
        return true; // Dispatched only: the caller waits for the real selection UI.
    }

    public unsafe bool SelectionMenuIsOpen() =>
        IsReady(gameGui.GetAddonByName<AtkUnitBase>("SelectString"));

    public bool InstanceTravelMenuIsOpen() => StringMenuContains(text =>
        text.Contains("Travel to Instanced Area", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Change instance", StringComparison.OrdinalIgnoreCase));

    public bool InstanceSelectionIsOpen(int requested) => StringMenuContains(text =>
        text.Any(character => character is >= '\uE0B1' and <= '\uE0B9'));

    private unsafe bool StringMenuContains(Func<string, bool> predicate)
    {
        var addon = gameGui.GetAddonByName<AddonSelectString>("SelectString");
        if (addon is null || !IsReady((AtkUnitBase*)addon))
            return false;
        var menu = &addon->PopupMenu.PopupMenu;
        if (menu->EntryNames is null || menu->EntryCount <= 0)
            return false;
        for (var i = 0; i < menu->EntryCount; i++)
            if (predicate(menu->EntryNames[i].ToString()))
                return true;
        return false;
    }

    public unsafe bool SelectInstanceTravelMenu() =>
        TrySelectStringEntry(text =>
            text.Contains("Travel to Instanced Area", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Change instance", StringComparison.OrdinalIgnoreCase));

    public unsafe bool SelectAethernetTravelMenu() =>
        TrySelectStringEntry(text =>
            text.Contains("aethernet destination", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("Aethernet", StringComparison.OrdinalIgnoreCase));

    public unsafe bool SelectIdyllshireWesternGate() =>
        TrySelectStringEntry(text =>
            text.Contains("Prologue Gate", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Western Hinterlands", StringComparison.OrdinalIgnoreCase));

    public unsafe bool SelectInstance(int instance)
    {
        if (instance is < 1 or > 9)
            return false;

        var glyph = (char)(0xE0B0 + instance);
        return TrySelectStringEntry(text => text.Contains(glyph));
    }

    public unsafe bool ReturnConfirmationIsOpen(out string prompt)
    {
        prompt = string.Empty;
        var addon = gameGui.GetAddonByName<AddonSelectYesno>("SelectYesno");
        if (addon is null || !IsReady((AtkUnitBase*)addon) || addon->PromptText is null)
            return false;

        prompt = addon->PromptText->NodeText.ToString();
        return IsReturnToUldahPrompt(prompt);
    }

    public unsafe bool ConfirmReturnToUldah()
    {
        var addon = gameGui.GetAddonByName<AddonSelectYesno>("SelectYesno");
        if (addon is null || !IsReady((AtkUnitBase*)addon) || addon->PromptText is null ||
            addon->YesButton is null || !addon->YesButton->IsEnabled)
            return false;

        var prompt = addon->PromptText->NodeText.ToString();
        if (!IsReturnToUldahPrompt(prompt))
            return false;

        FireCallback((AtkUnitBase*)addon, 0);
        return true;
    }

    // World travel is owned by Lifestream; only local interaction menus are handled here.

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

    private static bool IsReturnToUldahPrompt(string prompt)
    {
        var normalized = prompt.Replace('’', '\'').Replace('‘', '\'');
        return normalized.Contains("Return to", StringComparison.OrdinalIgnoreCase) &&
               (normalized.Contains("Ul'dah", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("Steps of Nald", StringComparison.OrdinalIgnoreCase));
    }

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
