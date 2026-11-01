using System.Numerics;

namespace SRankSentinel;

internal enum InstanceChangeAction
{
    ResumeHunt, WaitForTransition, Hold, Retry, WaitForMenu, OpenInstanceMenu,
    SelectInstance, WaitForTarget, Dismount, WaitForMesh, Approach, WaitForApproach, Interact,
}

internal readonly record struct InstanceChangeObservation(
    bool CorrectInstance, bool Busy, bool MainMenu, bool InstanceMenu, bool AnyMenu,
    bool TargetAvailable, bool InRange, bool Mounted, bool MeshReady);

internal sealed record InstanceApproachPlan(
    IReadOnlyList<Vector3> Points, int ProjectionMisses, int LocalityRejections, int RangeRejections);

/// <summary>Bounded local interaction recovery; menu/zoning/arrival always preempt movement.</summary>
internal sealed class InstanceChangeRecovery
{
    private enum Phase { Resolve, Approach, Interaction, InstanceMenu, Transition }
    private Phase phase;
    private DateTime startedUtc;
    private DateTime phaseDeadlineUtc;
    private DateTime missingSinceUtc;
    public int Failures { get; private set; }
    public const int MaximumFailures = 3;
    public string LastFailure { get; private set; } = string.Empty;
    public float InteractionRange => Failures == 0 ? 8f : 6.5f;

    public void Reset()
    {
        phase = Phase.Resolve;
        startedUtc = phaseDeadlineUtc = missingSinceUtc = DateTime.MinValue;
        Failures = 0;
        LastFailure = string.Empty;
    }

    public void Submitted(InstanceChangeAction action, DateTime now)
    {
        phase = action switch
        {
            InstanceChangeAction.Approach => Phase.Approach,
            InstanceChangeAction.Interact => Phase.Interaction,
            InstanceChangeAction.OpenInstanceMenu => Phase.InstanceMenu,
            InstanceChangeAction.SelectInstance => Phase.Transition,
            _ => Phase.Resolve,
        };
        phaseDeadlineUtc = now.AddSeconds(action == InstanceChangeAction.Approach ? 15 :
            action == InstanceChangeAction.SelectInstance ? 25 : 4);
    }

    public InstanceChangeAction Failed(string reason)
    {
        Failures++;
        LastFailure = reason;
        phase = Phase.Resolve;
        missingSinceUtc = DateTime.MinValue;
        return Failures >= MaximumFailures ? InstanceChangeAction.Hold : InstanceChangeAction.Retry;
    }

    public void Block(string reason)
    {
        Failures = MaximumFailures;
        LastFailure = reason;
    }

    public static InstanceApproachPlan PlanLocalApproach(Vector3 origin, Vector3 player,
        Vector3 target, float interactionRange, Func<Vector3, float, Vector3?> projectFloor)
    {
        var points = new List<Vector3>();
        var misses = 0;
        var nonLocal = 0;
        var outOfRange = 0;
        var away = player - target;
        away.Y = 0;
        away = away.LengthSquared() < 0.01f ? Vector3.UnitX : Vector3.Normalize(away);
        foreach (var radius in new[] { interactionRange - 2f, interactionRange - 3.5f })
        foreach (var angle in new[] { 0f, 25f, -25f, 50f, -50f, 75f, -75f, 100f, -100f })
        {
            var radians = angle * MathF.PI / 180f;
            var direction = new Vector3(away.X * MathF.Cos(radians) - away.Z * MathF.Sin(radians),
                0, away.X * MathF.Sin(radians) + away.Z * MathF.Cos(radians));
            var sample = target + direction * radius;
            // PointOnFloor excludes polygons above the query Y. Query from above the
            // player's ground so a raised crystal plinth is not rejected before pathfinding.
            sample.Y = player.Y + 4f;
            var floor = projectFloor(sample, 2f);
            if (floor is null)
            {
                misses++;
                continue;
            }
            var point = floor.Value;
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z) ||
                Vector3.Distance(player, point) > 15f || Vector3.Distance(origin, point) > 20f ||
                MathF.Abs(point.Y - player.Y) > 3f)
            {
                nonLocal++;
                continue;
            }
            // Leave room for normal path-follower endpoint tolerance before interacting.
            if (Vector3.Distance(point, target) > interactionRange - 1.5f)
            {
                outOfRange++;
                continue;
            }
            if (!points.Any(existing => Vector3.Distance(existing, point) < 0.75f))
                points.Add(point);
        }
        return new(points.OrderBy(point => Vector3.Distance(player, point)).ToArray(),
            misses, nonLocal, outOfRange);
    }

    public static bool IsLocalPath(IReadOnlyList<Vector3> path, Vector3 origin, Vector3 player, Vector3 target)
    {
        if (path.Count == 0 || Vector3.Distance(player, path[0]) > 3f ||
            Vector3.Distance(path[^1], target) > 8f)
            return false;
        var length = 0f;
        var previous = player;
        foreach (var point in path)
        {
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z) ||
                Vector3.Distance(point, origin) > 20f)
                return false;
            length += Vector3.Distance(previous, point);
            previous = point;
        }
        return length <= 25f;
    }

    public InstanceChangeAction Decide(DateTime now, InstanceChangeObservation input)
    {
        if (input.CorrectInstance)
            return InstanceChangeAction.ResumeHunt;
        if (startedUtc == DateTime.MinValue)
            startedUtc = now;
        if (input.Busy)
            return InstanceChangeAction.WaitForTransition;
        if (Failures >= MaximumFailures || (now - startedUtc).TotalSeconds >= 90)
            return InstanceChangeAction.Hold;

        if (input.InstanceMenu)
        {
            if (phase != Phase.Transition)
                return InstanceChangeAction.SelectInstance;
            return now < phaseDeadlineUtc ? InstanceChangeAction.WaitForMenu :
                Failed("requested instance did not begin changing after selection");
        }
        if (input.MainMenu)
        {
            if (phase != Phase.InstanceMenu)
                return InstanceChangeAction.OpenInstanceMenu;
            return now < phaseDeadlineUtc ? InstanceChangeAction.WaitForMenu :
                Failed("instance-selection menu did not open");
        }
        if (input.AnyMenu)
            return InstanceChangeAction.WaitForMenu;

        if (phase is Phase.Interaction or Phase.InstanceMenu or Phase.Transition)
            return now < phaseDeadlineUtc ? InstanceChangeAction.WaitForMenu :
                Failed("interaction/selection was submitted but no successful menu or arrival was observed");

        if (!input.TargetAvailable)
        {
            // A disappeared/reloaded object invalidates any pending local path.
            phase = Phase.Resolve;
            if (missingSinceUtc == DateTime.MinValue)
                missingSinceUtc = now;
            return (now - missingSinceUtc).TotalSeconds < 10 ? InstanceChangeAction.WaitForTarget :
                Failed("local Aetheryte object remained unavailable");
        }
        missingSinceUtc = DateTime.MinValue;
        if (input.Mounted)
            return InstanceChangeAction.Dismount;
        if (input.InRange)
        {
            phase = Phase.Resolve;
            return InstanceChangeAction.Interact;
        }
        if (phase == Phase.Approach)
            return now < phaseDeadlineUtc ? InstanceChangeAction.WaitForApproach :
                Failed("local Aetheryte approach did not reach interaction range");
        return input.MeshReady ? InstanceChangeAction.Approach : InstanceChangeAction.WaitForMesh;
    }
}

