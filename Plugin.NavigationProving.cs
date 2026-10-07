using System.Collections.Concurrent;
using System.Numerics;
using System.Threading;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Plugin.Ipc;
using SentinelCore.Diagnostics;
using SentinelCore.Identity;
using SentinelCore.Navigation;

namespace SRankSentinel;

public sealed partial class Plugin
{
    private const string NavigationProvingCommand = "/sranknavtest";
    private readonly ConcurrentQueue<string> navigationProvingCommands = new();
    private bool navigationProvingEnabled;
    private NavigationCoordinator? provingNavigation;
    private NavigationOperation? provingOperation;
    private ProvingNavigationAdapter? provingAdapter;
    private NavigationDiagnostics? provingDiagnostics;
    private long? provingMeshWaitStarted;
    private NavigationProvingJournal? provingJournal;

    private void RegisterNavigationProving()
    {
        commands.AddHandler(NavigationProvingCommand, new CommandInfo((_, args) =>
            navigationProvingCommands.Enqueue(args.Trim().ToLowerInvariant()))
        { HelpMessage = "Controlled shared-navigation test: on (idle only), off (legacy rollback), status, export. Off after reload." });
    }

    private void DisposeNavigationProving()
    {
        commands.RemoveHandler(NavigationProvingCommand);
        if (provingNavigation is null) return;
        // Complete cancellation before unloading; no delayed cleanup may stop a later plugin instance.
        framework.RunOnFrameworkThread(() =>
        {
            navigationProvingEnabled = false;
            provingNavigation?.Dispose();
            provingNavigation = null;
            provingOperation = null;
        }).GetAwaiter().GetResult();
    }

    private void TickNavigationProvingControl()
    {
        provingAdapter?.ObserveZone();
        while (navigationProvingCommands.TryDequeue(out var command))
        {
            switch (command)
            {
                case "on":
                    if (current is not null || state != SentinelState.Idle || vnav.IsPathRunningSafe())
                    {
                        chat.Print("[SRank navigation test] Enable only while idle with no active hunt or movement.");
                        break;
                    }
                    provingJournal ??= new NavigationProvingJournal();
                    provingDiagnostics ??= new NavigationDiagnostics(
                        SentinelIdentity.FromAssembly("SRankSentinel", "S Rank Sentinel", "MTitan", typeof(Plugin).Assembly));
                    provingAdapter ??= new ProvingNavigationAdapter(this);
                    provingAdapter.ObserveZone();
                    provingNavigation ??= new NavigationCoordinator(provingAdapter, provingDiagnostics,
                        new NavigationOptions { ReadinessSettle = TimeSpan.Zero });
                    provingMeshWaitStarted = null;
                    navigationProvingEnabled = true;
                    provingJournal.Record(ProvingEvent.Enabled, state);
                    chat.Print("[SRank navigation test] ON for this session. Ordinary long approach only; SS and hunt policy remain legacy.");
                    break;
                case "off":
                    provingMeshWaitStarted = null;
                    navigationProvingEnabled = false;
                    CancelNavigationProvingOperation();
                    if (provingAdapter is not null && !provingAdapter.FollowerStopped())
                    {
                        config.Enabled = false;
                        chat.Print("[SRank navigation test] Rollback held: follower stop cannot be confirmed. Stop vnavmesh, then retry off.");
                        break;
                    }
                    provingNavigation?.Dispose();
                    provingNavigation = null;
                    if (state == SentinelState.ApproachAlertCoordinates)
                    {
                        ResetApproachRouteTracking(clearProjectionCandidates: false);
                        ResetLongApproachFlightStartup();
                        SetState(SentinelState.PrepareApproachDestination, "Shared navigation test disabled; legacy approach restored");
                    }
                    provingJournal?.Record(ProvingEvent.Disabled, state);
                    chat.Print($"[SRank navigation test] OFF. Legacy travel restored. Shared operations this instance: {provingJournal?.OperationsStarted ?? 0}.");
                    ExportNavigationProving();
                    break;
                case "export":
                    if (provingDiagnostics is null) { chat.Print("[SRank navigation test] No diagnostics yet."); break; }
                    ExportNavigationProving();
                    break;
                default:
                    chat.Print($"[SRank navigation test] {(navigationProvingEnabled ? "ON" : "OFF")}; operation={provingOperation?.Id}; state={provingOperation?.State}; huntState={state}; sharedOperations={provingJournal?.OperationsStarted ?? 0}. Commands: on, off, status, export.");
                    break;
            }
        }
        if (!config.Enabled || (state != SentinelState.PrepareApproachDestination && state != SentinelState.ApproachAlertCoordinates))
            CancelNavigationProvingOperation();
    }

