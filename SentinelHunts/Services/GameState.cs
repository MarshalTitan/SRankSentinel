using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using SentinelHunts.Core;
using System.Numerics;

namespace SentinelHunts.Services;

internal sealed class GameState(
    IClientState clientState,
    ICondition condition,
    IObjectTable objects,
    IDataManager data)
{
    public IPlayerCharacter? Player => objects.LocalPlayer;
    public bool IsLoggedIn => clientState.IsLoggedIn && Player is not null;
    public uint TerritoryId => clientState.TerritoryType;
    public bool IsDead => condition[ConditionFlag.Unconscious];
    public bool IsMounted => condition[ConditionFlag.Mounted];
    public bool IsInFlight => condition[ConditionFlag.InFlight];
    public bool IsBetweenAreas => condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51];
    public bool IsTravelBusy => IsBetweenAreas ||
                                condition[ConditionFlag.WaitingToVisitOtherWorld] ||
                                condition[ConditionFlag.ReadyingVisitOtherWorld];
    public bool IsReady => IsLoggedIn && !IsDead && !IsTravelBusy;
    public uint CurrentWorldId => Player?.CurrentWorld.RowId ?? 0;
    public string CurrentWorldName => Player?.CurrentWorld.Value.Name.ToString() ?? string.Empty;
    public Vector3 PlayerPosition => Player?.Position ?? Vector3.Zero;
    public float PlayerHitboxRadius => Player?.HitboxRadius ?? 0.5f;
    public bool IsWarrior => Player?.ClassJob.RowId is 3 or 21;

    public unsafe byte CurrentInstance
    {
        get
        {
            var state = UIState.Instance();
            return state is null ? (byte)0 : (byte)state->PublicInstance.InstanceId;
        }
    }

    public IBattleChara? FindMark(uint dataId) => objects
        .OfType<IBattleChara>()
        .Where(actor => actor.ObjectKind == ObjectKind.BattleNpc && actor.BaseId == dataId)
        .OrderBy(actor => actor.IsDead || actor.CurrentHp == 0 ? 1 : 0)
        .ThenBy(actor => SafeParkingPlanner.HorizontalDistance(PlayerPosition, actor.Position))
        .FirstOrDefault();

    public IBattleChara? FindIncidentalThreat(ulong excludedGameObjectId)
    {
        if (Player is not { } player)
            return null;

        return objects
            .OfType<IBattleChara>()
            .Where(actor => actor.ObjectKind == ObjectKind.BattleNpc && actor.GameObjectId != excludedGameObjectId)
            .Where(actor => !actor.IsDead && actor.CurrentHp > 0 && actor.IsTargetable)
            .Where(actor => actor.TargetObjectId == player.GameObjectId)
            .OrderBy(actor => SafeParkingPlanner.HorizontalDistance(player.Position, actor.Position))
            .FirstOrDefault();
    }

    public bool IsSameDataCenter(uint worldId)
    {
        if (Player is not { } player || worldId == 0)
            return false;

        var world = data.GetExcelSheet<World>().FirstOrDefault(row => row.RowId == worldId);
        return world.RowId != 0 && world.DataCenter.RowId != 0 &&
               player.CurrentWorld.Value.DataCenter.RowId == world.DataCenter.RowId;
    }

    public (uint Id, string Name)? ResolveWorld(string text)
    {
        var normalized = SentinelHunts.Core.HuntCatalog.Normalize(text);
        foreach (var world in data.GetExcelSheet<World>())
        {
            var name = world.Name.ToString();
            if (!string.IsNullOrWhiteSpace(name) && normalized.Contains(
                    SentinelHunts.Core.HuntCatalog.Normalize(name), StringComparison.Ordinal))
                return (world.RowId, name);
        }

        return null;
    }

    public uint ResolveTerritoryFromAetheryteName(string placeName)
    {
        if (string.IsNullOrWhiteSpace(placeName))
            return 0;

        var normalized = SentinelHunts.Core.HuntCatalog.Normalize(placeName);
        foreach (var aetheryte in data.GetExcelSheet<Aetheryte>())
        {
            if (!aetheryte.IsAetheryte || !aetheryte.PlaceName.IsValid)
                continue;
            if (SentinelHunts.Core.HuntCatalog.Normalize(aetheryte.PlaceName.Value.Name.ToString()) == normalized)
                return aetheryte.Territory.RowId;
        }

        return 0;
    }

    public Vector3? MapToWorld(uint territoryId, float mapX, float mapY)
    {
        if (Player is null || mapX is <= 0 or > 50 || mapY is <= 0 or > 50)
            return null;

        var map = data.GetExcelSheet<Lumina.Excel.Sheets.Map>()
            .FirstOrDefault(row => row.TerritoryType.RowId == territoryId);
        if (map.RowId == 0)
            return null;

        var link = new MapLinkPayload(territoryId, map.RowId, mapX, mapY);
        return new Vector3(link.RawX / 1000f, Player.Position.Y, link.RawY / 1000f);
    }
}
