using System.Numerics;
using System.Threading;
using SentinelCore.Navigation;

namespace SRankSentinel;

/// <summary>Immutable consumer approval, not a movement owner. Core alone holds the lease.
/// A retry cannot turn this approved route into a fresh unprotected path query.</summary>
internal sealed class NavigationProvingParkingRoute
{
    private readonly Vector3[] approved;
    private bool claimed;
    public Vector3 Destination { get; }
    public ZoneStamp Zone { get; }
    public ulong EntityId { get; }
    public NavigationProvingParkingRoute(IReadOnlyList<Vector3> path, Vector3 destination,
        ZoneStamp zone, ulong entityId)
    {
        if (path.Count < 2 || path.Any(p => !Finite(p)) || !Finite(destination) ||
            Vector3.Distance(path[^1], destination) > 5f || entityId == 0)
            throw new ArgumentOutOfRangeException(nameof(path));
        approved = path.ToArray();
        Destination = destination;
        Zone = zone;
        EntityId = entityId;
    }
    public IReadOnlyList<Vector3> Claim(ZoneStamp zone, bool fly, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (claimed || zone != Zone || !fly)
            throw new InvalidOperationException("Approved parking route expired or was already consumed.");
        claimed = true;
        return approved.ToArray();
    }
    public bool Matches(IReadOnlyList<Vector3> path)
        => path.Count == approved.Length && path.SequenceEqual(approved);
    private static bool Finite(Vector3 point)
        => float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
}
