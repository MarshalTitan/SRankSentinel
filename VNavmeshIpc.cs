using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace SRankSentinel;

internal sealed class VNavmeshIpc
{
    private readonly ICallGateSubscriber<bool> navReady;
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> moveCloseTo;
    private readonly ICallGateSubscriber<Vector3, bool, bool> moveTo;
    private readonly ICallGateSubscriber<Vector3, Vector3, bool, Task<List<Vector3>>> pathfind;
    private readonly ICallGateSubscriber<Vector3, Vector3, bool, Vector3, float, Task<List<Vector3>>> pathfindAvoid;
    private readonly ICallGateSubscriber<List<Vector3>, bool, object> movePath;
    private readonly ICallGateSubscriber<bool> pathIsRunning;
    private readonly ICallGateSubscriber<int> pathWaypointCount;
    private readonly ICallGateSubscriber<List<Vector3>> pathWaypoints;
    private readonly ICallGateSubscriber<bool> movementAllowed;
    private readonly ICallGateSubscriber<bool> simplePathfindInProgress;
    private readonly ICallGateSubscriber<bool> navPathfindInProgress;
    private readonly ICallGateSubscriber<Vector3, bool, float, Vector3?> pointOnFloor;
    private readonly ICallGateSubscriber<object> stop;
    private readonly IPluginLog log;
    private string lastStopDiagnostic = string.Empty;
    private DateTime lastStopDiagnosticUtc = DateTime.MinValue;

    public VNavmeshIpc(IDalamudPluginInterface pi, IPluginLog log)
    {
        this.log = log;
        navReady = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        moveCloseTo = pi.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        moveTo = pi.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo");
        pathfind = pi.GetIpcSubscriber<Vector3, Vector3, bool, Task<List<Vector3>>>("vnavmesh.Nav.Pathfind");
        pathfindAvoid = pi.GetIpcSubscriber<Vector3, Vector3, bool, Vector3, float, Task<List<Vector3>>>("vnavmesh.Nav.PathfindAvoid");
        movePath = pi.GetIpcSubscriber<List<Vector3>, bool, object>("vnavmesh.Path.MoveTo");
        pathIsRunning = pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        pathWaypointCount = pi.GetIpcSubscriber<int>("vnavmesh.Path.NumWaypoints");
        pathWaypoints = pi.GetIpcSubscriber<List<Vector3>>("vnavmesh.Path.ListWaypoints");
        movementAllowed = pi.GetIpcSubscriber<bool>("vnavmesh.Path.GetMovementAllowed");
        simplePathfindInProgress = pi.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        navPathfindInProgress = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.PathfindInProgress");
        pointOnFloor = pi.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor");
        stop = pi.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
    }

    public bool IsReadySafe()
    {
        try { return navReady.InvokeFunc(); }
        catch { return false; }
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

    public Task<List<Vector3>>? PathfindAvoidSafe(
        Vector3 from,
        Vector3 destination,
        bool fly,
        Vector3 avoidCenter,
        float avoidRadius)
    {
        try { return pathfindAvoid.InvokeFunc(from, destination, fly, avoidCenter, avoidRadius); }
        catch { return null; }
    }

    public Task<List<Vector3>>? PathfindSafe(Vector3 from, Vector3 destination, bool fly)
    {
        try { return pathfind.InvokeFunc(from, destination, fly); }
        catch (Exception ex)
        {
            log.Warning(ex, "vnavmesh Nav.Pathfind request failed");
            return null;
        }
    }

    public bool MovePathSafe(List<Vector3> waypoints, bool fly)
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

    public bool IsPathRunningSafe()
    {
        try { return pathIsRunning.InvokeFunc(); }
        catch { return false; }
    }

    public bool IsPathfindInProgressSafe()
    {
        try { return simplePathfindInProgress.InvokeFunc(); }
        catch { return false; }
    }

    public bool IsNavPathfindInProgressSafe()
    {
        try { return navPathfindInProgress.InvokeFunc(); }
        catch { return false; }
    }

    public int PathWaypointCountSafe()
    {
        try { return pathWaypointCount.InvokeFunc(); }
        catch { return -1; }
    }

    public List<Vector3> PathWaypointsSafe()
    {
        try { return pathWaypoints.InvokeFunc(); }
        catch { return new List<Vector3>(); }
    }

    public bool IsMovementAllowedSafe()
    {
        try { return movementAllowed.InvokeFunc(); }
        catch { return false; }
    }

    public Vector3? PointOnFloorSafe(Vector3 destination, float halfExtentXZ = 10f)
    {
        try { return pointOnFloor.InvokeFunc(destination, false, halfExtentXZ); }
        catch { return null; }
    }

    public void StopSafe(string reason = "unspecified", [CallerMemberName] string caller = "unknown")
    {
        try
        {
            var diagnostic = $"{caller}:{reason}";
            var now = DateTime.UtcNow;
            if (!string.Equals(lastStopDiagnostic, diagnostic, StringComparison.Ordinal) ||
                (now - lastStopDiagnosticUtc).TotalSeconds >= 10)
            {
                log.Debug("vnavmesh Stop requested by {Caller}: {Reason}", caller, reason);
                lastStopDiagnostic = diagnostic;
                lastStopDiagnosticUtc = now;
            }
            stop.InvokeAction();
        }
        catch { }
    }
}
