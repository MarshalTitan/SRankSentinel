using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using System.Numerics;

namespace SentinelHunts.Services;

internal sealed class VNavmeshService
{
    private readonly ICallGateSubscriber<bool> navReady;
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> moveCloseTo;
    private readonly ICallGateSubscriber<Vector3, Vector3, bool, Vector3, float, Task<List<Vector3>>> pathfindAvoid;
    private readonly ICallGateSubscriber<List<Vector3>, bool, object> movePath;
    private readonly ICallGateSubscriber<bool> pathIsRunning;
    private readonly ICallGateSubscriber<bool> pathfindInProgress;
    private readonly ICallGateSubscriber<Vector3, bool, float, Vector3?> pointOnFloor;
    private readonly ICallGateSubscriber<object> stop;

    public VNavmeshService(IDalamudPluginInterface pi)
    {
        navReady = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        moveCloseTo = pi.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        pathfindAvoid = pi.GetIpcSubscriber<Vector3, Vector3, bool, Vector3, float, Task<List<Vector3>>>(
            "vnavmesh.Nav.PathfindAvoid");
        movePath = pi.GetIpcSubscriber<List<Vector3>, bool, object>("vnavmesh.Path.MoveTo");
        pathIsRunning = pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        pathfindInProgress = pi.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        pointOnFloor = pi.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor");
        stop = pi.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
    }

    public bool IsReady
    {
        get
        {
            try { return navReady.InvokeFunc(); }
            catch { return false; }
        }
    }

    public bool IsRunning
    {
        get
        {
            try { return pathIsRunning.InvokeFunc() || pathfindInProgress.InvokeFunc(); }
            catch { return false; }
        }
    }

    public bool MoveCloseTo(Vector3 destination, bool fly, float range)
    {
        try { return moveCloseTo.InvokeFunc(destination, fly, range); }
        catch { return false; }
    }

    public Task<List<Vector3>>? PathfindAvoid(
        Vector3 from,
        Vector3 destination,
        bool fly,
        Vector3 avoidCenter,
        float avoidRadius)
    {
        try { return pathfindAvoid.InvokeFunc(from, destination, fly, avoidCenter, avoidRadius); }
        catch { return null; }
    }

    public bool MovePath(List<Vector3> waypoints, bool fly)
    {
        if (waypoints.Count == 0)
            return false;
        try
        {
            movePath.InvokeAction(waypoints, fly);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public Vector3? PointOnFloor(Vector3 destination, float halfExtent = 20f)
    {
        try { return pointOnFloor.InvokeFunc(destination, false, halfExtent); }
        catch { return null; }
    }

    public void Stop()
    {
        try { stop.InvokeAction(); }
        catch { }
    }
}
