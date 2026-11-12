using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System.Runtime.InteropServices;

namespace SRankSentinel;

internal sealed partial class NativeTravel
{
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

        var node = addon->GetNodeById(WorldVisitPolicy.RegionalPanelId);
        var regional = node is not null && node->IsVisible();
        if (!regional)
        {
            // Legacy same-logical-DC client layout (e.g. a single-DC region).
            // This callback is the pre-regional behavior, only with validated legacy rows.
            var legacyList = addon->GetComponentListById(LegacyWorldListNode);
            if (legacyList is null || legacyList->OwnerNode is null || !legacyList->OwnerNode->IsVisible())
                return (VisitMenuAction.WaitForRegionalLayout, "visible World Visit window has no verified regional selector or legacy World list");
            var worlds = GetLegacyWorldRows().Select(ResolveVisitWorld).ToArray();
            var decision = WorldVisitPolicy.ChooseMenu(source, home, target, false, null, worlds);
            if (decision != VisitMenuAction.SelectWorld || !submitWorld)
                return (decision, "waiting for exact legacy World list");
            var legacyIndex = Array.FindIndex(worlds, row => row?.Id == target.Id);
            FireCallback(addon, 0, legacyIndex + 2);
            return (VisitMenuAction.WorldSubmitted, $"selected exact World {target.Name} in legacy list");
        }

        var header = addon->GetNodeById(WorldVisitPolicy.RegionalHeaderId);
        var dropdownNode = addon->GetNodeById(WorldVisitPolicy.RegionalDropdownId);
        var listNode = addon->GetNodeById(WorldVisitPolicy.RegionalWorldListId);
        // Verified against the installed 7.58 worldtranslate.uld. Node 2 is a
        // resource container, not an AtkComponentDropDownList.
        if (node->Type != NodeType.Res || header is null || dropdownNode is null || listNode is null ||
            header->ParentNode != node || dropdownNode->ParentNode != header || listNode->ParentNode != node ||
            !WorldVisitPolicy.IsRegionalLayout(header->ParentNode->NodeId,
                dropdownNode->ParentNode->NodeId, listNode->ParentNode->NodeId))
            return (VisitMenuAction.WaitForRegionalLayout, "waiting for the regional selector panel, header, dropdown and World list");

        var dropdown = dropdownNode->GetAsAtkComponentDropdownList();
        var list = listNode->GetAsAtkComponentList();
        var dcList = dropdown is null ? null : dropdown->List;
        if (dropdown is null || (AtkResNode*)dropdown->OwnerNode != dropdownNode ||
            !dropdownNode->IsVisible() || dcList is null || list is null ||
            (AtkResNode*)list->OwnerNode != listNode || dropdown->OpenStateChangePending)
            return (VisitMenuAction.WaitForRegionalLayout, "regional dropdown or World list is still initializing");

        var expectedDcs = VisitWorlds.Values.Where(row => row.RegionId == source.RegionId)
            .Select(row => row.LogicalDc).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var dcCount = expectedDcs.Length;
        var strings = GetWorldTranslateStrings();
        // Installed Lifestream 2.6.0.13 corroborates these native callbacks and
        // slots: DC labels start at zero; World labels follow DC count + 2.
        if (dcCount is < 1 or > 8 || strings is null || strings->StringArray is null ||
            strings->Size < dcCount + 2 + WorldVisitPolicy.RegionalWorldSlots ||
            dcList->GetItemCount() != dcCount)
            return (VisitMenuAction.WaitForRegionalLayout, "native World Visit DC labels are still initializing");

        var dcSlots = new string[dcCount];
        for (var i = 0; i < dcCount; i++)
            dcSlots[i] = ReadWorldTranslateString(strings, i);
        var selectedDcIndex = dropdown->GetSelectedItemIndex();
        var worldSlots = new List<VisitWorld?>();
        for (var i = 0; i < WorldVisitPolicy.RegionalWorldSlots; i++)
        {
            var name = ReadWorldTranslateString(strings, dcCount + 2 + i);
            if (name.Length == 0)
                break;
            worldSlots.Add(ResolveVisitWorld(name));
        }

