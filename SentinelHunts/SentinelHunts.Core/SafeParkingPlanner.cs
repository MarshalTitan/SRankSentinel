using System.Numerics;

namespace SentinelHunts.Core;

public static class SafeParkingPlanner
{
    public static float HorizontalDistance(Vector3 left, Vector3 right)
    {
        var x = left.X - right.X;
        var z = left.Z - right.Z;
        return MathF.Sqrt(x * x + z * z);
    }

    public static float Clearance(
        Vector3 player,
        Vector3 mark,
        float playerHitboxRadius,
        float markHitboxRadius) =>
        HorizontalDistance(player, mark) - playerHitboxRadius - markHitboxRadius;

    public static IReadOnlyList<Vector3> CreateCandidates(
        Vector3 mark,
        Vector3 player,
        float playerHitboxRadius,
        float markHitboxRadius,
        float minimumClearance,
        int count = 16)
    {
        if (count < 4)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (minimumClearance <= 0)
            throw new ArgumentOutOfRangeException(nameof(minimumClearance));

        var away = new Vector2(player.X - mark.X, player.Z - mark.Z);
        if (away.LengthSquared() < 0.001f)
            away = Vector2.UnitX;
        away = Vector2.Normalize(away);

        var baseAngle = MathF.Atan2(away.Y, away.X);
        var radius = playerHitboxRadius + markHitboxRadius + minimumClearance + 1f;
        var result = new List<Vector3>(count);
        for (var index = 0; index < count; index++)
        {
            // Alternating offsets try the directly-away point first, then fan out symmetrically.
            var step = index == 0 ? 0 : (index + 1) / 2;
            var sign = index % 2 == 0 ? -1 : 1;
            var angle = baseAngle + sign * step * (MathF.Tau / count);
            result.Add(new Vector3(
                mark.X + MathF.Cos(angle) * radius,
                mark.Y,
                mark.Z + MathF.Sin(angle) * radius));
        }

        return result;
    }

    public static bool PathRespectsClearance(
        IEnumerable<Vector3> path,
        Vector3 mark,
        float playerHitboxRadius,
        float markHitboxRadius,
        float minimumClearance) =>
        path.All(point => Clearance(point, mark, playerHitboxRadius, markHitboxRadius) >= minimumClearance);

    public static bool PathEscapesProtectedArea(
        IEnumerable<Vector3> path,
        Vector3 mark,
        float playerHitboxRadius,
        float markHitboxRadius,
        float minimumClearance)
    {
        var reachedSafety = false;
        var previousClearance = float.NegativeInfinity;
        foreach (var point in path)
        {
            var clearance = Clearance(point, mark, playerHitboxRadius, markHitboxRadius);
            if (!reachedSafety && clearance + 0.5f < previousClearance)
                return false;
            if (reachedSafety && clearance < minimumClearance)
                return false;
            reachedSafety |= clearance >= minimumClearance;
            previousClearance = clearance;
        }

        return reachedSafety;
    }
}
