using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using SentinelHunts.Core;
using SentinelHunts.Services;
using System.Numerics;

namespace SentinelHunts.Orchestration;

internal sealed class HuntCoordinator
{
    private static readonly TimeSpan ActionRetry = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan LongActionRetry = TimeSpan.FromSeconds(10);

    private readonly Configuration config;
    private readonly GameState game;
    private readonly VNavmeshService vnav;
    private readonly LifestreamService lifestream;
    private readonly ActionService actions;
    private readonly IPluginLog log;
    private readonly Queue<Vector3> parkingCandidates = [];

    private DateTimeOffset stateSince = DateTimeOffset.UtcNow;
    private DateTimeOffset nextActionAt = DateTimeOffset.MinValue;
    private DateTimeOffset threatClearSince = DateTimeOffset.MinValue;
    private SentinelState resumeAfterAggro = SentinelState.LocateMark;
    private IBattleChara? mark;
    private ulong markGameObjectId;
    private Vector3? reportPoint;
    private Vector3? safePoint;
    private Task<List<Vector3>>? parkingPathTask;
    private bool parkingMoveIssued;
    private bool tagAttempted;
    private bool tagged;
    private bool activeKillConfirmed;

    public HuntCoordinator(
        Configuration config,
        GameState game,
        VNavmeshService vnav,
        LifestreamService lifestream,
        ActionService actions,
        IPluginLog log)
    {
        this.config = config;
        this.game = game;
        this.vnav = vnav;
        this.lifestream = lifestream;
        this.actions = actions;
        this.log = log;
    }

    public HuntQueue Queue { get; } = new();
    public HuntQueueEntry? Active { get; private set; }
    public SentinelState State { get; private set; } = SentinelState.IdleAtUldah;
    public string Status { get; private set; } = "Observation mode; waiting for reports";
    public DateTimeOffset StateSince => stateSince;
    public bool VNavmeshReady => vnav.IsReady;
    public bool LifestreamBusy => lifestream.IsBusy;

    public event Action? Changed;

    public void Observe(HuntEvent huntEvent)
    {
        var definition = HuntCatalog.FindByDataId(huntEvent.MarkDataId);
        if (definition is null)
            return;

        if (huntEvent.Kind == HuntEventKind.Reported && !config.IsEnabled(definition))
        {
            Status = $"Filtered {definition.Name}: {definition.Expansion} is disabled";
            return;
        }

        if (Active?.Key == huntEvent.Key)
        {
            if (huntEvent.Kind == HuntEventKind.Killed && huntEvent.ObservedAt >= Active.FirstObservedAt)
            {
                activeKillConfirmed = true;
                Status = $"{Active.MarkName} death confirmed by {huntEvent.Source}";
            }
            else if (huntEvent.Kind == HuntEventKind.Reported)
            {
                Active.Merge(huntEvent);
                Status = $"Updated active {Active.MarkName} report from {huntEvent.Source}";
                Changed?.Invoke();
                return;
            }
        }

        var mutation = Queue.Apply(huntEvent);
        if (mutation is QueueMutation.Added or QueueMutation.Merged or QueueMutation.RemovedByKill)
        {
            Changed?.Invoke();
            if (Active?.Key != huntEvent.Key)
                Status = mutation switch
                {
                    QueueMutation.Added => $"Queued {definition.Name} on {huntEvent.WorldName}",
                    QueueMutation.Merged => $"Merged {huntEvent.Source} report for {definition.Name}",
                    _ => $"Removed killed {definition.Name} from the queue",
                };
        }
    }

