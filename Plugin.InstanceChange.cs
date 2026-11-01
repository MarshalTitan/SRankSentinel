using Dalamud.Game.ClientState.Conditions;
using System.Numerics;

namespace SRankSentinel;

public sealed partial class Plugin
{
    private readonly InstanceChangeRecovery instanceRecovery = new();
    private NativeTravel.InstanceAetheryteTarget? instanceAetheryte;
    private Vector3? instanceRecoveryOrigin;
    private Task<List<Vector3>>? instanceLocalPath;
    private ulong instancePathObjectId;
    private bool instanceLocalMovement;
    private bool instanceFailureLogged;
    private bool instanceTargetMissing;
    private readonly Queue<Vector3> instanceApproachCandidates = new();
    private DateTime instanceGeometryRetryUtc;

    private static bool IsInstanceRecoveryState(SentinelState value) => value is
        SentinelState.ChangeInstance or SentinelState.SelectInstance or SentinelState.WaitForInstance;

    private void ResetInstanceRangeRecovery()
    {
        StopInstanceRangeMovement();
        instanceRecovery.Reset();
        instanceAetheryte = null;
        instanceRecoveryOrigin = null;
        instanceFailureLogged = instanceTargetMissing = false;
        instanceGeometryRetryUtc = DateTime.MinValue;
    }

    private void StopInstanceRangeMovement()
    {
        if (instanceLocalMovement || instanceLocalPath is not null)
            vnav.StopSafe("stopping local instance-change recovery movement");
        instanceLocalPath = null; // Never submit a result after menu/zoning/target loss.
        instanceLocalMovement = false;
        instancePathObjectId = 0;
        instanceApproachCandidates.Clear();
    }

    private void HoldInstanceChange(string reason)
    {
        StopInstanceRangeMovement();
        status = $"Instance change blocked: need {current?.Instance}, current {travel.CurrentInstance}; {reason}. " +
                 "Hunt retained; use Stop/Reset or retry after resolving the local interaction.";
        if (instanceFailureLogged)
            return;
        instanceFailureLogged = true;
        log.Warning("Instance-change recovery held without abandoning hunt: required={Required}, current={Current}, failures={Failures}; {Reason}",
            current?.Instance, travel.CurrentInstance, instanceRecovery.Failures, reason);
    }

    private void TickInstanceChange(DateTime now)
    {
        try { TickInstanceChangeCore(now); }
        catch (Exception ex)
        {
            instanceRecovery.Block("local interaction recovery raised an exception");
            log.Warning(ex, "Instance-change interaction failed; retaining the active hunt");
            HoldInstanceChange("local interaction recovery raised an exception");
        }
    }

