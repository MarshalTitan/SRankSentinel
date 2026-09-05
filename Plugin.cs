using Dalamud.Bindings.ImGui;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using System.Numerics;

namespace SRankSentinel;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => "S Rank Sentinel";

    private const string Command = "/sranksentinel";
    private readonly IDalamudPluginInterface pi;
    private readonly ICommandManager commands;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IChatGui chat;
    private readonly IObjectTable objects;
    private readonly IDataManager data;
    private readonly IGameGui gameGui;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly VNavmeshIpc vnav;
    private readonly NativeTravel travel;
    private readonly CombatController combat;
    private readonly ICallGateSubscriber<HuntTrainMessageDto, object> huntAlerts;
    private readonly Configuration config;
    private readonly Queue<HuntAlertSnapshot> pendingAlerts = new();
    private readonly Queue<Vector3> parkingCandidates = new();

    private bool configOpen;
    private HuntAlertSnapshot? current;
    private IBattleChara? mark;
    private MapLinkPayload? currentMapLink;
    private Vector3? flagPoint;
    private Vector3? safePoint;
    private uint territoryAetheryteId;
    private SentinelState state = SentinelState.Idle;
    private DateTime stateSinceUtc = DateTime.UtcNow;
    private DateTime lastTickUtc = DateTime.MinValue;
    private DateTime nextActionUtc = DateTime.MinValue;
    private bool flagPrepared;
    private bool killConfirmed;
    private bool tagAttempted;
    private uint activeTagActionId;
    private bool discardAtUldah;
    private bool returningHome;
    private string status = "Idle";

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IClientState clientState,
        ICondition condition,
        IChatGui chatGui,
        IObjectTable objectTable,
        IDataManager dataManager,
        IGameGui gameGui,
        IFramework framework,
        ITargetManager targetManager,
        IPluginLog pluginLog)
    {
        pi = pluginInterface;
        commands = commandManager;
        this.clientState = clientState;
        this.condition = condition;
        chat = chatGui;
        objects = objectTable;
        data = dataManager;
        this.gameGui = gameGui;
        this.framework = framework;
        log = pluginLog;

        config = pi.GetPluginConfig() as Configuration ?? new Configuration();
        config.Initialize(pi);
        vnav = new VNavmeshIpc(pi);
        travel = new NativeTravel(gameGui, objectTable, targetManager, condition, dataManager);
        combat = new CombatController(gameGui, condition, objectTable, targetManager);

        huntAlerts = pi.GetIpcSubscriber<HuntTrainMessageDto, object>("HuntAlerts.OnHuntTrainMessageReceived");
        huntAlerts.Subscribe(OnHuntAlert);
        chat.ChatMessage += OnSonarChatMessage;
        framework.Update += OnFrameworkUpdate;
        pi.UiBuilder.Draw += DrawUi;
        pi.UiBuilder.OpenMainUi += OpenConfig;
        pi.UiBuilder.OpenConfigUi += OpenConfig;
        commands.AddHandler(Command, new CommandInfo((_, _) => configOpen = true)
        {
            HelpMessage = "Open S Rank Sentinel settings/status."
        });

        log.Information("S Rank Sentinel standalone orchestrator loaded.");
    }

    public void Dispose()
    {
        vnav.StopSafe();
        huntAlerts.Unsubscribe(OnHuntAlert);
        chat.ChatMessage -= OnSonarChatMessage;
        framework.Update -= OnFrameworkUpdate;
        pi.UiBuilder.Draw -= DrawUi;
        pi.UiBuilder.OpenMainUi -= OpenConfig;
        pi.UiBuilder.OpenConfigUi -= OpenConfig;
        commands.RemoveHandler(Command);
    }

    private void OpenConfig() => configOpen = true;

    private void OnHuntAlert(HuntTrainMessageDto payload)
    {
        if (!config.Enabled || payload is null)
            return;

        AcceptSRankAlert(
            payload.huntType,
            payload.huntWorld,
            payload.creatureName,
            payload.startTerritoryTypeId,
            payload.instance,
            payload.mapLocationX,
            payload.mapLocationY,
            "HuntAlerts");
    }

    private void OnSonarChatMessage(IHandleableChatMessage chatMessage)
    {
        if (!config.Enabled || !chatMessage.Sender.TextValue.Equals("Sonar", StringComparison.Ordinal))
            return;

        try
        {
            var text = chatMessage.Message.TextValue;
            if (text.Contains("was just killed", StringComparison.OrdinalIgnoreCase))
            {
                var killedWorld = ParseSonarWorld(text);
                if (current is not null &&
                    text.Contains(current.CreatureName, StringComparison.OrdinalIgnoreCase) &&
                    KillNoticeMatchesWorld(killedWorld, current))
                    ConfirmKill($"Sonar confirmed {current.CreatureName} was killed");
                RemoveKilledQueuedAlerts(text, killedWorld);
                return;
            }

            const string prefix = "Rank S:";
            var namePayload = chatMessage.Message.Payloads
                .OfType<TextPayload>()
                .Select(payload => payload.Text)
                .FirstOrDefault(value => value?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true);
            if (namePayload is null)
                return;

            var creature = namePayload[prefix.Length..].Trim();
            var mapLink = chatMessage.Message.Payloads.OfType<MapLinkPayload>().FirstOrDefault();
            if (mapLink is null || string.IsNullOrWhiteSpace(creature))
            {
                status = "Sonar S-rank alert did not contain a readable creature/map link";
                return;
            }

            AcceptSRankAlert(
                "srank",
                ParseSonarWorld(text),
                creature,
                mapLink.TerritoryType.RowId,
                ParseSonarInstance(text),
                mapLink.XCoord,
                mapLink.YCoord,
                "Sonar");
        }
        catch (Exception ex)
        {
            status = "Sonar alert could not be parsed; no travel was started";
            log.Warning("Could not parse Sonar S-rank message safely: {Error}", ex.Message);
        }
    }

    private void AcceptSRankAlert(
        string? huntType,
        string? world,
        string? creature,
        uint territory,
        int instance,
        float mapX,
        float mapY,
        string source)
    {
        if (!string.Equals(huntType, "srank", StringComparison.OrdinalIgnoreCase))
            return;
        if (territory == 0 || string.IsNullOrWhiteSpace(creature))
        {
            status = $"{source} alert was missing the S-rank name or territory";
            return;
        }

        world = string.IsNullOrWhiteSpace(world) ? travel.CurrentWorld : world.Trim();
        if (!travel.IsSameDataCenter(world))
        {
            status = $"Ignored {source} alert for {creature.Trim()} on {world}: normal World Visit cannot cross data centers";
            return;
        }

        instance = Math.Max(1, instance);
        var definition = HuntCatalog.Resolve(territory, creature);
        var incoming = new HuntAlertSnapshot(
            "srank", world, creature.Trim(), territory,
            definition?.DataId ?? 0, definition?.PreferredAetheryteId ?? 0,
            instance, mapX, mapY, DateTime.UtcNow);
        if (current?.Key == incoming.Key || pendingAlerts.Any(alert => alert.Key == incoming.Key))
            return;

        if (current is not null || state != SentinelState.Idle)
        {
            pendingAlerts.Enqueue(incoming);
            status = $"Queued {incoming.CreatureName}; {pendingAlerts.Count} S rank(s) waiting";
            return;
        }

        StartAlert(incoming, source);
    }

    private void StartAlert(HuntAlertSnapshot alert, string source)
    {
        current = alert;
        mark = null;
        killConfirmed = false;
        tagAttempted = false;
        activeTagActionId = 0;
        discardAtUldah = false;
        returningHome = false;
        PrepareCurrentTravel();
        SetState(SentinelState.ResetToUldah,
            $"{source}: resetting through Ul'dah before {alert.CreatureName} on {alert.World}");
    }

    private void PrepareCurrentTravel()
    {
        territoryAetheryteId = 0;
        currentMapLink = null;
        flagPoint = null;
        safePoint = null;
        flagPrepared = false;
        parkingCandidates.Clear();
        nextActionUtc = DateTime.MinValue;

        if (current is null)
            return;

        var attuned = data.GetExcelSheet<Aetheryte>()
            .Where(row => row.IsAetheryte && row.Territory.RowId == current.TerritoryId)
            .Select(row => row.RowId)
            .Where(travel.CanTeleportTo)
            .ToArray();
        territoryAetheryteId = attuned.FirstOrDefault(id => id == current.PreferredAetheryteId);
        if (territoryAetheryteId == 0)
            territoryAetheryteId = attuned.FirstOrDefault();
        var map = data.GetExcelSheet<Map>()
            .FirstOrDefault(row => row.TerritoryType.RowId == current.TerritoryId);
        if (map.RowId != 0)
            currentMapLink = new MapLinkPayload(current.TerritoryId, map.RowId, current.MapX, current.MapY);
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!config.Enabled || (current is null && state == SentinelState.Idle))
            return;

        var now = DateTime.UtcNow;
        if ((now - lastTickUtc).TotalMilliseconds < 250)
            return;
        lastTickUtc = now;

        try
        {
            Tick(now);
        }
        catch (Exception ex)
        {
            log.Error(ex, "S Rank Sentinel state machine failed; resetting safely.");
            FailCurrent("Internal error; resetting through Ul'dah");
        }
    }

    private void Tick(DateTime now)
    {
        if (current is not null && !killConfirmed && clientState.TerritoryType == current.TerritoryId)
        {
            var visibleMark = FindMark();
            mark = visibleMark;
            if (visibleMark is not null && (visibleMark.IsDead || visibleMark.CurrentHp == 0))
            {
                ConfirmKill($"{visibleMark.Name.TextValue} is dead");
                return;
            }

            if (combat.IsPlayerDead)
            {
                if (visibleMark is not null && combat.TryAcceptRaise())
                    status = $"Accepted Raise while {visibleMark.Name.TextValue} is still alive";
                else
                    status = visibleMark is null
                        ? "Dead before the mark's death was confirmed; waiting for Raise (Return is locked)"
                        : $"Dead while {visibleMark.Name.TextValue} is alive; waiting for Raise (Return is locked)";
                return;
            }
        }

        switch (state)
        {
            case SentinelState.ResetToUldah:
                TickResetToUldah(now);
                return;
            case SentinelState.WorldVisit:
                TickWorldVisit(now);
                return;
            case SentinelState.SelectWorld:
                TickSelectWorld(now);
                return;
            case SentinelState.ConfirmWorldVisit:
                TickConfirmWorldVisit(now);
                return;
            case SentinelState.WaitForWorld:
                TickWaitForWorld(now);
                return;
            case SentinelState.TeleportToTerritory:
                TickTeleportToTerritory(now);
                return;
            case SentinelState.WaitForTerritory:
                TickWaitForTerritory(now);
                return;
            case SentinelState.ChangeInstance:
                TickChangeInstance(now);
                return;
            case SentinelState.SelectInstance:
                TickSelectInstance(now);
                return;
            case SentinelState.WaitForInstance:
                TickWaitForInstance(now);
                return;
            case SentinelState.WaitForFlag:
                TickWaitForFlag(now);
                return;
            case SentinelState.ApproachFlag:
                TickApproachFlag(now);
                return;
            case SentinelState.MoveToSafePoint:
                TickMoveToSafePoint(now);
                return;
            case SentinelState.Landing:
                TickLanding(now);
                return;
            case SentinelState.SafeWait:
                TickSafeWait(now);
                return;
            case SentinelState.TagApproach:
                TickTagApproach(now);
                return;
            case SentinelState.GroundRetreat:
                TickGroundRetreat(now);
                return;
        }
    }

    private void TickResetToUldah(DateTime now)
    {
        if (travel.IsBusy)
            return;

        if (travel.IsInUldah(clientState.TerritoryType))
        {
            if (killConfirmed || discardAtUldah)
            {
                var finished = current?.CreatureName ?? "alert";
                ClearCurrent();
                if (pendingAlerts.TryDequeue(out var next))
                {
                    StartAlert(next, $"{finished} cleared; next queued alert");
                    return;
                }

                if (BeginReturnHome(now, $"{finished} cleared"))
                    return;
                SetState(SentinelState.Idle, $"{finished} cleared; reset complete in Ul'dah");
                return;
            }

            if (current is null)
            {
                if (pendingAlerts.TryDequeue(out var next))
                {
                    StartAlert(next, "Ul'dah reset complete; next queued alert");
                    return;
                }
                if (BeginReturnHome(now, "Ul'dah reset complete"))
                    return;
                SetState(SentinelState.Idle, "Reset complete in Ul'dah");
                return;
            }

            if (!string.IsNullOrWhiteSpace(current.World) &&
                !travel.CurrentWorld.Equals(current.World, StringComparison.OrdinalIgnoreCase))
            {
                nextActionUtc = now;
                SetState(SentinelState.WorldVisit, $"Starting normal World Visit to {current.World}");
                return;
            }

            SetState(SentinelState.TeleportToTerritory,
                $"World ready; preparing normal teleport toward {current.CreatureName}");
            return;
        }

        if (combat.IsPlayerDead)
        {
            if (!killConfirmed)
            {
                status = "Return locked until the S rank's death is confirmed";
                return;
            }

            if (now >= nextActionUtc && combat.UseReturn())
            {
                status = "S rank dead and player still dead; using normal Return";
                nextActionUtc = now.AddSeconds(10);
            }
            return;
        }

        if (now >= nextActionUtc)
        {
            if (condition[ConditionFlag.Mounted])
                UseGeneralAction(23);
            else if (travel.Teleport(NativeTravel.UldahAetheryteId))
                status = "Teleporting normally to Ul'dah for the mandatory reset";
            nextActionUtc = now.AddSeconds(8);
        }
    }

    private void TickWorldVisit(DateTime now)
    {
        var targetWorld = WorldVisitTarget();
        if (string.IsNullOrWhiteSpace(targetWorld))
        {
            FailWorldVisit("World Visit had no valid destination");
            return;
        }
        if (travel.CurrentWorld.Equals(targetWorld, StringComparison.OrdinalIgnoreCase))
        {
            CompleteWorldVisit(targetWorld);
            return;
        }
        if (TravelTimedOut(now))
        {
            FailWorldVisit($"World Visit to {targetWorld} timed out");
            return;
        }

        if (travel.SelectWorldVisitMenu())
        {
            vnav.StopSafe();
            SetState(SentinelState.SelectWorld, $"Selecting {targetWorld} from World Visit");
            return;
        }

        if (now < nextActionUtc || travel.IsBusy)
            return;
        if (condition[ConditionFlag.Mounted])
            UseGeneralAction(23);
        else if (!travel.InteractWithNearbyAetheryte() && vnav.IsReadySafe() &&
                 !vnav.IsPathRunningSafe() && !vnav.IsPathfindInProgressSafe())
        {
            vnav.MoveToSafe(NativeTravel.UldahAetherytePosition, false);
            status = "Walking normally to the Ul'dah World Visit aetheryte";
        }
        nextActionUtc = now.AddSeconds(2);
    }

    private void TickSelectWorld(DateTime now)
    {
        var targetWorld = WorldVisitTarget();
        if (string.IsNullOrWhiteSpace(targetWorld))
            return;
        if (TravelTimedOut(now))
        {
            FailWorldVisit($"Could not select {targetWorld} in World Visit");
            return;
        }
        if (travel.SelectWorld(targetWorld))
            SetState(SentinelState.ConfirmWorldVisit, $"Confirming normal World Visit to {targetWorld}");
    }

    private void TickConfirmWorldVisit(DateTime now)
    {
        var targetWorld = WorldVisitTarget();
        if (string.IsNullOrWhiteSpace(targetWorld))
            return;
        if (travel.CurrentWorld.Equals(targetWorld, StringComparison.OrdinalIgnoreCase))
        {
            SetState(SentinelState.WaitForWorld, $"Arriving on {targetWorld}");
            return;
        }
        if (TravelTimedOut(now))
        {
            FailWorldVisit($"World Visit confirmation for {targetWorld} timed out");
            return;
        }
        if (travel.ConfirmWorldVisit(targetWorld))
            SetState(SentinelState.WaitForWorld, $"Queued for World Visit to {targetWorld}");
    }

    private void TickWaitForWorld(DateTime now)
    {
        var targetWorld = WorldVisitTarget();
        if (string.IsNullOrWhiteSpace(targetWorld))
            return;
        if (!travel.IsBusy && travel.CurrentWorld.Equals(targetWorld, StringComparison.OrdinalIgnoreCase) &&
            travel.IsInUldah(clientState.TerritoryType))
        {
            CompleteWorldVisit(targetWorld);
            return;
        }
        if (TravelTimedOut(now))
            FailWorldVisit($"Arrival on {targetWorld} timed out");
    }

    private bool BeginReturnHome(DateTime now, string reason)
    {
        var homeWorld = travel.HomeWorld;
        if (string.IsNullOrWhiteSpace(homeWorld) ||
            travel.CurrentWorld.Equals(homeWorld, StringComparison.OrdinalIgnoreCase))
            return false;

        returningHome = true;
        nextActionUtc = now;
        SetState(SentinelState.WorldVisit, $"{reason}; returning normally to home world {homeWorld}");
        return true;
    }

    private string WorldVisitTarget() => returningHome ? travel.HomeWorld : current?.World ?? string.Empty;

    private void CompleteWorldVisit(string targetWorld)
    {
        vnav.StopSafe();
        if (returningHome)
        {
            returningHome = false;
            SetState(SentinelState.Idle, $"Returned home to {targetWorld}; standing by in Ul'dah");
            return;
        }

        SetState(SentinelState.TeleportToTerritory,
            $"Arrived on {targetWorld}; preparing territory teleport");
    }

    private void FailWorldVisit(string reason)
    {
        vnav.StopSafe();
        if (returningHome)
        {
            returningHome = false;
            SetState(SentinelState.Idle, $"{reason}; remaining safely at the Ul'dah aetheryte");
            return;
        }

        FailCurrent(reason);
    }

    private void TickTeleportToTerritory(DateTime now)
    {
        if (current is null)
            return;
        if (clientState.TerritoryType == current.TerritoryId)
        {
            BeginInstanceCheck();
            return;
        }
        if (territoryAetheryteId == 0)
        {
            FailCurrent($"No attuned aetheryte was found for territory {current.TerritoryId}");
            return;
        }
        if (now < nextActionUtc || travel.IsBusy)
            return;
        if (condition[ConditionFlag.Mounted])
            UseGeneralAction(23);
        else if (travel.Teleport(territoryAetheryteId))
        {
            SetState(SentinelState.WaitForTerritory, $"Teleporting normally toward {current.CreatureName}");
            return;
        }
        nextActionUtc = now.AddSeconds(3);
    }

    private void TickWaitForTerritory(DateTime now)
    {
        if (current is null)
            return;
        if (!travel.IsBusy && clientState.TerritoryType == current.TerritoryId)
        {
            BeginInstanceCheck();
            return;
        }
        if (TravelTimedOut(now))
            FailCurrent($"Teleport arrival in territory {current.TerritoryId} timed out");
    }

    private void BeginInstanceCheck()
    {
        if (current is null)
            return;
        var instance = travel.CurrentInstance;
        if (instance == current.Instance || (current.Instance == 1 && instance == 0))
        {
            SetState(SentinelState.WaitForFlag, "Territory and instance ready; resolving map flag");
            return;
        }
        nextActionUtc = DateTime.UtcNow;
        SetState(SentinelState.ChangeInstance, $"Changing normally to instance {current.Instance}");
    }

    private void TickChangeInstance(DateTime now)
    {
        if (current is null)
            return;
        if (travel.CurrentInstance == current.Instance)
        {
            SetState(SentinelState.WaitForFlag, "Correct instance reached; resolving map flag");
            return;
        }
        if (TravelTimedOut(now))
        {
            FailCurrent($"Could not change to instance {current.Instance}");
            return;
        }
        if (travel.SelectInstanceTravelMenu())
        {
            SetState(SentinelState.SelectInstance, $"Selecting instance {current.Instance}");
            return;
        }
        if (now < nextActionUtc || travel.IsBusy)
            return;
        if (condition[ConditionFlag.Mounted])
            UseGeneralAction(23);
        else if (!travel.InteractWithNearbyAetheryte(15f) &&
                 (now - stateSinceUtc).TotalSeconds > 4 &&
                 territoryAetheryteId != 0 && travel.Teleport(territoryAetheryteId))
        {
            status = "Repositioning normally at the territory aetheryte to change instance";
        }
        nextActionUtc = now.AddSeconds(3);
    }

    private void TickSelectInstance(DateTime now)
    {
        if (current is null)
            return;
        if (TravelTimedOut(now))
        {
            FailCurrent($"Instance {current.Instance} was not available");
            return;
        }
        if (travel.SelectInstance(current.Instance))
            SetState(SentinelState.WaitForInstance, $"Traveling to instance {current.Instance}");
    }

    private void TickWaitForInstance(DateTime now)
    {
        if (current is null)
            return;
        if (!travel.IsBusy && clientState.TerritoryType == current.TerritoryId &&
            travel.CurrentInstance == current.Instance)
        {
            SetState(SentinelState.WaitForFlag, "Correct instance reached; resolving map flag");
            return;
        }
        if (TravelTimedOut(now))
            FailCurrent($"Arrival in instance {current.Instance} timed out");
    }

    private void TickWaitForFlag(DateTime now)
    {
        if (current is null)
            return;
        if (!vnav.IsReadySafe())
        {
            status = "Waiting for vnavmesh to become ready";
            return;
        }
        if (!flagPrepared && currentMapLink is not null)
            flagPrepared = gameGui.OpenMapWithMapLink(currentMapLink);
        flagPoint = vnav.FlagToPointSafe();
        if (flagPoint is null)
        {
            if ((now - stateSinceUtc).TotalSeconds > 30)
                FailCurrent("Map flag could not be projected onto vnavmesh");
            return;
        }
        if (!EnsureMounted(now))
            return;
        if (vnav.MoveCloseToSafe(flagPoint.Value, true, config.FlagApproachDistance))
            SetState(SentinelState.ApproachFlag,
                $"Flying toward the flag; initial stop {config.FlagApproachDistance:0}y away");
    }

    private void TickApproachFlag(DateTime now)
    {
        mark = FindMark();
        if (mark is not null)
        {
            vnav.StopSafe();
            BeginSafeParking(mark, true);
            return;
        }
        if ((now - stateSinceUtc).TotalSeconds > config.LocateTimeoutSeconds)
            FailCurrent("The named S rank never became visible; refusing to guess its position");
    }

    private void TickMoveToSafePoint(DateTime now)
    {
        mark = FindMark();
        if (mark is null)
        {
            FailCurrent("Lost positive identification of the S rank while parking");
            return;
        }
        if (safePoint is not null && HorizontalDistance(PlayerPosition(), safePoint.Value) <= 5f)
        {
            vnav.StopSafe();
            SetState(SentinelState.Landing, "At the safe parking point; landing normally");
            return;
        }
        if ((now - stateSinceUtc).TotalSeconds > 4 && !vnav.IsPathRunningSafe() && !vnav.IsPathfindInProgressSafe())
        {
            if (!TryStartNextParkingRoute(true))
            {
                FailCurrent("All sampled safe parking routes were unreachable");
                return;
            }
            SetState(SentinelState.MoveToSafePoint,
                $"Trying another safe parking route ({parkingCandidates.Count} alternatives remain)");
        }
    }

    private void TickLanding(DateTime now)
    {
        if (now < nextActionUtc)
            return;
        if (condition[ConditionFlag.InFlight] || condition[ConditionFlag.Mounted])
        {
            UseGeneralAction(23);
            nextActionUtc = now.AddSeconds(1);
            return;
        }
        SetState(SentinelState.SafeWait,
            $"Parked {config.WaitingDistance:0}y clear; emergency floor {config.EmergencyDistance:0}y");
    }

    private void TickSafeWait(DateTime now)
    {
        mark = FindMark();
        if (mark is null)
        {
            status = "Mark temporarily not visible; holding position and refusing to chase";
            return;
        }

        var clearance = ClearanceFromMark(mark);
        var hp = CombatController.HpPercent(mark);
        var inCombat = CombatController.IsMarkInCombat(mark);
        status = $"Safe wait: {mark.Name.TextValue} {clearance:0.0}y clear, {hp:0.0}% HP, " +
                 (inCombat ? "in combat" : "not in combat");

        if (clearance < config.EmergencyDistance)
        {
            BeginGroundRetreat(mark);
            return;
        }

        if (!tagAttempted && inCombat && hp <= config.EngageHpPercent)
        {
            activeTagActionId = combat.ResolveTagActionId(config.AutomaticTagAction, config.TagActionId);
            if (activeTagActionId == 0)
            {
                status = "Combat/HP gate passed, but this job has no supported ranged tag; waiting without attacking";
                return;
            }

            combat.TargetMark(mark);
            var desiredCenterRange = mark.HitboxRadius + (objects.LocalPlayer?.HitboxRadius ?? 0f) + 18f;
            if (vnav.MoveCloseToSafe(mark.Position, false, desiredCenterRange))
                SetState(SentinelState.TagApproach,
                    $"Combat/HP gate passed ({hp:0.0}%); targeted mark and moving into range for action {activeTagActionId}");
        }
    }

    private void TickTagApproach(DateTime now)
    {
        mark = FindMark();
        if (mark is null)
        {
            vnav.StopSafe();
            SetState(SentinelState.SafeWait, "Mark lost during tag approach; holding safely");
            return;
        }

        if (tagAttempted)
        {
            if (now >= nextActionUtc)
            {
                BeginGroundRetreat(mark);
                status = $"Attack cutoff active; retreating to {config.WaitingDistance:0}y after the one tag attempt";
            }
            else
            {
                status = $"One tag attempt sent (action {activeTagActionId}); holding still briefly so casted tags are not cancelled";
            }
            return;
        }

        if (!CombatController.IsMarkInCombat(mark) || CombatController.HpPercent(mark) > config.EngageHpPercent)
        {
            vnav.StopSafe();
            BeginGroundRetreat(mark);
            return;
        }


        combat.TargetMark(mark);

        if (ClearanceFromMark(mark) <= 20f)
        {
            vnav.StopSafe();
            if (now >= nextActionUtc)
            {
                var attempt = combat.TrySingleTag(activeTagActionId, mark);
                if (attempt.Attempted)
                {
                    tagAttempted = true;
                    nextActionUtc = now.AddSeconds(3);
                    status = $"One tag attempt sent (action {activeTagActionId}); client " +
                             (attempt.Accepted ? "accepted it" : "did not accept it") +
                             "; attack cutoff is active and no further attacks will be issued";
                    return;
                }

                nextActionUtc = now.AddSeconds(1);
            }
        }

        if ((now - stateSinceUtc).TotalSeconds > 3 && !vnav.IsPathRunningSafe() && !vnav.IsPathfindInProgressSafe())
        {
            var desiredCenterRange = mark.HitboxRadius + (objects.LocalPlayer?.HitboxRadius ?? 0f) + 18f;
            vnav.MoveCloseToSafe(mark.Position, false, desiredCenterRange);
        }
    }

    private void TickGroundRetreat(DateTime now)
    {
        mark = FindMark();
        if (mark is null)
        {
            vnav.StopSafe();
            SetState(SentinelState.SafeWait, "Mark lost during retreat; stopped safely");
            return;
        }
        if (ClearanceFromMark(mark) >= config.WaitingDistance - 2f)
        {
            vnav.StopSafe();
            SetState(SentinelState.SafeWait, tagAttempted ? "One tag attempt completed; safe radius restored" : "Safe radius restored");
            return;
        }
        if ((now - stateSinceUtc).TotalSeconds > 4 && !vnav.IsPathRunningSafe() && !vnav.IsPathfindInProgressSafe())
        {
            if (!TryStartNextParkingRoute(false))
            {
                vnav.StopSafe();
                SetState(SentinelState.SafeWait, "No ground-retreat route was reachable; holding position");
                return;
            }
            SetState(SentinelState.GroundRetreat,
                $"Trying another ground-retreat route ({parkingCandidates.Count} alternatives remain)");
        }
    }

    private void BeginSafeParking(IBattleChara target, bool fly)
    {
        if (fly && !EnsureMounted(DateTime.UtcNow))
            return;
        PrepareParkingCandidates(target, config.WaitingDistance);
        if (!TryStartNextParkingRoute(fly))
        {
            FailCurrent("No reachable sampled safe parking point was found");
            return;
        }
        SetState(SentinelState.MoveToSafePoint,
            $"Parking dynamically {config.WaitingDistance:0}y clear of the S rank ({parkingCandidates.Count} alternatives ready)");
    }

    private void BeginGroundRetreat(IBattleChara target)
    {
        PrepareParkingCandidates(target, config.WaitingDistance);
        if (!TryStartNextParkingRoute(false))
        {
            vnav.StopSafe();
            SetState(SentinelState.SafeWait, "No sampled ground-retreat route was reachable; holding position");
            return;
        }
        SetState(SentinelState.GroundRetreat,
            $"Retreating to {config.WaitingDistance:0}y clear ({parkingCandidates.Count} alternatives ready)");
    }

    private void PrepareParkingCandidates(IBattleChara target, float clearance)
    {
        parkingCandidates.Clear();
        safePoint = null;
        var player = PlayerPosition();
        var away = player - target.Position;
        away.Y = 0;
        if (away.LengthSquared() < 0.01f)
            away = Vector3.UnitX;
        away = Vector3.Normalize(away);

        ReadOnlySpan<float> angles = [0f, 30f, -30f, 60f, -60f, 90f, -90f, 120f, -120f, 150f, -150f, 180f];
        ReadOnlySpan<float> extraRadii = [0f, 8f];
        var hitboxPadding = target.HitboxRadius + (objects.LocalPlayer?.HitboxRadius ?? 0f);
        var centerRadius = clearance + hitboxPadding;
        var minimumCenterDistance = config.EmergencyDistance + hitboxPadding + 3f;
        var accepted = new List<Vector3>();

        foreach (var extra in extraRadii)
        {
            foreach (var angle in angles)
            {
                var radians = angle * MathF.PI / 180f;
                var cos = MathF.Cos(radians);
                var sin = MathF.Sin(radians);
                var direction = new Vector3(
                    away.X * cos - away.Z * sin,
                    0f,
                    away.X * sin + away.Z * cos);
                var candidate = target.Position + direction * (centerRadius + extra);
                candidate.Y = 1024f;
                var projected = vnav.PointOnFloorSafe(candidate, 12f);
                if (projected is null || HorizontalDistance(projected.Value, target.Position) < minimumCenterDistance)
                    continue;
                if (accepted.Any(point => HorizontalDistance(point, projected.Value) < 2f))
                    continue;
                accepted.Add(projected.Value);
                parkingCandidates.Enqueue(projected.Value);
            }
        }
    }

    private bool TryStartNextParkingRoute(bool fly)
    {
        while (parkingCandidates.Count > 0)
        {
            var candidate = parkingCandidates.Dequeue();
            if (!vnav.MoveToSafe(candidate, fly))
                continue;
            safePoint = candidate;
            return true;
        }
        safePoint = null;
        return false;
    }

    private IBattleChara? FindMark()
    {
        if (current is null || clientState.TerritoryType != current.TerritoryId)
            return null;
        return objects
            .OfType<IBattleChara>()
            .FirstOrDefault(o => o.ObjectKind == ObjectKind.BattleNpc &&
                ((current.MarkDataId != 0 && o.BaseId == current.MarkDataId) ||
                 o.Name.TextValue.Equals(current.CreatureName, StringComparison.OrdinalIgnoreCase)));
    }

    private void ConfirmKill(string reason)
    {
        if (current is null || killConfirmed)
            return;
        killConfirmed = true;
        discardAtUldah = false;
        vnav.StopSafe();
        nextActionUtc = DateTime.UtcNow.AddSeconds(2);
        SetState(SentinelState.ResetToUldah, $"{reason}; resetting through Ul'dah");
    }

    private void FailCurrent(string reason)
    {
        vnav.StopSafe();
        discardAtUldah = true;
        nextActionUtc = DateTime.UtcNow;
        SetState(SentinelState.ResetToUldah, $"{reason}; discarding this alert after the Ul'dah reset");
    }

    private void ClearCurrent()
    {
        vnav.StopSafe();
        current = null;
        mark = null;
        currentMapLink = null;
        flagPoint = null;
        safePoint = null;
        territoryAetheryteId = 0;
        flagPrepared = false;
        killConfirmed = false;
        tagAttempted = false;
        activeTagActionId = 0;
        discardAtUldah = false;
        returningHome = false;
        parkingCandidates.Clear();
    }

    private bool KillNoticeMatchesWorld(string killedWorld, HuntAlertSnapshot alert)
    {
        if (!string.IsNullOrWhiteSpace(killedWorld))
            return alert.World.Equals(killedWorld, StringComparison.OrdinalIgnoreCase);

        return clientState.TerritoryType == alert.TerritoryId &&
               travel.CurrentWorld.Equals(alert.World, StringComparison.OrdinalIgnoreCase);
    }

    private void RemoveKilledQueuedAlerts(string sonarText, string killedWorld)
    {
        if (string.IsNullOrWhiteSpace(killedWorld))
            return;

        var survivors = pendingAlerts
            .Where(alert => !alert.World.Equals(killedWorld, StringComparison.OrdinalIgnoreCase) ||
                            !sonarText.Contains(alert.CreatureName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (survivors.Length == pendingAlerts.Count)
            return;
        pendingAlerts.Clear();
        foreach (var alert in survivors)
            pendingAlerts.Enqueue(alert);
    }

    private bool TravelTimedOut(DateTime now) =>
        (now - stateSinceUtc).TotalSeconds > config.TravelTimeoutSeconds;

    private Vector3 PlayerPosition() => objects.LocalPlayer?.Position ?? Vector3.Zero;

    private float ClearanceFromMark(IBattleChara target)
    {
        var playerRadius = objects.LocalPlayer?.HitboxRadius ?? 0f;
        return MathF.Max(0f, HorizontalDistance(PlayerPosition(), target.Position) - target.HitboxRadius - playerRadius);
    }

    private bool EnsureMounted(DateTime now)
    {
        if (condition[ConditionFlag.Mounted])
            return true;
        if (now < nextActionUtc)
            return false;
        UseGeneralAction(9);
        nextActionUtc = now.AddSeconds(2);
        status = "Mounting normally before vnavmesh flight";
        return false;
    }

    private unsafe void UseGeneralAction(uint id)
    {
        var manager = ActionManager.Instance();
        if (manager is not null)
            manager->UseAction(ActionType.GeneralAction, id);
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private void SetState(SentinelState next, string message)
    {
        state = next;
        stateSinceUtc = DateTime.UtcNow;
        status = message;
        log.Information("State -> {State}: {Message}", next, message);
    }

    private static string ParseSonarWorld(string text)
    {
        var start = text.LastIndexOf('<');
        var end = start >= 0 ? text.IndexOf('>', start + 1) : -1;
        return start < 0 || end <= start
            ? string.Empty
            : new string(text[(start + 1)..end].Where(char.IsLetterOrDigit).ToArray());
    }

    private static int ParseSonarInstance(string text)
    {
        for (var instance = 1; instance <= 9; instance++)
            if (text.Contains((char)(0xE0B0 + instance)))
                return instance;
        return 1;
    }

    private void DrawUi()
    {
        if (!configOpen)
            return;
        ImGui.SetNextWindowSize(new Vector2(560, 430), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("S Rank Sentinel###SRankSentinel", ref configOpen))
        {
            ImGui.End();
            return;
        }

        var enabled = config.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled))
        {
            config.Enabled = enabled;
            if (!enabled)
                vnav.StopSafe();
            config.Save();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("STANDALONE S-RANK ORCHESTRATOR");
        ImGui.TextWrapped("HuntAlerts and Sonar supply alerts only. Sentinel owns Ul'dah reset, World Visit, teleport, instance selection, safe vnavmesh movement, one gated ranged tag, and post-kill recovery.");
        ImGui.Spacing();
        ImGui.TextUnformatted($"State: {state}");
        ImGui.TextWrapped($"Status: {status}");
        if (current is not null)
            ImGui.TextWrapped($"Current: {current.CreatureName} | {current.World} | territory {current.TerritoryId} | instance {current.Instance}");
        if (pendingAlerts.Count > 0)
            ImGui.TextWrapped($"Queued: {pendingAlerts.Count} | Next: {pendingAlerts.Peek().CreatureName}");

        ImGui.Separator();
        config.FlagApproachDistance = DrawFloat("Initial flag stop", config.FlagApproachDistance, 35f, 90f);
        config.WaitingDistance = DrawFloat("Safe parking clearance", config.WaitingDistance, 35f, 70f);
        config.EmergencyDistance = DrawFloat("Emergency clearance", config.EmergencyDistance, 20f, 50f);
        config.EngageHpPercent = DrawFloat("Engage only at/below HP %", config.EngageHpPercent, 1f, 99f);
        var automaticTag = config.AutomaticTagAction;
        if (ImGui.Checkbox("Choose ranged tag from current job", ref automaticTag))
            config.AutomaticTagAction = automaticTag;
        if (!config.AutomaticTagAction)
        {
            var tagAction = (int)config.TagActionId;
            if (ImGui.InputInt("Manual ranged tag action ID", ref tagAction))
                config.TagActionId = (uint)Math.Max(0, tagAction);
        }

        if (ImGui.Button("Save settings"))
            config.Save();
        ImGui.SameLine();
        if (ImGui.Button("STOP + RESET THROUGH UL'DAH"))
        {
            pendingAlerts.Clear();
            if (current is null)
                SetState(SentinelState.ResetToUldah, "Manual reset requested");
            else
                FailCurrent("Stopped manually");
        }

        ImGui.Separator();
        ImGui.TextWrapped("Safety gates: the mark itself must already be in combat and at/below the configured HP threshold. Sentinel targets it, attempts one job-appropriate ranged action, permanently closes the attack gate for that mark, never runs a rotation, never writes coordinates, and never uses Return until the mark's death is confirmed.");
        ImGui.End();
    }

    private static float DrawFloat(string label, float value, float min, float max)
    {
        ImGui.SliderFloat(label, ref value, min, max, "%.0f y");
        return value;
    }

    private enum SentinelState
    {
        Idle,
        ResetToUldah,
        WorldVisit,
        SelectWorld,
        ConfirmWorldVisit,
        WaitForWorld,
        TeleportToTerritory,
        WaitForTerritory,
        ChangeInstance,
        SelectInstance,
        WaitForInstance,
        WaitForFlag,
        ApproachFlag,
        MoveToSafePoint,
        Landing,
        SafeWait,
        TagApproach,
        GroundRetreat,
    }
}

internal sealed class HuntTrainMessageDto
{
    public string huntType { get; set; } = string.Empty;
    public string huntWorld { get; set; } = string.Empty;
    public string creatureName { get; set; } = string.Empty;
    public uint startTerritoryTypeId { get; set; }
    public int instance { get; set; }
    public float mapLocationX { get; set; }
    public float mapLocationY { get; set; }
}