    public void Tick(DateTimeOffset now)
    {
        if (!game.IsLoggedIn)
        {
            vnav.Stop();
            Status = "Waiting for the player to log in";
            return;
        }

        var removed = Queue.Prune(config.IsEnabled, now, TimeSpan.FromMinutes(config.AlertFreshnessMinutes));
        if (removed > 0)
            Changed?.Invoke();

        if (!config.AutomationEnabled)
        {
            if (vnav.IsRunning)
                vnav.Stop();
            Status = Active is null
                ? $"Observation mode; {Queue.Entries.Count} hunt(s) queued"
                : $"Observation mode; active hunt retained: {Active.MarkName}";
            return;
        }

        if (Active is not null && HuntCatalog.FindByDataId(Active.Key.MarkDataId) is { } activeDefinition &&
            !config.IsEnabled(activeDefinition) && State != SentinelState.ReturnToUldah)
        {
            activeKillConfirmed = true;
            BeginReturn($"{activeDefinition.Expansion} was disabled; abandoning the active hunt safely");
        }

        RefreshMark();
        if (mark is not null && (mark.IsDead || mark.CurrentHp == 0))
            activeKillConfirmed = true;

        if (Active is not null && game.IsDead && State != SentinelState.RecoverDeath)
        {
            resumeAfterAggro = tagged ? SentinelState.TaggedWait : SentinelState.LocateMark;
            SetState(SentinelState.RecoverDeath, "Player died; all movement and combat stopped");
        }

        if (Active is not null && State is not (SentinelState.RecoverDeath or SentinelState.ClearIncidentalAggro or
                SentinelState.ReturnToUldah or SentinelState.PausedError))
        {
            var threat = game.FindIncidentalThreat(markGameObjectId);
            if (threat is not null)
            {
                resumeAfterAggro = State;
                threatClearSince = DateTimeOffset.MinValue;
                vnav.Stop();
                SetState(SentinelState.ClearIncidentalAggro,
                    $"Clearing incidental aggro: {threat.Name.TextValue}");
            }
        }

        if (CheckStateTimeout(now))
            return;

        switch (State)
        {
            case SentinelState.IdleAtUldah:
                TickIdle(now);
                break;
            case SentinelState.EnsureUldah:
                TickEnsureUldah(now);
                break;
            case SentinelState.WorldVisit:
                TickWorldVisit(now);
                break;
            case SentinelState.TeleportToTerritory:
                TickTeleportToTerritory(now);
                break;
            case SentinelState.ChangeInstance:
                TickChangeInstance(now);
                break;
            case SentinelState.WaitForPlayerReady:
                TickWaitForPlayerReady();
                break;
            case SentinelState.ApproachReportedArea:
                TickApproach(now);
                break;
            case SentinelState.LocateMark:
                TickLocate();
                break;
            case SentinelState.ParkSafely:
                TickPark(now);
                break;
            case SentinelState.WaitForPull:
                TickWaitForPull();
                break;
            case SentinelState.TagApproach:
                TickTag(now);
                break;
            case SentinelState.TaggedWait:
                TickTaggedWait();
                break;
            case SentinelState.ClearIncidentalAggro:
                TickClearAggro(now);
                break;
            case SentinelState.RecoverDeath:
                TickRecoverDeath(now);
                break;
            case SentinelState.ReturnToUldah:
                TickReturnToUldah(now);
                break;
            case SentinelState.PausedError:
                break;
        }
    }

    public void StopAutomation()
    {
        vnav.Stop();
        Status = "Automation stopped; reports continue to queue";
    }

    public void RetryCurrent()
    {
        if (Active is null)
            SetState(SentinelState.IdleAtUldah, "Retry requested; waiting for the next hunt");
        else if (game.TerritoryId == Active.Key.TerritoryId)
            SetState(SentinelState.WaitForPlayerReady, "Retrying the active hunt from the current territory");
        else
            SetState(SentinelState.EnsureUldah, "Retrying the active hunt through Ul'dah");
    }

    public void SkipCurrent()
    {
        if (Active is null)
            return;
        log.Warning("User skipped active hunt {Mark} on {World}", Active.MarkName, Active.WorldName);
        activeKillConfirmed = true;
        BeginReturn("Active hunt skipped by user");
    }

    public void ClearQueue()
    {
        Queue.Clear();
        Changed?.Invoke();
        Status = "Queued hunts cleared; active hunt was not changed";
    }