    private void TickInstanceChangeCore(DateTime now)
    {
        if (current is null)
            return;
        if (clientState.TerritoryType != current.TerritoryId ||
            !travel.CurrentWorld.Equals(current.World, StringComparison.OrdinalIgnoreCase))
        {
            StopInstanceRangeMovement();
            if (!travel.IsBusy)
                HoldInstanceChange("player is outside the required territory/World");
            return;
        }

        var correct = travel.CurrentInstance == current.Instance;
        var busy = travel.IsBusy;
        var anyMenu = travel.SelectionMenuIsOpen();
        if (correct || busy || anyMenu)
            StopInstanceRangeMovement();

        var player = objects.LocalPlayer;
        if (instanceRecoveryOrigin is null && player is not null && !busy)
            instanceRecoveryOrigin = player.Position;
        var target = !busy && instanceRecoveryOrigin is Vector3 origin
            ? travel.ResolveInstanceAetheryte(instanceAetheryte, origin) : null;
        if (target is not null)
        {
            if (instanceAetheryte?.ObjectId != target.ObjectId || instanceTargetMissing)
                log.Information("Instance Aetheryte resolved: required={Required}, current={Current}, object={Object}, data={Data}, name={Name}, distance={Distance:0.0}y",
                    current.Instance, travel.CurrentInstance, target.ObjectId, target.BaseId, target.Name,
                    player is null ? -1f : Vector3.Distance(player.Position, target.Position));
            instanceAetheryte = target;
            instanceTargetMissing = false;
        }
        else if (!busy && !correct && !anyMenu)
        {
            StopInstanceRangeMovement();
            if (!instanceTargetMissing)
                log.Warning("Instance Aetheryte temporarily unavailable; stopping movement and re-resolving locally");
            instanceTargetMissing = true;
        }

        var distance = target is not null && player is not null
            ? Vector3.Distance(player.Position, target.Position) : float.MaxValue;
        var action = instanceRecovery.Decide(now, new InstanceChangeObservation(
            correct, busy,
            travel.InstanceTravelMenuIsOpen() && travel.InstanceTargetIsSelected(instanceAetheryte),
            travel.InstanceSelectionIsOpen(current.Instance) && travel.InstanceTargetIsSelected(instanceAetheryte), anyMenu,
            target is not null && player is not null,
            distance <= instanceRecovery.InteractionRange,
            condition[ConditionFlag.Mounted] || condition[ConditionFlag.InFlight], vnav.IsReadySafe()));

        switch (action)
        {
            case InstanceChangeAction.ResumeHunt:
                log.Information("Instance change confirmed: required={Required}, current={Current}; clearing local recovery and resuming hunt {Mark}",
                    current.Instance, travel.CurrentInstance, current.CreatureName);
                ResetInstanceRangeRecovery();
                BeginPlayerReadyWait("Correct instance confirmed; hunt resumed");
                return;
            case InstanceChangeAction.WaitForTransition:
                status = $"Waiting for normal instance transition to {current.Instance}; local movement stopped";
                return;
            case InstanceChangeAction.Hold:
                HoldInstanceChange(string.IsNullOrEmpty(instanceRecovery.LastFailure)
                    ? "local recovery exceeded its 90s budget" : instanceRecovery.LastFailure);
                return;
            case InstanceChangeAction.Retry:
                StopInstanceRangeMovement();
                instanceGeometryRetryUtc = now.AddSeconds(2);
                log.Warning("Instance interaction rejected/timed out: required={Required}, current={Current}, distance={Distance:0.0}y, failure={Failure}/{Limit}; re-resolving local Aetheryte: {Reason}",
                    current.Instance, travel.CurrentInstance, distance, instanceRecovery.Failures,
                    InstanceChangeRecovery.MaximumFailures, instanceRecovery.LastFailure);
                SetState(SentinelState.ChangeInstance, "Retrying local Aetheryte interaction; hunt retained");
                return;
            case InstanceChangeAction.WaitForMenu:
                status = $"Waiting for Aetheryte interaction/instance {current.Instance}; local movement stopped";
                return;
            case InstanceChangeAction.OpenInstanceMenu:
                log.Information("Aetheryte menu opened for instance change; movement stopped, required={Required}", current.Instance);
                if (travel.SelectInstanceTravelMenu())
                {
                    instanceRecovery.Submitted(action, now);
                    SetState(SentinelState.SelectInstance, $"Selecting instance {current.Instance}");
                }
                else
                    instanceRecovery.Failed("instance menu callback was rejected");
                return;
            case InstanceChangeAction.SelectInstance:
                log.Information("Instance-selection menu opened; movement stopped, requested={Required}", current.Instance);
                if (travel.SelectInstance(current.Instance))
                {
                    log.Information("Requested instance selected: {Instance}", current.Instance);
                    instanceRecovery.Submitted(action, now);
                    SetState(SentinelState.WaitForInstance, $"Traveling to instance {current.Instance}");
                }
                else
                    instanceRecovery.Failed("requested instance was not selectable");
                return;
            case InstanceChangeAction.WaitForTarget:
                status = "Waiting for the local Aetheryte object to reload; hunt retained";
                return;
            case InstanceChangeAction.Dismount:
                StopInstanceRangeMovement();
                if (now >= nextActionUtc)
                {
                    UseGeneralAction(23);
                    nextActionUtc = now.AddSeconds(1);
                }
                return;
            case InstanceChangeAction.WaitForMesh:
                status = "Waiting for vnavmesh before the short ground approach to the Aetheryte";
                return;
            case InstanceChangeAction.Approach:
                StartLocalInstanceApproach(target!, now);
                return;
            case InstanceChangeAction.WaitForApproach:
                PollLocalInstanceApproach(target!, now);
                return;
            case InstanceChangeAction.Interact:
                StopInstanceRangeMovement();
                log.Information("Instance interaction range reached: object={Object}, distance={Distance:0.0}y; interaction attempted, required={Required}, current={Current}",
                    target!.ObjectId, distance, current.Instance, travel.CurrentInstance);
                if (travel.InteractWithInstanceAetheryte(target, instanceRecovery.InteractionRange))
                    instanceRecovery.Submitted(action, now); // Dispatch is not success; require actual UI.
                else
                {
                    instanceRecovery.Failed("Aetheryte failed identity/range/readiness revalidation");
                    log.Warning("Instance Aetheryte interaction rejected before dispatch; re-resolving on next tick");
                }
                return;
        }
    }

