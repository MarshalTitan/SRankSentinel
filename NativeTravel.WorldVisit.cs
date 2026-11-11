using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System.Runtime.InteropServices;

namespace SRankSentinel;

internal sealed partial class NativeTravel
{
    // WorldTravelSelect regional layout introduced in 7.57 (SimpleTweaks' cross-DC
    // World Visit layout audit). Validate live component types, labels and event ownership
    // on 7.58 before emitting input. Regional selection never uses the old row callback.
    private const uint LogicalDcSelectorNode = 2;
    private const uint RegionalWorldListNode = 7;
    private const uint LegacyWorldListNode = 20;

    private Dictionary<uint, VisitWorld>? visitWorlds;
    private VisitWorld? lastVisitWorld;
    private VisitWorld? lastHomeWorld;

    private Dictionary<uint, VisitWorld> VisitWorlds => visitWorlds ??= LoadVisitWorlds();

    private Dictionary<uint, VisitWorld> LoadVisitWorlds()
    {
        var centers = data.GetExcelSheet<WorldDCGroupType>();
        var worlds = new Dictionary<uint, VisitWorld>();
        foreach (var world in data.GetExcelSheet<World>())
        {
            if (!world.IsPublic || world.RowId == 0)
                continue;
            // API 15 Lumina names World.LogicalDataCenter "Unknown0". DataCenter
            // can be the shared matchmaking group on 7.58 (e.g. Crystal -> Aether).
            var logicalDcId = world.Unknown0 != 0 ? (uint)world.Unknown0 : world.DataCenter.RowId;
            if (logicalDcId == 0 || !centers.TryGetRow(logicalDcId, out var dc) || dc.IsCloud)
                continue;
            var name = world.Name.ToString();
            var dcName = dc.Name.ToString();
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(dcName))
                worlds[world.RowId] = new(world.RowId, name, logicalDcId, dcName, dc.Region.RowId);
        }
        return worlds;
    }

    public VisitWorld? ResolveVisitWorld(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null :
        VisitWorlds.Values.FirstOrDefault(world => world.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    public void RefreshCurrentDataCenter()
    {
        if (objects.LocalPlayer is not { } player || IsBetweenAreas)
            return;
        if (VisitWorlds.TryGetValue(player.CurrentWorld.RowId, out var visited))
            lastVisitWorld = visited;
        if (VisitWorlds.TryGetValue(player.HomeWorld.RowId, out var home))
            lastHomeWorld = home;
    }

    public bool HasWorldVisitContext
    {
        get
        {
            RefreshCurrentDataCenter();
            return lastVisitWorld is not null && lastHomeWorld is not null;
        }
    }

    public VisitWorld? CurrentVisitWorld
    {
        get
        {
            RefreshCurrentDataCenter();
            return lastVisitWorld;
        }
    }

    public bool IsWorldVisitCompatible(string world) =>
        WorldVisitPolicy.CanVisit(CurrentVisitWorld, lastHomeWorld, ResolveVisitWorld(world));

    // The optional Faloop feed retains its existing logical-DC subscription/filter.
    public bool TryGetDataCenterRelationship(string world, out string currentDataCenter,
        out string eventDataCenter, out bool isSameDataCenter)
    {
        var source = CurrentVisitWorld;
        var target = ResolveVisitWorld(world);
        currentDataCenter = source?.LogicalDc ?? string.Empty;
        eventDataCenter = target?.LogicalDc ?? string.Empty;
        isSameDataCenter = source is not null && target is not null && source.LogicalDcId == target.LogicalDcId;
        return source is not null && target is not null;
    }

    public string GetDataCenterSlug(string world) =>
        new((ResolveVisitWorld(world)?.LogicalDc ?? string.Empty)
            .Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public unsafe bool TryReadLiveInstance(out int instance)
    {
        instance = 0;
        var uiState = UIState.Instance();
        if (uiState is null || IsBusy || objects.LocalPlayer is not { IsTargetable: true })
            return false;
        instance = (int)uiState->PublicInstance.InstanceId;
        return instance is >= 0 and <= 9;
    }

    public unsafe (VisitMenuAction Action, string Detail) StepWorldVisitMenu(string world, bool submitWorld)
    {
        var source = CurrentVisitWorld;
        var target = ResolveVisitWorld(world);
        if (source is null || lastHomeWorld is not { } home || target is null ||
            !WorldVisitPolicy.CanVisit(source, home, target))
            return (VisitMenuAction.Ineligible, "destination is outside the current physical region");
        if (source.Id == target.Id && CurrentWorld.Equals(world, StringComparison.OrdinalIgnoreCase))
            return (VisitMenuAction.AlreadyCurrent, "already on the exact destination World");

        var addon = gameGui.GetAddonByName<AtkUnitBase>("WorldTravelSelect");
        if (!IsReady(addon) || IsBusy)
            return (VisitMenuAction.WaitForSelector, "waiting for the native World Visit window");

        var node = addon->GetNodeById(LogicalDcSelectorNode);
        var regional = node is not null && node->IsVisible();
        if (!regional)
        {
            // Legacy same-logical-DC client layout (e.g. a single-DC region).
            // This callback is the pre-regional behavior, only with validated legacy rows.
            var legacyList = addon->GetComponentListById(LegacyWorldListNode);
            if (legacyList is null || legacyList->OwnerNode is null || !legacyList->OwnerNode->IsVisible())
                return (VisitMenuAction.WaitForSelector, "waiting for a verified regional selector or legacy World list");
            var worlds = GetLegacyWorldRows().Select(ResolveVisitWorld).ToArray();
            var decision = WorldVisitPolicy.ChooseMenu(source, home, target, false, null, worlds);
            if (decision != VisitMenuAction.SelectWorld || !submitWorld)
                return (decision, "waiting for exact legacy World list");
            var index = Array.FindIndex(worlds, row => row?.Id == target.Id);
            FireCallback(addon, 0, index + 2);
            return (VisitMenuAction.WorldSubmitted, $"selected exact World {target.Name} in legacy list");
        }

        var dropdown = node->GetAsAtkComponentDropdownList();
        var dcList = dropdown is null ? null : dropdown->List;
        if (dropdown is null || (AtkResNode*)dropdown->OwnerNode != node || dcList is null ||
            dcList->GetItemCount() is < 1 or > 8 || dropdown->OpenStateChangePending)
            return (VisitMenuAction.WaitForSelector, "regional DC selector is not ready or has an unsupported layout");

        var count = dcList->GetItemCount();
        var dcLabels = new string[count];
        for (var i = 0; i < count; i++)
            dcLabels[i] = ReadItemLabel(dcList, i);
        if (dcLabels.Distinct(StringComparer.OrdinalIgnoreCase).Count() != count ||
            dcLabels.Any(label => !VisitWorlds.Values.Any(row => row.RegionId == source.RegionId &&
                row.LogicalDc.Equals(label, StringComparison.OrdinalIgnoreCase))))
            return (VisitMenuAction.WaitForSelector, "native DC selector labels do not match this region's game data");

        var selected = dropdown->GetSelectedItemIndex();
        var selectedDc = selected >= 0 && selected < count ? dcLabels[selected] : null;
        if (!string.Equals(selectedDc, target.LogicalDc, StringComparison.OrdinalIgnoreCase))
        {
            if (submitWorld)
                return (VisitMenuAction.SelectLogicalDataCenter, $"waiting for the {target.LogicalDc} page");
            var index = Array.FindIndex(dcLabels, label => label.Equals(target.LogicalDc, StringComparison.OrdinalIgnoreCase));
            if (index < 0 || dcList->GetItemDisabledState(index) || !HasOwnedClick(dcList, (AtkEventListener*)dropdown))
                return (VisitMenuAction.WaitForSelector, $"{target.LogicalDc} is unavailable or the selector event could not be verified");
            dropdown->SelectItem(index);
            if (dropdown->GetSelectedItemIndex() == index &&
                ReadItemLabel(dcList, index).Equals(target.LogicalDc, StringComparison.OrdinalIgnoreCase) &&
                HasOwnedClick(dcList, (AtkEventListener*)dropdown))
            {
                dcList->DispatchItemEvent(index, AtkEventType.ListItemClick);
                return (VisitMenuAction.SelectLogicalDataCenter, $"requested logical DC {target.LogicalDc}; waiting for World list refresh");
            }
            return (VisitMenuAction.WaitForSelector, "DC selector changed during revalidation");
        }

        var list = addon->GetComponentListById(RegionalWorldListNode);
        if (list is null || list->OwnerNode is null || !list->OwnerNode->IsVisible() ||
            list->GetItemCount() is < 1 or > 16 || list->IsUpdatePending)
            return (VisitMenuAction.WaitForWorldList, $"waiting for the rendered {target.LogicalDc} World list");
        var rows = new VisitWorld?[list->GetItemCount()];
        for (var i = 0; i < rows.Length; i++)
            rows[i] = ReadWorldRow(list, i);
        var action = WorldVisitPolicy.ChooseMenu(source, home, target, true, selectedDc, rows);
        if (action != VisitMenuAction.SelectWorld || !submitWorld)
            return (action, action == VisitMenuAction.SelectWorld
                ? $"verified {target.LogicalDc} World list contains {target.Name}"
                : $"waiting for the World list to refresh to {target.LogicalDc}");
        var worldIndex = Array.FindIndex(rows, row => row?.Id == target.Id);
        if (list->GetItemDisabledState(worldIndex) || !HasOwnedClick(list, (AtkEventListener*)addon))
            return (VisitMenuAction.WaitForWorldList, $"{target.Name} is unavailable or the World row event could not be verified");
        list->SelectItem(worldIndex, false);
        if (list->SelectedItemIndex != worldIndex || ReadWorldRow(list, worldIndex)?.Id != target.Id ||
            dropdown->GetSelectedItemIndex() != selected || !HasOwnedClick(list, (AtkEventListener*)addon))
            return (VisitMenuAction.WaitForWorldList, "World row changed during revalidation");
        list->DispatchItemEvent(worldIndex, AtkEventType.ListItemClick);
        return (VisitMenuAction.WorldSubmitted, $"selected exact World {target.Name} on {target.LogicalDc}");
    }

    private static unsafe string ReadItemLabel(AtkComponentList* list, int index)
    {
        var label = list->GetItemLabel(index);
        return label.HasValue ? label.ToString().Trim() : string.Empty;
    }

    private unsafe VisitWorld? ReadWorldRow(AtkComponentList* list, int index)
    {
        var label = list->GetItemLabel(index);
        if (label.HasValue && ResolveVisitWorld(label.ToString()) is { } named)
            return named;

        // WorldTravelSelect can render text via a row populator instead of ItemLabels.
        var renderer = list->GetItemRenderer(index);
        if (renderer is null || renderer->OwnerNode is null || !renderer->OwnerNode->IsVisible() ||
            renderer->UldManager.NodeList is null || renderer->UldManager.NodeListCount > 128)
            return null;
        VisitWorld? resolved = null;
        for (var i = 0; i < renderer->UldManager.NodeListCount; i++)
        {
            var node = renderer->UldManager.NodeList[i];
            if (node is null || !node->IsVisible())
                continue;
            var text = node->GetAsAtkTextNode();
            if (text is null || ResolveVisitWorld(text->NodeText.ToString()) is not { } candidate)
                continue;
            if (resolved is not null && resolved.Id != candidate.Id)
                return null;
            resolved = candidate;
        }
        return resolved;
    }

    private static unsafe bool HasOwnedClick(AtkComponentList* list, AtkEventListener* listener)
    {
        if (list is null || list->OwnerNode is null)
            return false;
        var entry = list->OwnerNode->AtkEventManager.Event;
        var matches = 0;
        for (var i = 0; entry is not null && i < 32; i++, entry = entry->NextEvent)
        {
            if (entry->State.EventType != AtkEventType.ListItemClick)
                continue;
            if (entry->Listener != listener || ++matches > 1)
                return false;
        }
        return entry is null && matches == 1;
    }

    private static unsafe List<string> GetLegacyWorldRows()
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
            var name = (Marshal.PtrToStringUTF8((nint)value.Value) ?? string.Empty).Trim();
            if (name.Length == 0)
                break;
            result.Add(name);
        }
        return result;
    }

    public unsafe bool ConfirmWorldVisit(string world)
    {
        var addon = gameGui.GetAddonByName<AddonSelectYesno>("SelectYesno");
        if (addon is null || !IsReady((AtkUnitBase*)addon) || addon->PromptText is null ||
            addon->YesButton is null || !addon->YesButton->IsEnabled || IsBusy ||
            !WorldVisitPolicy.IsConfirmationFor(addon->PromptText->NodeText.ToString(), world))
            return false;
        FireCallback((AtkUnitBase*)addon, 0);
        return true;
    }

    public unsafe void CloseWorldVisitMenus(string world)
    {
        if (IsBusy)
            return;
        var confirm = gameGui.GetAddonByName<AddonSelectYesno>("SelectYesno");
        if (confirm is not null && IsReady((AtkUnitBase*)confirm) && confirm->PromptText is not null &&
            WorldVisitPolicy.IsConfirmationFor(confirm->PromptText->NodeText.ToString(), world))
            ((AtkUnitBase*)confirm)->Close(true);
        var selection = gameGui.GetAddonByName<AtkUnitBase>("WorldTravelSelect");
        if (IsReady(selection))
            selection->Close(true);
    }
}