    private void TickIdle(DateTimeOffset now)
    {
        var next = Queue.TakeNext(config.IsEnabled, now, TimeSpan.FromMinutes(config.AlertFreshnessMinutes));
        if (next is not null)
        {
            Active = next;
            ResetActiveRuntime();
            Changed?.Invoke();
            SetState(SentinelState.EnsureUldah,
                $"Selected {next.MarkName} on {next.WorldName}; routing through Ul'dah");
            return;
        }

        if (game.IsReady && game.TerritoryId != LifestreamService.UldahTerritoryId)
            SetState(SentinelState.EnsureUldah, "No hunt is active; returning to the Ul'dah waiting hub");
        else
            Status = "Waiting at Ul'dah for an eligible S-rank report";
    }

    private void TickEnsureUldah(DateTimeOffset now)
    {
        if (game.IsTravelBusy || lifestream.IsBusy)
        {
            Status = "Waiting for current travel to finish";
            return;
        }

        if (game.TerritoryId == LifestreamService.UldahTerritoryId && game.IsReady)
        {
            SetState(Active is null ? SentinelState.IdleAtUldah : SentinelState.WorldVisit,
                Active is null ? "Arrived at the Ul'dah waiting hub" : "Ul'dah ready for World Visit");
            return;
        }

        if (CanAct(now, LongActionRetry) && !lifestream.Teleport(LifestreamService.UldahAetheryteId))
            Status = "Waiting for Lifestream to accept the Ul'dah teleport";
    }

    private void TickWorldVisit(DateTimeOffset now)
    {
        if (Active is null)
        {
            SetState(SentinelState.IdleAtUldah, "No active hunt remains");
            return;
        }

        if (game.CurrentWorldId == Active.Key.WorldId)
        {
            SetState(SentinelState.TeleportToTerritory,
                $"World verified: {Active.WorldName}; resolving territory teleport");
            return;
        }

        if (!game.IsSameDataCenter(Active.Key.WorldId))
        {
            Pause($"{Active.WorldName} is not on the current data center; cross-DC travel is disabled");
            return;
        }

        if (game.TerritoryId != LifestreamService.UldahTerritoryId)
        {
            SetState(SentinelState.EnsureUldah, "World Visit requires the Ul'dah hub");
            return;
        }

        if (game.IsTravelBusy || lifestream.IsBusy)
        {
            Status = $"World Visit to {Active.WorldName} is in progress";
            return;
        }

        if (CanAct(now, LongActionRetry) && !lifestream.ChangeWorld(Active.WorldName))
            Status = $"Waiting for Lifestream to accept World Visit to {Active.WorldName}";
    }

    private void TickTeleportToTerritory(DateTimeOffset now)
    {
        if (Active is null)
        {
            SetState(SentinelState.ReturnToUldah, "Active hunt disappeared before teleport");
            return;
        }

        if (game.TerritoryId == Active.Key.TerritoryId && game.IsReady)
        {
            SetState(SentinelState.ChangeInstance, "Hunt territory reached");
            return;
        }

        if (game.IsTravelBusy || lifestream.IsBusy)
        {
            Status = $"Teleporting toward {Active.MarkName}";
            return;
        }

        var definition = HuntCatalog.FindByDataId(Active.Key.MarkDataId);
        if (definition is null)
        {
            Pause("The active hunt no longer resolves in the catalog");
            return;
        }

        if (definition.TerritoryId == 399)
        {
            Pause("The Dravanian Hinterlands gateway route is not enabled in the first test build");
            return;
        }

        if (CanAct(now, LongActionRetry) && !lifestream.Teleport(definition.PreferredAetheryteId))
            Status = $"Waiting for Lifestream to accept teleport for {definition.Name}";
    }

    private void TickChangeInstance(DateTimeOffset now)
    {
        if (Active is null)
            return;

        if (Active.Key.Instance <= 1 || game.CurrentInstance == Active.Key.Instance)
        {
            SetState(SentinelState.WaitForPlayerReady, "Territory and instance verified");
            return;
        }

        if (game.IsTravelBusy || lifestream.IsBusy)
        {
            Status = $"Changing to instance {Active.Key.Instance}";
            return;
        }

        if (CanAct(now, LongActionRetry) && !lifestream.ChangeInstance(Active.Key.Instance))
            Status = $"Waiting for Lifestream to accept instance {Active.Key.Instance}";
    }

