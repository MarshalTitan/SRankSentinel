using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using System.Numerics;

namespace SRankSentinel;

public sealed partial class Plugin
{
    private readonly TagGroundRouteRecovery tagGroundRoute = new();

    private bool HoldLiveTagGroundRecovery(IBattleChara target) =>
        tagGroundRoute.DirectFailures > 0 && IsExactCheckpointMark(target) &&
        !target.IsDead && target.CurrentHp > 0 && CombatController.IsMarkInCombat(target);

    private void TickTagGroundApproach(IBattleChara target, float clearance, DateTime now)
    {
        if (clearance > TagApproachClearance + 1f && now < nextActionUtc)
            return;
        var frame = new TagGroundRouteFrame(PlayerPosition(), target.Position,
            target.HitboxRadius + (objects.LocalPlayer?.HitboxRadius ?? 0f), clearance,
            CanActAfterRevival() && tagCycle.CanDispatch(IsExactCheckpointMark(target),
                !target.IsDead && target.CurrentHp > 0, CombatController.IsMarkInCombat(target),
                CombatController.HpPercent(target), ActiveDistanceProfile.EngageHpPercent));
        var step = tagGroundRoute.Tick(now, frame, new TagGroundNavigation(vnav, log));
        status = step switch
        {
            TagGroundRouteStep.Searching => $"Tag required: validating alternate ground routes around {target.Name.TextValue}",
            TagGroundRouteStep.Following => $"Tag required: following a validated alternate ground route to {target.Name.TextValue}",
            TagGroundRouteStep.Holding => $"Tag required: no reachable ground approach yet; holding the live hunt and checkpoint",
            TagGroundRouteStep.Retry => $"Tag required: failed ground route stopped; preparing another approach",
            _ => $"Tag required: approaching {target.Name.TextValue} for one confirmed ranged action ({clearance:0.0}y clear)",
        };
    }

    private sealed class TagGroundNavigation(VNavmeshIpc vnav, IPluginLog log) : ITagGroundNavigation
    {
        public bool RouteRunning => vnav.IsPathRunningSafe() || vnav.IsPathfindInProgressSafe();
        public bool QueryRunning => vnav.IsNavPathfindInProgressSafe();
        public bool Ready => vnav.IsReadySafe();
        public bool Direct(Vector3 mark, float range) => vnav.MoveCloseToSafe(mark, false, range);
        public Vector3? Floor(Vector3 sample) => vnav.PointOnFloorSafe(sample, 2f);
        public Task<List<Vector3>>? FindPath(Vector3 from, Vector3 to) => vnav.PathfindSafe(from, to, false);
        public bool Follow(List<Vector3> path) => vnav.MovePathSafe(path, false);
        public void Stop() => vnav.StopSafe("tag ground route recovery");
        public void Report(string message) => log.Information("Tag ground routing: {Detail}", message);
    }
}