    private void StartLocalInstanceApproach(NativeTravel.InstanceAetheryteTarget target, DateTime now)
    {
        if (now < instanceGeometryRetryUtc)
        {
            status = "Waiting briefly before rechecking local Aetheryte ground points";
            return;
        }
        vnav.StopSafe("instance change requires a local Aetheryte approach");
        var player = PlayerPosition();
        var plan = InstanceChangeRecovery.PlanLocalApproach(
            instanceRecoveryOrigin ?? player, player, target.Position,
            instanceRecovery.InteractionRange, vnav.PointOnFloorSafe);
        log.Information("Instance approach geometry: object={Object}, player={Player}, crystal={Crystal}, range={Range:0.0}y, candidates={Candidates}, projectionMisses={Misses}, localityRejected={Locality}, rangeRejected={RangeRejected}",
            target.ObjectId, player, target.Position, instanceRecovery.InteractionRange,
            plan.Points.Count, plan.ProjectionMisses, plan.LocalityRejections, plan.RangeRejections);
        if (plan.Points.Count == 0)
        {
            FailLocalInstanceApproach(now,
                $"no local interaction ground point: projection misses={plan.ProjectionMisses}, locality rejected={plan.LocalityRejections}, range rejected={plan.RangeRejections}");
            return;
        }

        instanceApproachCandidates.Clear();
        foreach (var point in plan.Points)
            instanceApproachCandidates.Enqueue(point);
        instancePathObjectId = target.ObjectId;
        instanceRecovery.Submitted(InstanceChangeAction.Approach, now);
        TryNextLocalInstancePath(target, now);
    }

    private void TryNextLocalInstancePath(NativeTravel.InstanceAetheryteTarget target, DateTime now)
    {
        // Candidate failures share one bounded approach budget, rather than consuming
        // all three recovery attempts at the same unprojectable point.
        if (travel.IsBusy || travel.SelectionMenuIsOpen())
        {
            StopInstanceRangeMovement();
            return;
        }
        while (instanceApproachCandidates.TryDequeue(out var goal))
        {
            instanceLocalPath = vnav.PathfindSafe(PlayerPosition(), goal, false);
            if (instanceLocalPath is null)
                continue;
            log.Information("Local instance Aetheryte ground path requested: object={Object}, goal={Goal}, alternatives={Alternatives}",
                target.ObjectId, goal, instanceApproachCandidates.Count);
            status = "Preparing a short ground walk into Aetheryte interaction range";
            return;
        }
        FailLocalInstanceApproach(now, "all nearby Aetheryte interaction paths were unavailable");
    }

    private void FailLocalInstanceApproach(DateTime now, string reason)
    {
        StopInstanceRangeMovement();
        instanceRecovery.Failed(reason);
        instanceGeometryRetryUtc = now.AddSeconds(2);
        log.Warning("Local instance approach failed: attempt={Attempt}/{Limit}; {Reason}",
            instanceRecovery.Failures, InstanceChangeRecovery.MaximumFailures, reason);
    }

    private void PollLocalInstanceApproach(NativeTravel.InstanceAetheryteTarget target, DateTime now)
    {
        if (!vnav.IsReadySafe())
        {
            FailLocalInstanceApproach(now, "mesh readiness lost during local approach");
            return;
        }
        if (instanceLocalPath is not { IsCompleted: true } task)
            return;
        instanceLocalPath = null;
        // Revalidate races before reading/submitting any asynchronous path result.
        if (travel.IsBusy || travel.SelectionMenuIsOpen())
        {
            StopInstanceRangeMovement();
            return;
        }
        if (target.ObjectId != instancePathObjectId)
        {
            FailLocalInstanceApproach(now, "Aetheryte object changed during local path query");
            return;
        }
        List<Vector3> path;
        try { path = task.GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            log.Warning(ex, "Instance ground-path candidate failed; trying the next nearby point");
            TryNextLocalInstancePath(target, now);
            return;
        }
        if (instanceRecoveryOrigin is not Vector3 origin ||
            !InstanceChangeRecovery.IsLocalPath(path, origin, PlayerPosition(), target.Position))
        {
            log.Information("Instance ground-path candidate rejected by local path limits; trying another nearby point");
            TryNextLocalInstancePath(target, now);
            return;
        }
        instanceLocalMovement = vnav.MovePathSafe(path, false);
        if (!instanceLocalMovement)
        {
            TryNextLocalInstancePath(target, now);
            return;
        }
        instanceApproachCandidates.Clear();
        log.Information("Local instance Aetheryte ground movement started: object={Object}, waypoints={Waypoints}, distance={Distance:0.0}y",
            target.ObjectId, path.Count, Vector3.Distance(PlayerPosition(), target.Position));
        status = "Walking into local Aetheryte interaction range before changing instance";
    }
}