    private void TickWaitForPlayerReady()
    {
        if (!game.IsReady)
        {
            Status = "Waiting for the player and zone to become ready";
            return;
        }

        if (!vnav.IsReady)
        {
            Status = "Waiting for vnavmesh to build/load the current territory mesh";
            return;
        }

        if (Active is null)
            return;

        reportPoint = game.MapToWorld(Active.Key.TerritoryId, Active.MapX, Active.MapY);
        if (reportPoint is null)
        {
            Pause("The alert did not contain usable map coordinates");
            return;
        }

        SetState(SentinelState.ApproachReportedArea,
            $"Approaching the reported area for {Active.MarkName}");
    }

    private void TickApproach(DateTimeOffset now)
    {
        if (Active is null || reportPoint is null)
            return;

        if (mark is not null)
        {
            BeginParking("S rank positively identified before reaching the report point");
            return;
        }

        var distance = SafeParkingPlanner.HorizontalDistance(game.PlayerPosition, reportPoint.Value);
        if (distance <= config.InitialApproachDistance + 3f)
        {
            vnav.Stop();
            SetState(SentinelState.LocateMark, "Reached the outer edge of the reported area; locating the S rank");
            return;
        }

        if (!game.IsMounted)
        {
            if (CanAct(now, ActionRetry))
                actions.TryMount();
            Status = "Mounting before vnavmesh flight approach";
            return;
        }

        if (!vnav.IsRunning && CanAct(now, ActionRetry) &&
            !vnav.MoveCloseTo(reportPoint.Value, true, config.InitialApproachDistance))
            Status = "Waiting for vnavmesh to accept the flight path";
        else
            Status = $"Flying toward {Active.MarkName}; {distance:0} yalms from the report point";
    }

    private void TickLocate()
    {
        if (activeKillConfirmed)
        {
            BeginReturn("The active S rank was confirmed dead while locating it");
            return;
        }

        if (mark is not null)
        {
            BeginParking($"Located {mark.Name.TextValue}; calculating protected parking");
            return;
        }

        if (DateTimeOffset.UtcNow - stateSince > TimeSpan.FromSeconds(90))
            Pause("The S rank was not visible within 90 seconds of reaching the report area");
        else
            Status = $"Scanning loaded actors for {Active?.MarkName}";
    }