        var rowCount = list->GetItemCount();
        var rendered = listNode->IsVisible() && !list->IsUpdatePending && rowCount is > 0 and <= 8
            ? new VisitWorld?[rowCount] : [];
        for (var i = 0; i < rendered.Length; i++)
            rendered[i] = ReadWorldRow(list, i);

        var (action, index) = WorldVisitPolicy.ChooseRegionalMenu(
            source, home, target, expectedDcs, dcSlots, selectedDcIndex, worldSlots, rendered);
        switch (action)
        {
            case VisitMenuAction.WaitForRegionalLayout:
                return (action, "native selector has not reported a valid selected DC and regional DC labels");
            case VisitMenuAction.SelectLogicalDataCenter:
                if (submitWorld)
                    return (action, $"waiting for the {target.LogicalDc} page");
                FireCallback(addon, 1, index);
                return (action, $"requested logical DC {target.LogicalDc} (slot {index}); waiting for World list refresh");
            case VisitMenuAction.WaitForWorldList:
                return (action, $"waiting for the selected {target.LogicalDc} page and rendered World list to agree");
            case VisitMenuAction.DestinationUnavailable:
                return (action, $"{target.Name} is absent from the verified {target.LogicalDc} World list");
            case VisitMenuAction.SelectWorld:
                if (list->GetItemDisabledState(index))
                    return (VisitMenuAction.DestinationUnavailable, $"{target.Name} is disabled on the verified {target.LogicalDc} page");
                if (!submitWorld)
                    return (action, $"verified {target.LogicalDc} World list contains {target.Name}");
                FireCallback(addon, 0, index + 2);
                return (VisitMenuAction.WorldSubmitted, $"selected exact World {target.Name} on {target.LogicalDc}");
            default:
                return (action, $"no World Visit input for {target.Name}");
        }
    }

    private static unsafe string ReadItemLabel(AtkComponentList* list, int index)
    {
        var label = list->GetItemLabel(index);
        return label.HasValue ? label.ToString().Trim() : string.Empty;
    }

    private unsafe VisitWorld? ReadWorldRow(AtkComponentList* list, int index)
    {
        // Prefer the current row renderer so an already-updated backing array
        // cannot authorize a click while an old logical DC is still on screen.
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

    private static unsafe StringArrayData* GetWorldTranslateStrings()
    {
        var module = RaptureAtkModule.Instance();
        return module is null ? null : module->AtkArrayDataHolder.StringArrays[(int)StringArrayType.WorldTranslate];
    }

    private static unsafe string ReadWorldTranslateString(StringArrayData* strings, int index)
    {
        if (strings is null || strings->StringArray is null || index < 0 || index >= strings->Size)
            return string.Empty;
        var value = strings->StringArray[index];
        return value.HasValue ? (Marshal.PtrToStringUTF8((nint)value.Value) ?? string.Empty).Trim() : string.Empty;
    }

    private static unsafe List<string> GetLegacyWorldRows()
    {
        var result = new List<string>();
        var strings = GetWorldTranslateStrings();
        for (var i = 3; i <= 10; i++)
        {
            var name = ReadWorldTranslateString(strings, i);
            if (name.Length == 0)
                break;
            result.Add(name);
        }
        return result;
    }

    public unsafe string DescribeWorldVisitMenu(string world)
    {
        var target = ResolveVisitWorld(world);
        var addon = gameGui.GetAddonByName<AtkUnitBase>("WorldTravelSelect");
        if (!IsReady(addon))
            return $"WorldTravelSelect not ready; target={target?.LogicalDc}/{world}";

        var panel = addon->GetNodeById(WorldVisitPolicy.RegionalPanelId);
        var header = addon->GetNodeById(WorldVisitPolicy.RegionalHeaderId);
        var dropdownNode = addon->GetNodeById(WorldVisitPolicy.RegionalDropdownId);
        var dropdown = dropdownNode is null ? null : dropdownNode->GetAsAtkComponentDropdownList();
        var dcList = dropdown is null ? null : dropdown->List;
        var rowNode = addon->GetNodeById(WorldVisitPolicy.RegionalWorldListId);
        var rows = rowNode is null ? null : rowNode->GetAsAtkComponentList();
        var dcCount = dcList is null ? -1 : dcList->GetItemCount();
        var selected = dropdown is null || dcList is null ? -1 : dropdown->GetSelectedItemIndex();
        var rowCount = rows is null ? -1 : rows->GetItemCount();
        var dcLabels = new List<string>();
        var rowLabels = new List<string>();
        for (var i = 0; i < Math.Clamp(dcCount, 0, 8); i++)
            dcLabels.Add(ReadItemLabel(dcList, i));
        for (var i = 0; i < Math.Clamp(rowCount, 0, 8); i++)
            rowLabels.Add(ReadWorldRow(rows, i)?.Name ?? "?");

        var strings = GetWorldTranslateStrings();
        var arraySize = strings is null ? -1 : strings->Size;
        var slots = new List<string>();
        for (var i = 0; i < Math.Clamp(arraySize, 0, 18); i++)
            slots.Add($"[{i}]={ReadWorldTranslateString(strings, i)}");

        return $"WorldTravelSelect target={target?.LogicalDc}/{world}, atkValues={addon->AtkValuesCount}; " +
            $"panel={DescribeVisitNode(panel)}, header={DescribeVisitNode(header)}, dropdownNode={DescribeVisitNode(dropdownNode)}, " +
            $"dropdown={dropdown is not null}, owner={(dropdown is null || dropdown->OwnerNode is null ? 0 : dropdown->OwnerNode->NodeId)}, " +
            $"dcList={dcList is not null}, dcCount={dcCount}, selected={selected}, dcLabels=[{string.Join(", ", dcLabels)}], " +
            $"dcEvents=[{DescribeVisitEvents(dcList, (AtkEventListener*)addon, (AtkEventListener*)dropdown)}]; " +
            $"worldNode={DescribeVisitNode(rowNode)}, worldCount={rowCount}, worldRows=[{string.Join(", ", rowLabels)}], " +
            $"worldEvents=[{DescribeVisitEvents(rows, (AtkEventListener*)addon, null)}]; " +
            $"WorldTranslate(size={arraySize})={string.Join(", ", slots)}";
    }

    private static unsafe string DescribeVisitNode(AtkResNode* node)
    {
        if (node is null)
            return "missing";
        var component = node->GetAsAtkComponentNode();
        var kind = component is null || component->Component is null
            ? "none" : component->Component->GetComponentType().ToString();
        return $"#{node->NodeId} type={(ushort)node->Type}/{kind} parent={(node->ParentNode is null ? 0 : node->ParentNode->NodeId)} visible={node->IsVisible()}";
    }

    private static unsafe string DescribeVisitEvents(AtkComponentList* list, AtkEventListener* addon, AtkEventListener* dropdown)
    {
        if (list is null || list->OwnerNode is null)
            return "none";
        var descriptions = new List<string>();
        var entry = list->OwnerNode->AtkEventManager.Event;
        for (var i = 0; entry is not null && i < 12; i++, entry = entry->NextEvent)
        {
            var owner = entry->Listener == addon ? "addon" :
                dropdown is not null && entry->Listener == dropdown ? "dropdown" :
                entry->Listener == (AtkEventListener*)list ? "list" : "other";
            descriptions.Add($"{entry->State.EventType}:{owner}");
        }
        return string.Join(", ", descriptions);
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