    private void ExportNavigationProving()
    {
        if (provingDiagnostics is null || provingJournal is null) return;
        try
        {
            provingJournal.Record(ProvingEvent.Exported, state, provingOperation?.Id);
            var path = System.IO.Path.Combine(pi.ConfigDirectory.FullName, "navigation-proving.json");
            System.IO.Directory.CreateDirectory(pi.ConfigDirectory.FullName);
            System.IO.File.WriteAllText(path, provingJournal.Export(provingDiagnostics, navigationProvingEnabled, state));
            chat.Print("[SRank navigation test] Sanitized export: " + path);
            if (provingJournal.OperationsStarted == 0)
                chat.Print("[SRank navigation test] No shared operation started in this plugin instance. The export now includes activation and hunt states; this is not shared-path acceptance.");
        }
        catch
        {
            // An evidence-write failure must never alter movement or hunt recovery.
            chat.Print("[SRank navigation test] Could not save diagnostic export. Movement state was not changed.");
        }
    }

    private void CancelNavigationProvingOperation()
    {
        if (provingOperation is null) return;
        var operation = provingOperation;
        provingOperation = null;
        // UI stop/skip actions can reach reset/state hooks; ownership changes still run on the framework thread.
        framework.RunOnFrameworkThread(operation.Dispose).GetAwaiter().GetResult();
    }

    private void NavigationProvingStateHandoff(SentinelState next)
    {
        if (navigationProvingEnabled)
            provingJournal?.Record(ProvingEvent.DomainState, next, provingOperation?.Id);
        if (next != SentinelState.PrepareApproachDestination && next != SentinelState.ApproachAlertCoordinates)
            CancelNavigationProvingOperation();
    }

    private bool NavigationProvingWaitForMesh()
    {
        if (!navigationProvingEnabled || provingAdapter is null) return false;
        try
        {
            var snapshot = provingAdapter.Read();
            if (snapshot.MeshZone == snapshot.Zone && !snapshot.Loading)
            { provingMeshWaitStarted = null; return false; }
            provingMeshWaitStarted ??= System.Diagnostics.Stopwatch.GetTimestamp();
            if (System.Diagnostics.Stopwatch.GetElapsedTime(provingMeshWaitStarted.Value) >= TimeSpan.FromSeconds(90))
                HaltNavigationProving("Current-zone mesh readiness was not confirmed within 90 seconds. Export diagnostics and roll back.");
            else
                status = "Shared navigation test: waiting for stable current-zone mesh/build readiness before projection";
        }
        catch { HaltNavigationProving("Navigation dependency unavailable. Export diagnostics and roll back."); }
        return true;
    }

    private bool TickNavigationProvingApproach()
    {
        try { return TickNavigationProvingApproachCore(); }
        catch
        {
            if (!navigationProvingEnabled) throw;
            HaltNavigationProving("Navigation adapter failed. Export diagnostics and roll back.");
            return true;
        }
    }

    private bool TickNavigationProvingApproachCore()
    {
        if (!navigationProvingEnabled || provingNavigation is null || approachPoint is null ||
            current is null || HuntCatalog.IsAnySsName(current.CreatureName))
            return false;
        // Approximate recovery remains plugin policy; do not silently mix it into this proving run.
        if (approachPointIsApproximate)
        {
            HaltNavigationProving("Approximate-coordinate recovery is outside this proving slice. Use /sranknavtest off for legacy.");
            return true;
        }
        var scanRange = Math.Max(ActiveDistanceProfile.FlagApproachDistance, ActiveDistanceProfile.WaitingDistance)
                        + ApproachScanTolerance;
        if (provingOperation is null)
        {
            provingAdapter!.ResetMountRequests();
            provingOperation = provingNavigation.Begin(new NavigationRequest(approachPoint.Value,
                TravelMode.PreferFlight, RequireMount: true, ArrivalRadius: scanRange, HorizontalArrival: true));
            provingJournal?.Record(ProvingEvent.OperationStarted, state, provingOperation.Id);
            chat.Print($"[SRank navigation test] SHARED operation started: {provingOperation.Id}");
        }
        provingNavigation.Tick();
        status = $"Shared navigation test: {provingOperation.State}, operation {provingOperation.Id}, retries {provingOperation.Retries}";
        if (provingOperation.Result == NavigationResult.Success)
        {
            CancelNavigationProvingOperation();
            ResetApproachRouteTracking(clearProjectionCandidates: false);
            SetState(SentinelState.LocateMark, "Shared navigation reached report scan range; existing hunt policy resumes");
        }
        else if (provingOperation.Result != NavigationResult.Pending)
        {
            HaltNavigationProving("Shared navigation stopped without arrival. Export diagnostics; use /sranknavtest off for legacy.");
        }
        else if (state != SentinelState.ApproachAlertCoordinates)
        {
            SetState(SentinelState.ApproachAlertCoordinates, status);
        }
        return true;
    }