    private void TickPark(DateTimeOffset now)
    {
        if (activeKillConfirmed)
        {
            BeginReturn("The active S rank died during parking");
            return;
        }

        if (mark is null)
        {
            SetState(tagged ? SentinelState.TaggedWait : SentinelState.LocateMark,
                "The S rank left the object table; waiting to reacquire it");
            return;
        }

        var clearance = Clearance(mark);
        if (clearance >= config.SafeHitboxClearance &&
            (safePoint is null || SafeParkingPlanner.HorizontalDistance(game.PlayerPosition, safePoint.Value) <= 4f))
        {
            vnav.Stop();
            if (game.IsMounted && CanAct(now, ActionRetry))
                actions.TryDismount();
            if (!game.IsMounted)
                SetState(tagged ? SentinelState.TaggedWait : SentinelState.WaitForPull,
                    tagged ? "Tagged; waiting safely for positive death confirmation" :
                    $"Parked with {clearance:0.0} yalms of hitbox clearance");
            return;
        }

        if (parkingMoveIssued)
        {
            if (vnav.IsRunning)
            {
                Status = $"Moving to protected parking; current clearance {clearance:0.0} yalms";
                return;
            }

            parkingMoveIssued = false;
            if (safePoint is not null && CanAct(now, ActionRetry))
                vnav.MoveCloseTo(safePoint.Value, game.IsMounted || game.IsInFlight, 2f);
            return;
        }

        if (parkingPathTask is not null)
        {
            if (!parkingPathTask.IsCompleted)
            {
                Status = "Checking a protected route to the parking point";
                return;
            }

            if (parkingPathTask.IsCompletedSuccessfully && safePoint is not null)
            {
                var path = parkingPathTask.Result;
                var safe = tagged
                    ? SafeParkingPlanner.PathEscapesProtectedArea(path, mark.Position, game.PlayerHitboxRadius,
                        mark.HitboxRadius, config.SafeHitboxClearance)
                    : SafeParkingPlanner.PathRespectsClearance(path, mark.Position, game.PlayerHitboxRadius,
                        mark.HitboxRadius, config.SafeHitboxClearance);
                if (safe && vnav.MovePath(path, game.IsMounted || game.IsInFlight))
                {
                    parkingMoveIssued = true;
                    parkingPathTask = null;
                    Status = "Following a route that respects the protected hitbox radius";
                    return;
                }
            }

            parkingPathTask = null;
            safePoint = null;
        }

        while (parkingCandidates.Count > 0)
        {
            var candidate = parkingCandidates.Dequeue();
            var projected = vnav.PointOnFloor(candidate, 40f);
            if (projected is null || SafeParkingPlanner.Clearance(projected.Value, mark.Position,
                    game.PlayerHitboxRadius, mark.HitboxRadius) < config.SafeHitboxClearance)
                continue;

            safePoint = projected;
            parkingPathTask = vnav.PathfindAvoid(
                game.PlayerPosition,
                projected.Value,
                game.IsMounted || game.IsInFlight,
                mark.Position,
                game.PlayerHitboxRadius + mark.HitboxRadius + config.SafeHitboxClearance);
            if (parkingPathTask is not null)
                return;
            safePoint = null;
        }

        Pause("No reachable parking point preserved the configured hitbox clearance");
    }

    private void TickWaitForPull()
    {
        if (activeKillConfirmed)
        {
            BeginReturn("The active S rank died before a tag was needed");
            return;
        }

        if (mark is null)
        {
            SetState(SentinelState.LocateMark, "The S rank is not currently visible; reacquiring it");
            return;
        }

        var clearance = Clearance(mark);
        if (clearance < config.SafeHitboxClearance)
        {
            BeginParking($"{mark.Name.TextValue} moved inside the protected radius; re-parking");
            return;
        }

        var hp = HpPercent(mark);
        var inCombat = mark.StatusFlags.HasFlag(StatusFlags.InCombat);
        if (inCombat && hp <= config.EngageHpPercent)
        {
            if (!game.IsWarrior)
            {
                Pause("Tomahawk tagging requires Marauder or Warrior");
                return;
            }
            SetState(SentinelState.TagApproach,
                $"{mark.Name.TextValue} is at {hp:0.0}% HP; approaching for one Tomahawk");
            return;
        }

        Status = $"Waiting safely: {hp:0.0}% HP, {clearance:0.0} yalms hitbox clearance";
    }

    private void TickTag(DateTimeOffset now)
    {
        if (activeKillConfirmed)
        {
            BeginReturn("The active S rank died before the tag attempt");
            return;
        }

        if (mark is null)
        {
            SetState(SentinelState.LocateMark, "Lost the S rank before Tomahawk; reacquiring without attacking");
            return;
        }

        if (tagAttempted)
        {
            BeginParking(tagged ? "Tomahawk accepted; retreating to protected clearance" :
                "Tomahawk was attempted once; retreating without retry spam");
            return;
        }

        var hp = HpPercent(mark);
        if (!mark.StatusFlags.HasFlag(StatusFlags.InCombat) || hp > config.EngageHpPercent)
        {
            vnav.Stop();
            SetState(SentinelState.WaitForPull, "Pull reset detected before Tomahawk; attack gate closed");
            return;
        }

        var centerDistance = SafeParkingPlanner.HorizontalDistance(game.PlayerPosition, mark.Position);
        if (centerDistance > mark.HitboxRadius + 18f)
        {
            if (!vnav.IsRunning && CanAct(now, ActionRetry))
                vnav.MoveCloseTo(mark.Position, false, mark.HitboxRadius + 18f);
            Status = $"Entering Tomahawk range; {centerDistance:0.0} yalms from target center";
            return;
        }

        vnav.Stop();
        if (game.IsMounted)
        {
            if (CanAct(now, ActionRetry))
                actions.TryDismount();
            return;
        }

        var attempt = actions.TryTomahawk(mark);
        if (!attempt.Attempted)
        {
            Status = "In range; waiting for Tomahawk to become usable";
            return;
        }

        tagAttempted = true;
        tagged = attempt.Accepted;
        BeginParking(attempt.Accepted
            ? "One Tomahawk was accepted; retreating immediately"
            : "One Tomahawk request was rejected; retreating without a second attempt");
    }

