using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using System.Numerics;

namespace SRankSentinel;

internal sealed class VNavmeshIpc
{
    private readonly ICallGateSubscriber<bool> navReady;
    private readonly ICallGateSubscriber<Vector3?> flagToPoint;
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> moveCloseTo;
    private readonly ICallGateSubscriber<Vector3, bool, bool> moveTo;
    private readonly ICallGateSubscriber<Vector3, bool, float, Vector3?> pointOnFloor;
    private readonly ICallGateSubscriber<object> stop;

    public VNavmeshIpc(IDalamudPluginInterface pi)
    {
        navReady = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        flagToPoint = pi.GetIpcSubscriber<Vector3?>("vnavmesh.Query.Mesh.FlagToPoint");
        moveCloseTo = pi.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        moveTo = pi.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo");
        pointOnFloor = pi.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor");
        stop = pi.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
    }

    public bool IsReadySafe()
    {
        try { return navReady.InvokeFunc(); }
        catch { return false; }
    }

    public Vector3? FlagToPointSafe()
    {
        try { return flagToPoint.InvokeFunc(); }
        catch { return null; }
    }

    public bool MoveCloseToSafe(Vector3 destination, bool fly, float range)
    {
        try { return moveCloseTo.InvokeFunc(destination, fly, range); }
        catch { return false; }
    }

    public bool MoveToSafe(Vector3 destination, bool fly)
    {
        try { return moveTo.InvokeFunc(destination, fly); }
        catch { return false; }
    }

    public Vector3? PointOnFloorSafe(Vector3 destination, float halfExtentXZ = 10f)
    {
        try { return pointOnFloor.InvokeFunc(destination, false, halfExtentXZ); }
        catch { return null; }
    }

    public void StopSafe()
    {
        try { stop.InvokeAction(); }
        catch { }
    }
}