    private void HaltNavigationProving(string reason)
    {
        provingJournal?.Record(ProvingEvent.Halted, state, provingOperation?.Id);
        CancelNavigationProvingOperation();
        // Do not auto-retry forever, fall through to legacy, or discard the hunt.
        config.Enabled = false;
        navigationProvingEnabled = false;
        provingNavigation?.Dispose();
        provingNavigation = null;
        status = reason;
        chat.Print("[SRank navigation test] " + reason + " Sentinel is disabled; re-enable explicitly after rollback.");
        ExportNavigationProving();
    }

    private sealed class ProvingNavigationAdapter : INavigationAdapter
    {
        private readonly Plugin plugin;
        private readonly ZoneReadinessGate readiness = new();
        private readonly ICallGateSubscriber<bool> ready;
        private readonly ICallGateSubscriber<float> progress;
        private readonly ICallGateSubscriber<bool> running;
        private readonly ICallGateSubscriber<List<Vector3>> waypoints;
        private readonly ICallGateSubscriber<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>> pathfind;
        private readonly ICallGateSubscriber<List<Vector3>, bool, object> follow;
        private readonly ICallGateSubscriber<object> stop;
        private ZoneStamp zone;
        private string world = string.Empty;
        private int instance;
        private bool wasLoading;
        private int mountRequests;
        public void ResetMountRequests() => mountRequests = 0;

        public ProvingNavigationAdapter(Plugin plugin)
        {
            this.plugin = plugin;
            ready = plugin.pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
            progress = plugin.pi.GetIpcSubscriber<float>("vnavmesh.Nav.BuildProgress");
            running = plugin.pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
            waypoints = plugin.pi.GetIpcSubscriber<List<Vector3>>("vnavmesh.Path.ListWaypoints");
            pathfind = plugin.pi.GetIpcSubscriber<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>>("vnavmesh.Nav.PathfindCancelable");
            follow = plugin.pi.GetIpcSubscriber<List<Vector3>, bool, object>("vnavmesh.Path.MoveTo");
            stop = plugin.pi.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        }

        public bool FollowerStopped()
        {
            try { return !running.InvokeFunc(); } catch { return false; }
        }

        private bool Loading => plugin.objects.LocalPlayer is null ||
            plugin.condition[ConditionFlag.BetweenAreas] || plugin.condition[ConditionFlag.BetweenAreas51];

        public void ObserveZone()
        {
            var loading = Loading;
            var territory = plugin.clientState.TerritoryType;
            var currentWorld = plugin.travel.CurrentWorld;
            var currentInstance = plugin.travel.CurrentInstance;
            if (zone.Territory != territory || world != currentWorld || instance != currentInstance ||
                loading != wasLoading)
                zone = new(territory, zone.Epoch + 1);
            world = currentWorld;
            instance = currentInstance;
            wasLoading = loading;
            // Feed loading observations even while legacy world/zone travel is in progress.
            if (loading) readiness.Observe(zone, true, false, float.NaN);
        }

        public NavigationSnapshot Read()
        {
            ObserveZone();
            bool meshReady;
            float buildProgress;
            try { meshReady = ready.InvokeFunc(); buildProgress = progress.InvokeFunc(); }
            catch { meshReady = false; buildProgress = float.NaN; }
            var meshZone = readiness.Observe(zone, Loading, meshReady, buildProgress);
            var flightKnown = plugin.TryGetLoadedTerritoryFlightAvailability(out var flightAvailable);
            return new(zone, meshZone, Loading, meshReady, buildProgress, plugin.PlayerPosition(),
                plugin.condition[ConditionFlag.Mounted], plugin.condition[ConditionFlag.InFlight],
                !flightKnown ? FlightAvailability.Unknown : flightAvailable ? FlightAvailability.Available : FlightAvailability.Unavailable,
                running.InvokeFunc(), waypoints.InvokeFunc());
        }

        public Task<IReadOnlyList<Vector3>> FindPath(Vector3 from, Vector3 to, bool fly, CancellationToken cancellation)
        {
            // Invoke IPC synchronously on the framework thread. Async continuation returns data only.
            var task = pathfind.InvokeFunc(from, to, fly, cancellation);
            return CopyResult(task);
        }
        private static async Task<IReadOnlyList<Vector3>> CopyResult(Task<List<Vector3>> task)
            => (await task.ConfigureAwait(false)).ToArray();
        public void Follow(IReadOnlyList<Vector3> path, bool fly) => follow.InvokeAction(path.ToList(), fly);
        public void Stop()
        {
            stop.InvokeAction();
            if (running.InvokeFunc()) throw new InvalidOperationException("Follower stop unconfirmed.");
        }
        public void RequestMount()
        {
            if (plugin.condition[ConditionFlag.Mounting]) return;
            plugin.TryRequestSentinelMount(out _, out _, allowPreferredMount: mountRequests++ == 0);
        }
        public void RequestTakeoff() => plugin.UseGeneralAction(2);
        public Vector3? ProjectLanding(Vector3 candidate, float searchRadius)
            => plugin.vnav.PointOnFloorSafe(candidate, searchRadius);
    }
}