    private void TickTaggedWait()
    {
        if (activeKillConfirmed)
        {
            BeginReturn("Tagged S rank death confirmed; returning to Ul'dah");
            return;
        }

        if (mark is not null && Clearance(mark) < config.SafeHitboxClearance)
        {
            BeginParking("The tagged S rank moved inside the protected radius; re-parking");
            return;
        }

        Status = "Tag complete; waiting for positive S-rank death confirmation";
    }

    private void TickClearAggro(DateTimeOffset now)
    {
        if (game.IsDead)
        {
            SetState(SentinelState.RecoverDeath, "Incidental enemy killed the player; waiting for recovery");
            return;
        }

        var threat = game.FindIncidentalThreat(markGameObjectId);
        if (threat is null)
        {
            if (threatClearSince == DateTimeOffset.MinValue)
                threatClearSince = now;
            if (now - threatClearSince >= TimeSpan.FromSeconds(2))
                SetState(resumeAfterAggro, "Incidental aggro cleared; resuming the interrupted hunt state");
            else
                Status = "Confirming incidental aggro is clear";
            return;
        }

        threatClearSince = DateTimeOffset.MinValue;
        if (CanAct(now, TimeSpan.FromSeconds(1)))
            actions.TryClearThreat(threat);
        Status = $"Using bounded single-target WAR rotation on {threat.Name.TextValue}";
    }

    private void TickRecoverDeath(DateTimeOffset now)
    {
        vnav.Stop();
        if (!game.IsDead)
        {
            SetState(tagged ? SentinelState.TaggedWait : resumeAfterAggro,
                tagged ? "Raised; resuming tagged wait without attacking again" :
                "Raised; resuming the interrupted hunt state");
            return;
        }

        if (CanAct(now, ActionRetry) && actions.TryAcceptRaise())
        {
            Status = "Accepted Raise; waiting for revival while retaining the active hunt";
            return;
        }

        if (activeKillConfirmed && CanAct(now, LongActionRetry))
        {
            actions.TryReturnWhileDead();
            Status = "Active S rank is confirmed dead; using Return to the configured Ul'dah home point";
            return;
        }

        Status = tagged
            ? "Dead after tagging; Return is locked until the S rank's death is confirmed"
            : "Dead before tagging; waiting for a Raise and retaining the active report";
    }

    private void TickReturnToUldah(DateTimeOffset now)
    {
        if (game.IsDead)
        {
            if (!activeKillConfirmed)
            {
                SetState(SentinelState.RecoverDeath, "Return locked because the active mark is not confirmed dead");
                return;
            }
            if (CanAct(now, LongActionRetry))
                actions.TryReturnWhileDead();
            return;
        }

        if (game.TerritoryId == LifestreamService.UldahTerritoryId && game.IsReady)
        {
            var completed = Active?.MarkName;
            Active = null;
            ResetActiveRuntime();
            Changed?.Invoke();
            SetState(SentinelState.IdleAtUldah,
                string.IsNullOrWhiteSpace(completed) ? "Waiting at Ul'dah" : $"Finished {completed}; waiting at Ul'dah");
            return;
        }

        if (!game.IsTravelBusy && !lifestream.IsBusy && CanAct(now, LongActionRetry) &&
            !lifestream.Teleport(LifestreamService.UldahAetheryteId))
            Status = "Waiting for Lifestream to accept the post-hunt Ul'dah teleport";
    }

