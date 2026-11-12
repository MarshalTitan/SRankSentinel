using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace SRankSentinel;

internal sealed partial class NativeTravel
{
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
}
