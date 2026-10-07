using System.Collections.Concurrent;
using System.Numerics;
using System.Threading;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Objects.Types;
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
    private Vector3? provingProbeDestination;
    private uint provingProbeTerritory;
    private string provingProbeWorld = string.Empty;
    private int provingProbeInstance;
    private bool provingProbeActive;
    private bool provingParkingActive;
    private bool provingParkingRollbackPending;
    private bool provingProbeCancelOnLanding;
    private bool provingProbeLandingResumeAllowed;
    private bool provingProbeSetPending;
    private long? provingProbeSetStarted;
    private Vector3 provingProbeSetOrigin;

    private void RegisterNavigationProving()
    {
        commands.AddHandler(NavigationProvingCommand, new CommandInfo((_, args) =>
            navigationProvingCommands.Enqueue(args.Trim().ToLowerInvariant()))
        { HelpMessage = "Shared-navigation test: on/off/status/export; probe set/run/resume/cancel/clear; probe run cancel-landing while Sentinel is disabled and idle." });
    }

    private void DisposeNavigationProving()
    {
        commands.RemoveHandler(NavigationProvingCommand);
        if (provingNavigation is null) return;
        // Complete cancellation before unloading; no delayed cleanup may stop a later plugin instance.
        framework.RunOnFrameworkThread(() =>
        {
            navigationProvingEnabled = false;
            provingProbeActive = false;
            provingProbeSetPending = false;
            provingProbeDestination = null;
            provingProbeCancelOnLanding = false;
            provingProbeLandingResumeAllowed = false;
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
                    chat.Print("[SRank navigation test] ON for this session. Ordinary approach and approved pre-tag protected parking use Core; SS, retreat, landing and hunt policy remain legacy. Isolated probe commands require Sentinel disabled and idle.");
                    break;
                case "off":
                    var parkingRollback = provingParkingActive || provingParkingRollbackPending;
                    provingParkingRollbackPending = false;
                    provingMeshWaitStarted = null;
                    navigationProvingEnabled = false;
                    provingProbeActive = false;
                    provingProbeSetPending = false;
                    provingProbeDestination = null;
                    provingProbeCancelOnLanding = false;
                    provingProbeLandingResumeAllowed = false;
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
                    if (parkingRollback && state == SentinelState.MoveToSafePoint)
                    {
                        safePoint = null;
                        selectedParkingPath = null;
                        nextActionUtc = DateTime.UtcNow;
                        SetState(SentinelState.LocateMark, "Shared parking test disabled; legacy protected parking will resample");
                    }
                    provingJournal?.Record(ProvingEvent.Disabled, state);
                    chat.Print($"[SRank navigation test] OFF. Legacy travel restored. Shared operations this instance: {provingJournal?.OperationsStarted ?? 0}.");
                    ExportNavigationProving();
                    break;
                case "probe set":
                    SetNavigationProvingProbeDestination();
                    break;
                case "probe run cancel-landing":
                    provingProbeCancelOnLanding = true;
                    StartNavigationProvingProbe();
                    if (!provingProbeActive) provingProbeCancelOnLanding = false;
                    break;
                case "probe resume":
                    provingProbeCancelOnLanding = false;
                    StartNavigationProvingProbe(resumeLanding: true);
                    break;
                case "probe run":
                    provingProbeCancelOnLanding = false;
                    StartNavigationProvingProbe();
                    break;
                case "probe cancel":
                    CancelNavigationProvingProbe();
                    break;
                case "probe clear":
                    if (provingProbeActive)
                        chat.Print("[SRank navigation test] Cancel the active probe before clearing its destination.");
                    else
                    {
                        provingProbeSetPending = false;
                        provingProbeSetStarted = null;
                        provingProbeDestination = null;
                        provingProbeLandingResumeAllowed = false;
                        provingJournal?.Record(ProvingEvent.ProbeCleared, state);
                        chat.Print("[SRank navigation test] Probe destination cleared.");
                    }
                    break;
                case "export":
                    if (provingDiagnostics is null) { chat.Print("[SRank navigation test] No diagnostics yet."); break; }
                    ExportNavigationProving();
                    break;
                default:
                    chat.Print($"[SRank navigation test] {(navigationProvingEnabled ? "ON" : "OFF")}; operation={provingOperation?.Id}; state={provingOperation?.State}; huntState={state}; sharedOperations={provingJournal?.OperationsStarted ?? 0}; probeDestination={(provingProbeDestination is null ? "unset" : "set")}; probeSetupPending={provingProbeSetPending}; probeActive={provingProbeActive}; sharedParking={provingParkingActive}. Commands: on, off, status, export, probe set/run/resume/cancel/clear; probe run cancel-landing.");
                    break;
            }
        }
        if (provingParkingActive && combat.IsPlayerDead)
        {
            provingJournal?.Record(ProvingEvent.ParkingDeathYield, state, provingOperation?.Id);
            CancelNavigationProvingOperation();
        }
        if (provingProbeSetPending)
            TickNavigationProvingProbeDestination();
        if (provingProbeActive)
            TickNavigationProvingProbe();
        if (!provingProbeActive &&
            (!config.Enabled || (!provingParkingActive && state != SentinelState.PrepareApproachDestination && state != SentinelState.ApproachAlertCoordinates)))
            CancelNavigationProvingOperation();
    }

    private void SetNavigationProvingProbeDestination()
    {
        if (!navigationProvingEnabled || provingNavigation is null || provingAdapter is null)
        {
            chat.Print("[SRank navigation test] Run /sranknavtest on before setting a probe destination.");
            return;
        }
        if (config.Enabled || current is not null || state != SentinelState.Idle ||
            condition[ConditionFlag.InCombat] || condition[ConditionFlag.InFlight] || vnav.IsPathRunningSafe())
        {
            chat.Print("[SRank navigation test] Probe setup requires Sentinel disabled, Idle, landed, out of combat, with no movement.");
            return;
        }
        if (provingProbeActive)
        {
            chat.Print("[SRank navigation test] Cancel the active probe before changing its destination.");
            return;
        }
        provingProbeDestination = null;
        provingProbeLandingResumeAllowed = false;
        provingProbeSetOrigin = PlayerPosition();
        provingProbeSetStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        provingProbeSetPending = true;
        provingJournal?.Record(ProvingEvent.ProbeDestinationPending, state);
        chat.Print("[SRank navigation test] Probe destination capture started. Stay still while current-zone mesh readiness settles (up to 30 seconds).");
    }

    private void TickNavigationProvingProbeDestination()
    {
        if (!navigationProvingEnabled || provingNavigation is null || provingAdapter is null)
        {
            provingProbeSetPending = false;
            provingProbeSetStarted = null;
            return;
        }
        if (config.Enabled || current is not null || state != SentinelState.Idle ||
            condition[ConditionFlag.InCombat] || condition[ConditionFlag.InFlight] || vnav.IsPathRunningSafe() ||
            Vector3.Distance(PlayerPosition(), provingProbeSetOrigin) > 3f)
        {
            provingProbeSetPending = false;
            provingProbeSetStarted = null;
            provingJournal?.Record(ProvingEvent.ProbeDestinationRejected, state);
            chat.Print("[SRank navigation test] Probe destination capture cancelled. Remain landed, still, disabled, Idle and out of combat.");
            return;
        }

        NavigationSnapshot snapshot;
        try { snapshot = provingAdapter.Read(); }
        catch
        {
            provingProbeSetPending = false;
            provingProbeSetStarted = null;
            provingJournal?.Record(ProvingEvent.ProbeDestinationRejected, state);
            chat.Print("[SRank navigation test] Probe destination capture failed: vnavmesh readiness IPC was unavailable.");
            return;
        }

        var summary = NavigationProvingReadinessSummary(snapshot);
        var elapsed = provingProbeSetStarted is { } started
            ? System.Diagnostics.Stopwatch.GetElapsedTime(started)
            : TimeSpan.Zero;
        if (snapshot.Flight == FlightAvailability.Unavailable)
        {
            provingProbeSetPending = false;
            provingProbeSetStarted = null;
            provingJournal?.Record(ProvingEvent.ProbeDestinationRejected, state);
            chat.Print("[SRank navigation test] Probe destination rejected: flight is unavailable in this territory. " + summary);
            return;
        }
        if (snapshot.Loading || snapshot.MeshZone != snapshot.Zone || snapshot.Flight == FlightAvailability.Unknown)
        {
            status = "Shared probe setup waiting: " + summary;
            if (elapsed < TimeSpan.FromSeconds(30))
                return;
            provingProbeSetPending = false;
            provingProbeSetStarted = null;
            provingJournal?.Record(ProvingEvent.ProbeDestinationRejected, state);
            chat.Print("[SRank navigation test] Probe destination timed out after 30 seconds. " + summary);
            return;
        }

        var playerPosition = PlayerPosition();
        Vector3? projected;
        try { projected = provingAdapter.ProjectLanding(NavigationProvingFloorQueryOrigin(playerPosition), 8f); }
        catch { projected = null; }
        provingProbeSetPending = false;
        provingProbeSetStarted = null;
        if (projected is null || !NavigationProvingProjectionIsLocal(playerPosition, projected.Value, 8f, 3f))
        {
            provingJournal?.Record(ProvingEvent.ProbeDestinationRejected, state);
            chat.Print("[SRank navigation test] Current-zone mesh is ready, but the raised floor query did not return a safe local ground point. Move to clear ground and retry. " + summary);
            return;
        }
        provingProbeDestination = projected.Value;
        provingProbeTerritory = clientState.TerritoryType;
        provingProbeWorld = travel.CurrentWorld;
        provingProbeInstance = travel.CurrentInstance;
        provingJournal?.Record(ProvingEvent.ProbeDestinationSet, state);
        chat.Print("[SRank navigation test] Probe destination set for this zone/world/instance. Teleport away and back to its aetheryte, then run /sranknavtest probe run.");
    }

    private static Vector3 NavigationProvingFloorQueryOrigin(Vector3 playerPosition)
        => new(playerPosition.X, playerPosition.Y + 2f, playerPosition.Z);

    private static bool NavigationProvingProjectionIsLocal(Vector3 playerPosition, Vector3 projected,
        float horizontalRadius, float verticalRadius)
        => float.IsFinite(playerPosition.X) && float.IsFinite(playerPosition.Y) && float.IsFinite(playerPosition.Z) &&
           float.IsFinite(projected.X) && float.IsFinite(projected.Y) && float.IsFinite(projected.Z) &&
           Vector2.Distance(new(playerPosition.X, playerPosition.Z), new(projected.X, projected.Z)) <= horizontalRadius &&
           Math.Abs(projected.Y - playerPosition.Y) <= verticalRadius;

    private static string NavigationProvingReadinessSummary(NavigationSnapshot snapshot)
    {
        var progress = float.IsFinite(snapshot.BuildProgress) ? $"{snapshot.BuildProgress:0.###}" : "unknown";
        return $"loading={snapshot.Loading}; meshReady={snapshot.MeshReady}; buildProgress={progress}; currentZoneReady={snapshot.MeshZone == snapshot.Zone}; flight={snapshot.Flight}";
    }

    private void StartNavigationProvingProbe(bool resumeLanding = false)
    {
        if (!navigationProvingEnabled || provingNavigation is null || provingAdapter is null)
        {
            chat.Print("[SRank navigation test] Run /sranknavtest on before starting a probe.");
            return;
        }
        if (provingProbeSetPending)
        {
            chat.Print("[SRank navigation test] Probe destination capture is still waiting for readiness.");
            return;
        }
        if (provingProbeDestination is null)
        {
            chat.Print("[SRank navigation test] Set a safe probe destination first with /sranknavtest probe set.");
            return;
        }
        if (provingProbeActive || provingOperation is not null)
        {
            chat.Print("[SRank navigation test] A shared operation is already active.");
            return;
        }
        if (config.Enabled || current is not null || state != SentinelState.Idle ||
            condition[ConditionFlag.InCombat] || vnav.IsPathRunningSafe())
        {
            chat.Print("[SRank navigation test] Probe run requires Sentinel disabled, Idle, out of combat, with no movement.");
            return;
        }
        if (!NavigationProvingProbeContextMatches(clientState.TerritoryType, travel.CurrentWorld, travel.CurrentInstance,
                provingProbeTerritory, provingProbeWorld, provingProbeInstance))
        {
            chat.Print("[SRank navigation test] Return to the saved zone, world and instance before running the probe.");
            return;
        }
        if (resumeLanding)
        {
            if (!provingProbeLandingResumeAllowed ||
                !NavigationProvingProjectionIsLocal(PlayerPosition(), provingProbeDestination.Value, 3f, 3f))
            {
                chat.Print("[SRank navigation test] Resume requires a cancelled landing while still within the saved point's local bounds.");
                return;
            }
        }
        else if (!NavigationProvingProbeDistanceIsSufficient(PlayerPosition(), provingProbeDestination.Value))
        {
            chat.Print("[SRank navigation test] Start at least 80 yalms from the saved destination so mount, takeoff and flight are exercised.");
            return;
        }
        provingProbeLandingResumeAllowed = false;
        ResetNavigationProvingCoordinator(parking: false);
        provingAdapter.ResetMountRequests();
        provingOperation = provingNavigation!.Begin(CreateNavigationProvingProbeRequest(provingProbeDestination.Value));
        provingProbeActive = true;
        provingJournal?.Record(ProvingEvent.OperationStarted, state, provingOperation.Id);
        provingJournal?.Record(ProvingEvent.ProbeStarted, state, provingOperation.Id);
        chat.Print($"[SRank navigation test] SHARED probe started: {provingOperation.Id}. Use /sranknavtest probe cancel to test STOP.");
    }

    private static NavigationRequest CreateNavigationProvingProbeRequest(Vector3 destination)
        => new(destination, TravelMode.RequireFlight, RequireMount: true, ArrivalRadius: 3f,
            HorizontalArrival: false) { RequireLanding = true };

    private static bool NavigationProvingProbeContextMatches(uint currentTerritory, string currentWorld, int currentInstance,
        uint destinationTerritory, string destinationWorld, int destinationInstance)
        => currentTerritory != 0 && currentTerritory == destinationTerritory &&
           string.Equals(currentWorld, destinationWorld, StringComparison.Ordinal) &&
           currentInstance == destinationInstance;

    private static bool NavigationProvingProbeDistanceIsSufficient(Vector3 currentPosition, Vector3 destination)
        => float.IsFinite(currentPosition.X) && float.IsFinite(currentPosition.Z) &&
           float.IsFinite(destination.X) && float.IsFinite(destination.Z) &&
           Vector2.Distance(new(currentPosition.X, currentPosition.Z), new(destination.X, destination.Z)) >= 80f;

    private void TickNavigationProvingProbe()
    {
        if (provingOperation is null || provingNavigation is null)
        {
            provingProbeActive = false;
            return;
        }
        if (config.Enabled || current is not null || state != SentinelState.Idle || condition[ConditionFlag.InCombat])
        {
            var interrupted = provingOperation.Id;
            provingProbeActive = false;
            CancelNavigationProvingOperation();
            provingJournal?.Record(ProvingEvent.ProbeCancelled, state, interrupted);
            chat.Print("[SRank navigation test] Probe cancelled because Sentinel/hunt/combat state changed.");
            return;
        }
        try { provingNavigation.Tick(); }
        catch
        {
            provingProbeActive = false;
            HaltNavigationProving("Shared probe adapter failed. Export diagnostics and roll back.");
            return;
        }
        status = $"Shared navigation probe: {provingOperation.State}, operation {provingOperation.Id}, retries {provingOperation.Retries}";
        if (provingProbeCancelOnLanding && provingOperation.State == NavigationState.Landing)
        {
            provingProbeCancelOnLanding = false;
            CancelNavigationProvingProbe();
            chat.Print("[SRank navigation test] Armed cancellation exercised Landing before its first native landing action. No further action belongs to the cancelled operation.");
            return;
        }
        if (provingOperation.Result == NavigationResult.Success)
        {
            var arrived = provingOperation.Id;
            provingProbeActive = false;
            CancelNavigationProvingOperation();
            provingJournal?.Record(ProvingEvent.ProbeArrived, state, arrived);
            chat.Print("[SRank navigation test] SHARED probe landed: physical ground confirmed at the projected destination. Diagnostics exported.");
            ExportNavigationProving();
        }
        else if (provingOperation.Result != NavigationResult.Pending)
        {
            provingProbeActive = false;
            HaltNavigationProving("Shared probe stopped without arrival. Diagnostics exported; use off for rollback.");
        }
    }

    private void CancelNavigationProvingProbe()
    {
        if (!provingProbeActive || provingOperation is null)
        {
            chat.Print("[SRank navigation test] No active probe to cancel.");
            return;
        }
        var cancelled = provingOperation.Id;
        provingProbeLandingResumeAllowed = provingOperation.State == NavigationState.Landing;
        provingProbeActive = false;
        CancelNavigationProvingOperation();
        provingJournal?.Record(ProvingEvent.ProbeCancelled, state, cancelled);
        if (provingAdapter is null || !provingAdapter.FollowerStopped())
        {
            config.Enabled = false;
            chat.Print("[SRank navigation test] Probe cancellation could not confirm follower stop. Stop vnavmesh and run /sranknavtest off.");
            return;
        }
        chat.Print("[SRank navigation test] SHARED probe cancelled and follower stop confirmed. The saved destination remains available; use probe resume after landing cancellation or probe run for a new flight.");
        ExportNavigationProving();
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
        if (provingOperation is null)
        {
            provingParkingActive = false;
            if (provingAdapter is not null) provingAdapter.ApprovedParkingRoute = null;
            return;
        }
        var operation = provingOperation;
        provingOperation = null;
        // Revoke the old lease before clearing route data or permitting a replacement.
        framework.RunOnFrameworkThread(() =>
        {
            operation.Dispose();
            provingParkingActive = false;
            if (provingAdapter is not null) provingAdapter.ApprovedParkingRoute = null;
        }).GetAwaiter().GetResult();
    }

    private void NavigationProvingStateHandoff(SentinelState next)
    {
        if (navigationProvingEnabled)
            provingJournal?.Record(ProvingEvent.DomainState, next, provingOperation?.Id);
        if (provingProbeActive)
        {
            if (next != SentinelState.Idle)
            {
                provingProbeActive = false;
                CancelNavigationProvingOperation();
            }
            return;
        }
        if (provingParkingActive)
        {
            if (next == SentinelState.Landing)
            {
                provingParkingRollbackPending = false;
                provingJournal?.Record(ProvingEvent.ParkingLandingHandoff, next, provingOperation?.Id);
                chat?.Print($"[SRank navigation test] SHARED protected parking handoff: {provingOperation?.Id}; state={provingOperation?.State}. Existing safe landing/tag/kill/return resumes.");
            }
            if (next != SentinelState.MoveToSafePoint)
                CancelNavigationProvingOperation();
            return;
        }
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
            ResetNavigationProvingCoordinator(parking: false);
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


    private void CancelNavigationProvingParkingForPolicy()
    {
        if (!provingParkingActive) return;
        provingJournal?.Record(ProvingEvent.ParkingPolicyReplan, state, provingOperation?.Id);
        provingParkingRollbackPending = false;
        // Must precede any legacy replacement query/follower, including unprotected fallback.
        CancelNavigationProvingOperation();
    }

    private void ResetNavigationProvingCoordinator(bool parking)
    {
        CancelNavigationProvingOperation();
        provingNavigation?.Dispose();
        provingNavigation = new NavigationCoordinator(provingAdapter!, provingDiagnostics!,
            CreateNavigationProvingOptions(parking));
    }

    private static NavigationOptions CreateNavigationProvingOptions(bool parking)
        => parking
            ? new NavigationOptions { ReadinessSettle = TimeSpan.Zero, MaxRetries = 0,
                StallTimeout = TimeSpan.FromSeconds(ParkingRouteStallSeconds),
                OperationTimeout = TimeSpan.FromSeconds(ParkingRecoveryBudgetSeconds) }
            : new NavigationOptions { ReadinessSettle = TimeSpan.Zero };

    private static bool NavigationProvingParkingEligible(bool enabled, bool hasCurrent, bool isSs,
        bool fly, bool protectedRoute, bool randomizedRetreat, bool postTagRetreat, bool tagged)
        => enabled && hasCurrent && !isSs && fly && protectedRoute &&
            !randomizedRetreat && !postTagRetreat && !tagged;

    // null: existing route owner; true: Core owns the approved path; false: proving halted.
    // The caller still applies its unchanged candidate/progress/preference bookkeeping.
    private bool? TryStartNavigationProvingParking(IBattleChara target, ParkingCandidate candidate,
        List<Vector3> path)
    {
        if (!NavigationProvingParkingEligible(navigationProvingEnabled, current is not null,
                current is not null && HuntCatalog.IsAnySsName(current.CreatureName),
                parkingPathUsesFlight, candidate.RequiresProtectedRoute, candidate.IsRandomizedRetreat,
                postTagRetreatActive, pullCycleTagged))
            return null;
        try
        {
            ResetNavigationProvingCoordinator(parking: true);
            var snapshot = provingAdapter!.Read();
            provingAdapter.ApprovedParkingRoute = new NavigationProvingParkingRoute(path,
                candidate.Position, snapshot.Zone, target.GameObjectId);
            provingParkingActive = true;
            provingParkingRollbackPending = true;
            provingAdapter.ResetMountRequests();
            // Domain alignment/clearance checks hand off before this deliberately narrow radius.
            // Core never chooses a parking point, queries an unprotected replacement or lands here.
            provingOperation = provingNavigation!.Begin(new NavigationRequest(candidate.Position,
                TravelMode.RequireFlight, RequireMount: true, ArrivalRadius: 0.1f));
            provingJournal?.Record(ProvingEvent.OperationStarted, state, provingOperation.Id);
            chat.Print($"[SRank navigation test] SHARED protected parking started: {provingOperation.Id}. Existing safe landing/tag/kill/return policy remains active.");
            return true;
        }
        catch
        {
            HaltNavigationProving("Approved shared parking could not start. Export diagnostics and roll back.");
            return false;
        }
    }

    private bool TickNavigationProvingParking(DateTime now)
    {
        if (!provingParkingActive) return false;
        if (provingOperation is null || provingNavigation is null ||
            provingAdapter?.ApprovedParkingRoute is null || safePoint is null)
        {
            HaltNavigationProving("Shared parking lost its operation or approved destination.");
            return true;
        }
        try
        {
            // The caller has already run tag/death/aggro priority, mark checks, recovery budget
            // and its exact landing-alignment/revalidation handoff. Core alone ticks movement.
            provingNavigation.Tick();
            status = $"Shared protected parking: {provingOperation.State}, operation {provingOperation.Id}, retries {provingOperation.Retries}";
            if (provingOperation.Result != NavigationResult.Pending)
                HaltNavigationProving("Shared parking ended before the existing safe landing handoff. No automatic legacy fallback.");
        }
        catch { HaltNavigationProving("Shared protected parking adapter failed. Export diagnostics and roll back."); }
        return true;
    }

    private bool NavigationProvingParkingPathStillSafe(NavigationProvingParkingRoute route,
        IReadOnlyList<Vector3> path)
    {
        if (!provingParkingActive || !route.Matches(path) ||
            selectedParkingCandidate is not { RequiresProtectedRoute: true } candidate ||
            candidate.IsRandomizedRetreat || current is null || pullCycleTagged || postTagRetreatActive ||
            HuntCatalog.IsAnySsName(current.CreatureName) || candidate.Position != route.Destination)
            return false;
        var target = FindMark();
        if (target is null || target.GameObjectId != route.EntityId || target.IsDead || target.CurrentHp == 0)
            return false;
        var radius = ProtectedCenterRadius(target);
        var start = PlayerPosition();
        var escape = HorizontalDistance(start, target.Position) < radius;
        return ProtectedSegmentIsSafe(start, candidate.Position, target.Position, radius, escape) &&
            PathStaysOutsideProtectedRadius(start, path.ToList(), target.Position, radius, escape) &&
            ClearanceAtPoint(candidate.Position, target) >= ActiveDistanceProfile.WaitingDistance - 0.5f &&
            ClearanceAtPoint(candidate.Position, target) <= MaximumParkingClearance + 0.5f;
    }

    private void HaltNavigationProving(string reason)
    {
        provingJournal?.Record(ProvingEvent.Halted, state, provingOperation?.Id);
        provingProbeSetPending = false;
        provingProbeSetStarted = null;
        provingProbeActive = false;
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

    private sealed class ProvingNavigationAdapter : ILandingNavigationAdapter
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
        public NavigationProvingParkingRoute? ApprovedParkingRoute { get; set; }

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
                running.InvokeFunc(), waypoints.InvokeFunc()) { Grounded = ReadGroundEvidence() };
        }

        private bool? ReadGroundEvidence()
        {
            if (Loading || plugin.condition[ConditionFlag.InFlight] ||
                plugin.condition[ConditionFlag.Jumping] || plugin.condition[ConditionFlag.Jumping61] ||
                plugin.condition[ConditionFlag.Swimming] || plugin.condition[ConditionFlag.Diving] ||
                plugin.condition[ConditionFlag.MountOrOrnamentTransition]) return false;
            var position = plugin.PlayerPosition();
            var floor = ProjectLanding(NavigationProvingFloorQueryOrigin(position), 1.5f);
            return floor is { } point
                ? NavigationProvingProjectionIsLocal(position, point, 1.5f, 0.75f)
                : null;
        }

        public bool RequestLanding(NavigationSnapshot snapshot, Vector3 destination)
        {
            // Core has already stopped its follower. Never issue an unconditional dismount or
            // queue delayed movement: the current operation alone may request this native action.
            if (Loading || !snapshot.InFlight || !plugin.condition[ConditionFlag.InFlight] ||
                !plugin.condition[ConditionFlag.Mounted] || running.InvokeFunc() ||
                plugin.condition[ConditionFlag.InCombat]) return false;
            var position = plugin.PlayerPosition();
            if (!NavigationProvingProjectionIsLocal(position, destination, 3f, 3f)) return false;
            var floor = ProjectLanding(NavigationProvingFloorQueryOrigin(position), 3f);
            if (floor is null || !NavigationProvingProjectionIsLocal(destination, floor.Value, 3f, 1.5f))
                return false;
            // General action 23 is the existing consumer's normal native landing request. Its
            // return value records submission only; fresh physical ground evidence proves completion.
            return plugin.UseGeneralAction(23);
        }

        public Task<IReadOnlyList<Vector3>> FindPath(Vector3 from, Vector3 to, bool fly, CancellationToken cancellation)
        {
            if (ApprovedParkingRoute is { } approved)
                return Task.FromResult(approved.Claim(zone, fly, cancellation));
            // Invoke IPC synchronously on the framework thread. Async continuation returns data only.
            var task = pathfind.InvokeFunc(from, to, fly, cancellation);
            return CopyResult(task);
        }
        private static async Task<IReadOnlyList<Vector3>> CopyResult(Task<List<Vector3>> task)
            => (await task.ConfigureAwait(false)).ToArray();
        public void Follow(IReadOnlyList<Vector3> path, bool fly)
        {
            if (ApprovedParkingRoute is { } approved &&
                (!fly || !plugin.NavigationProvingParkingPathStillSafe(approved, path)))
                throw new InvalidOperationException("Approved protected parking route is no longer safe.");
            follow.InvokeAction(path.ToList(), fly);
        }
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