    private void RefreshMark()
    {
        if (Active is null || game.TerritoryId != Active.Key.TerritoryId)
        {
            mark = null;
            markGameObjectId = 0;
            return;
        }

        var found = game.FindMark(Active.Key.MarkDataId);
        if (found is null)
        {
            mark = null;
            return;
        }

        mark = found;
        markGameObjectId = found.GameObjectId;
    }

    private void BeginParking(string reason)
    {
        if (mark is null)
        {
            SetState(tagged ? SentinelState.TaggedWait : SentinelState.LocateMark, reason);
            return;
        }

        vnav.Stop();
        parkingCandidates.Clear();
        foreach (var candidate in SafeParkingPlanner.CreateCandidates(
                     mark.Position,
                     game.PlayerPosition,
                     game.PlayerHitboxRadius,
                     mark.HitboxRadius,
                     config.SafeHitboxClearance))
            parkingCandidates.Enqueue(candidate);
        parkingPathTask = null;
        parkingMoveIssued = false;
        safePoint = null;
        SetState(SentinelState.ParkSafely, reason);
    }

    private void BeginReturn(string reason)
    {
        vnav.Stop();
        SetState(SentinelState.ReturnToUldah, reason);
    }

    private void Pause(string reason)
    {
        vnav.Stop();
        SetState(SentinelState.PausedError, reason);
    }

    private void SetState(SentinelState next, string status)
    {
        State = next;
        Status = status;
        stateSince = DateTimeOffset.UtcNow;
        nextActionAt = DateTimeOffset.MinValue;
        log.Information("Sentinel Hunts state -> {State}: {Status}", next, status);
    }

    private bool CanAct(DateTimeOffset now, TimeSpan retry)
    {
        if (now < nextActionAt)
            return false;
        nextActionAt = now + retry;
        return true;
    }

    private bool CheckStateTimeout(DateTimeOffset now)
    {
        var timeout = State switch
        {
            SentinelState.EnsureUldah => TimeSpan.FromMinutes(3),
            SentinelState.WorldVisit => TimeSpan.FromMinutes(4),
            SentinelState.TeleportToTerritory => TimeSpan.FromMinutes(2),
            SentinelState.ChangeInstance => TimeSpan.FromMinutes(2),
            SentinelState.WaitForPlayerReady => TimeSpan.FromMinutes(2),
            SentinelState.ApproachReportedArea => TimeSpan.FromMinutes(4),
            SentinelState.LocateMark => TimeSpan.FromSeconds(90),
            SentinelState.ParkSafely => TimeSpan.FromMinutes(2),
            SentinelState.TagApproach => TimeSpan.FromMinutes(1),
            SentinelState.ClearIncidentalAggro => TimeSpan.FromMinutes(2),
            SentinelState.ReturnToUldah => TimeSpan.FromMinutes(3),
            _ => TimeSpan.Zero,
        };

        if (timeout == TimeSpan.Zero || now - stateSince <= timeout)
            return false;

        Pause($"{State} timed out after {timeout.TotalSeconds:0} seconds; no death was inferred");
        return true;
    }

    private void ResetActiveRuntime()
    {
        mark = null;
        markGameObjectId = 0;
        reportPoint = null;
        safePoint = null;
        parkingCandidates.Clear();
        parkingPathTask = null;
        parkingMoveIssued = false;
        tagAttempted = false;
        tagged = false;
        activeKillConfirmed = false;
        threatClearSince = DateTimeOffset.MinValue;
    }

    private float Clearance(IBattleChara target) => SafeParkingPlanner.Clearance(
        game.PlayerPosition,
        target.Position,
        game.PlayerHitboxRadius,
        target.HitboxRadius);

    private static float HpPercent(IBattleChara target) =>
        target.MaxHp == 0 ? 100f : target.CurrentHp * 100f / target.MaxHp;
}
