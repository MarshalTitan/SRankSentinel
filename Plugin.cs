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
using System.Collections.Concurrent;
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
    private readonly FaloopClient faloop;
    private readonly ICallGateSubscriber<HuntTrainMessageDto, object> huntAlerts;
    private readonly Configuration config;
    private readonly ConcurrentQueue<FaloopFeedEvent> faloopEvents = new();
    private readonly Queue<HuntAlertSnapshot> pendingAlerts = new();
    private readonly Dictionary<string, DateTime> killedAlerts = new(StringComparer.Ordinal);
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
    private bool markEverIdentified;
    private bool markCombatObserved;
    private uint activeTagActionId;
    private bool discardAtUldah;
    private bool ssChainObserved;
    private bool ssSpawnAnnounced;
    private SsProfile? activeSsProfile;
    private DateTime postKillSsGraceDeadlineUtc = DateTime.MinValue;
    private DateTime ssWatchDeadlineUtc = DateTime.MinValue;
    private DateTime playerReadySinceUtc = DateTime.MinValue;
    private DateTime lastMarkSeenUtc = DateTime.MinValue;
    private Task<FaloopAuthenticationResult>? faloopLoginTask;
    private string faloopUsername = string.Empty;
    private string faloopPassword = string.Empty;
    private string faloopLoginStatus = string.Empty;
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
        faloop = new FaloopClient(pluginLog);
        faloop.EventReceived += OnFaloopEvent;
        faloopUsername = config.FaloopUsername;

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

        RestorePersistentQueue();
        if (TryDequeueNextValid(out var restored))
            StartAlert(restored, "restored persistent queue");

        if (config.Enabled && config.EnableFaloop)
            faloop.Start(config.FaloopSessionId);

        log.Information("S Rank Sentinel standalone orchestrator loaded.");
    }

    public void Dispose()
    {
        vnav.StopSafe();
        faloop.EventReceived -= OnFaloopEvent;
        faloop.Dispose();
        huntAlerts.Unsubscribe(OnHuntAlert);
        chat.ChatMessage -= OnSonarChatMessage;
        framework.Update -= OnFrameworkUpdate;
        pi.UiBuilder.Draw -= DrawUi;
        pi.UiBuilder.OpenMainUi -= OpenConfig;
        pi.UiBuilder.OpenConfigUi -= OpenConfig;
        commands.RemoveHandler(Command);
    }

    private void OpenConfig() => configOpen = true;

    private void OnFaloopEvent(FaloopFeedEvent feedEvent)
    {
        if (config.Enabled && config.EnableFaloop)
            faloopEvents.Enqueue(feedEvent);
    }

    private void BeginFaloopLogin()
    {
        if (faloopLoginTask is { IsCompleted: false })
            return;
        if (string.IsNullOrWhiteSpace(faloopUsername) || string.IsNullOrEmpty(faloopPassword))
        {
            faloopLoginStatus = "Enter the Faloop username and password first.";
            return;
        }

        config.FaloopUsername = faloopUsername.Trim();
        config.EnableFaloop = true;
        config.Save();
        faloopLoginStatus = "Authenticating with Faloop...";
        faloopLoginTask = faloop.AuthenticateAsync(config.FaloopUsername, faloopPassword);
        faloopPassword = string.Empty;
    }

    private void CompleteFaloopLoginIfReady()
    {
        if (faloopLoginTask is not { IsCompleted: true } completed)
            return;
        faloopLoginTask = null;
        try
        {
            var result = completed.GetAwaiter().GetResult();
            if (!result.Success)
            {
                faloopLoginStatus = result.Error;
                return;
            }

            config.FaloopSessionId = result.SessionId;
            config.EnableFaloop = true;
            config.Save();
            if (config.Enabled)
            {
                faloop.Start(result.SessionId);
                faloopLoginStatus = "Authenticated; connecting to the live feed.";
            }
            else
            {
                faloop.Stop("Authenticated; Sentinel is disabled");
                faloopLoginStatus = "Authenticated; enable Sentinel to connect to the live feed.";
            }
        }
        catch (Exception ex)
        {
            faloopLoginStatus = "Faloop login failed; see the plugin log.";
            log.Warning("Could not complete Faloop login: {Error}", ex.Message);
        }
    }

    private void DrainFaloopEvents()
    {
        while (faloopEvents.TryDequeue(out var feedEvent))
            HandleFaloopEvent(feedEvent);
    }

    private void HandleFaloopEvent(FaloopFeedEvent feedEvent)
    {
        var world = ResolveFaloopWorld(feedEvent.WorldSlug);
        var creature = FaloopCatalog.DisplayName(feedEvent.MobSlug);
        var hasTerritory = FaloopCatalog.TryResolveTerritory(feedEvent.ZoneSlug, out var territory);

        if (feedEvent.Action == FaloopEventAction.Death)
        {
            if (!IsWithinFreshnessWindow(feedEvent.OccurredAtUtc, DateTime.UtcNow))
                return;
            InvalidateExternalDeath(world, creature, hasTerritory ? territory : 0,
                feedEvent.Instance, feedEvent.OccurredAtUtc, "Faloop");
            return;
        }

        if (!FaloopCatalog.TryResolve(feedEvent.ZoneSlug, feedEvent.PoiId,
                out territory, out var mapX, out var mapY))
        {
            status = $"Ignored Faloop spawn for {creature}: unknown supported-zone POI " +
                     $"{feedEvent.ZoneSlug ?? "(missing)"}/{feedEvent.PoiId}";
            log.Warning("Faloop spawn had no reviewed coordinate mapping: zone {Zone}, POI {Poi}",
                feedEvent.ZoneSlug ?? "(missing)", feedEvent.PoiId);
            return;
        }

        var precursorProfile = HuntCatalog.GetSsProfileForPrecursorName(creature);
        if (precursorProfile is not null)
        {
            if (SsAlertMatchesCurrent(world, territory, feedEvent.Instance))
                ObserveSsChain(precursorProfile,
                    $"Faloop reported a {precursorProfile.PrecursorName} precursor");
            return;
        }

        var definition = HuntCatalog.ResolveStrict(territory, creature);
        if (definition is null)
            return; // Faloop reports many ranks; only the strict ShB/EW/DT S/SS catalog is eligible.

        var isSs = HuntCatalog.IsAnySsName(definition.Name);
        AcceptSRankAlert(
            isSs ? "ssrank" : "srank",
            world,
            definition.Name,
            territory,
            Math.Max(1, feedEvent.Instance),
            mapX,
            mapY,
            "Faloop",
            feedEvent.OccurredAtUtc);
    }

    private string ResolveFaloopWorld(string worldId)
    {
        if (!uint.TryParse(worldId, out var numericId))
            return FaloopCatalog.DisplayName(worldId);
        var world = data.GetExcelSheet<World>().FirstOrDefault(row => row.RowId == numericId);
        return world.RowId == 0 ? worldId : world.Name.ToString();
    }

    private void OnHuntAlert(HuntTrainMessageDto payload)
    {
        if (!config.Enabled || !config.EnableHuntAlertsFallback || payload is null)
            return;

        if (IsKillEventType(payload.huntType))
        {
            InvalidateExternalDeath(
                string.IsNullOrWhiteSpace(payload.huntWorld) ? travel.CurrentWorld : payload.huntWorld.Trim(),
                payload.creatureName,
                payload.startTerritoryTypeId,
                payload.instance,
                DateTime.UtcNow,
                "HuntAlerts");
            return;
        }

        var precursorProfile = HuntCatalog.GetSsProfileForPrecursorName(payload.creatureName);
        if (precursorProfile is not null)
        {
            if (SsAlertMatchesCurrent(payload.huntWorld, payload.startTerritoryTypeId, payload.instance))
                ObserveSsChain(precursorProfile,
                    $"HuntAlerts reported a {precursorProfile.PrecursorName} precursor");
            return;
        }

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
        if (!config.Enabled)
            return;

        try
        {
            var text = chatMessage.Message.TextValue;
            var isSonar = chatMessage.Sender.TextValue.Equals("Sonar", StringComparison.Ordinal);
            var mapLink = chatMessage.Message.Payloads.OfType<MapLinkPayload>().FirstOrDefault();

            if (isSonar && !config.EnableSonarFallback)
                return;

            if (!isSonar && IsPositiveGameKillMessage(text))
            {
                ConfirmKill($"Game hunt message confirmed {current!.CreatureName} was killed");
                return;
            }

            if ((state is SentinelState.PostKillSsGrace or SentinelState.SsWatch) && activeSsProfile is not null)
            {
                if (HuntCatalog.IsSsChainStartMessage(text))
                    ObserveSsChain(activeSsProfile,
                        $"The {activeSsProfile.ExpansionName} SS precursor chain started");
                if (HuntCatalog.IsSsChainWithdrawnMessage(text))
                {
                    ssWatchDeadlineUtc = DateTime.UtcNow;
                    status = $"The {activeSsProfile.PrecursorName} chain withdrew; SS opportunity ended";
                }
                if (HuntCatalog.IsSsSpawnMessage(text))
                {
                    ObserveSsChain(activeSsProfile,
                        $"{activeSsProfile.SsName} spawn message detected");
                    ssSpawnAnnounced = true;
                    status = $"{activeSsProfile.SsName} announced; waiting for its alert or game object location";
                }
                if (text.Contains(activeSsProfile.PrecursorName, StringComparison.OrdinalIgnoreCase))
                    ObserveSsChain(activeSsProfile,
                        $"{activeSsProfile.PrecursorName} observed; remaining stationary and not targeting it");
            }

            var directSsProfile = HuntCatalog.FindSsProfileInText(text);
            if (!text.Contains("was just killed", StringComparison.OrdinalIgnoreCase) &&
                directSsProfile is not null &&
                mapLink is not null)
            {
                AcceptSRankAlert(
                    "ssrank",
                    isSonar ? ParseSonarWorld(text) : travel.CurrentWorld,
                    directSsProfile.SsName,
                    mapLink.TerritoryType.RowId,
                    ParseSonarInstance(text),
                    mapLink.XCoord,
                    mapLink.YCoord,
                    isSonar ? "Sonar" : "game hunt message");
                return;
            }

            if (!isSonar)
                return;

            if (text.Contains("was just killed", StringComparison.OrdinalIgnoreCase))
            {
                var killedWorld = ParseSonarWorld(text);
                if (current is not null &&
                    HuntCatalog.TextMentionsMark(text, current.CreatureName) &&
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
        string source,
        DateTime? occurredAtUtc = null)
    {
        var ssProfile = HuntCatalog.GetSsProfileForSsName(creature);
        var isSs = ssProfile is not null;
        if (!isSs && !string.Equals(huntType, "srank", StringComparison.OrdinalIgnoreCase))
            return;
        if (HuntCatalog.IsAnyPrecursorName(creature))
            return;
        if (isSs && territory == 0 &&
            (state is SentinelState.PostKillSsGrace or SentinelState.SsWatch) && current is not null)
            territory = current.TerritoryId;
        if (territory == 0 || string.IsNullOrWhiteSpace(creature))
        {
            status = $"{source} alert was missing the S-rank name or territory";
            return;
        }
        if (isSs && !ssProfile!.TerritoryIds.Contains(territory))
        {
            status = $"Ignored {source} alert for {creature.Trim()}: territory does not match its expansion";
            return;
        }
        if (!HuntCatalog.IsSupportedTerritory(territory))
        {
            status = $"Ignored {source} alert for {creature.Trim()}: only Shadowbringers, Endwalker, and Dawntrail are supported";
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
            isSs ? "ssrank" : "srank", world, creature.Trim(), territory,
            definition?.DataId ?? 0, definition?.PreferredAetheryteId ?? 0,
            instance, mapX, mapY, occurredAtUtc ?? DateTime.UtcNow);
        if (!IsWithinFreshnessWindow(incoming.ReceivedAtUtc, DateTime.UtcNow))
        {
            status = $"Ignored stale {source} alert for {incoming.CreatureName} on {incoming.World}";
            return;
        }
        PruneKilledAlerts();
        if (killedAlerts.ContainsKey(incoming.Key) || current?.Key == incoming.Key ||
            pendingAlerts.Any(alert => alert.Key == incoming.Key))
            return;

        if (isSs &&
            current is not null &&
            (state is SentinelState.PostKillSsGrace or SentinelState.SsWatch ||
             (killConfirmed && clientState.TerritoryType == current.TerritoryId && !travel.IsBusy)) &&
            current.World.Equals(incoming.World, StringComparison.OrdinalIgnoreCase) &&
            current.TerritoryId == incoming.TerritoryId &&
            activeSsProfile is not null && ssProfile == activeSsProfile)
        {
            var currentInstance = travel.CurrentInstance;
            if (currentInstance > 0)
                incoming = incoming with { Instance = currentInstance };
            StartSsAlertDirect(incoming, source);
            return;
        }

        if (current is not null || state != SentinelState.Idle)
        {
            EnqueuePersistent(incoming);
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
        markEverIdentified = false;
        markCombatObserved = false;
        activeTagActionId = 0;
        discardAtUldah = false;
        ssChainObserved = false;
        ssSpawnAnnounced = false;
        activeSsProfile = null;
        postKillSsGraceDeadlineUtc = DateTime.MinValue;
        ssWatchDeadlineUtc = DateTime.MinValue;
        playerReadySinceUtc = DateTime.MinValue;
        lastMarkSeenUtc = DateTime.MinValue;
        PrepareCurrentTravel();
        SetState(SentinelState.ResetToUldah,
            $"{source}: resetting through Ul'dah before {alert.CreatureName} on {alert.World}");
    }

    private void StartSsAlertDirect(HuntAlertSnapshot alert, string source)
    {
        vnav.StopSafe();
        current = alert;
        mark = null;
        killConfirmed = false;
        tagAttempted = false;
        markEverIdentified = false;
        markCombatObserved = false;
        activeTagActionId = 0;
        discardAtUldah = false;
        ssChainObserved = true;
        ssSpawnAnnounced = true;
        activeSsProfile = HuntCatalog.GetSsProfileForSsName(alert.CreatureName) ??
                          HuntCatalog.GetSsProfileForTerritory(alert.TerritoryId);
        postKillSsGraceDeadlineUtc = DateTime.MinValue;
        ssWatchDeadlineUtc = DateTime.MinValue;
        playerReadySinceUtc = DateTime.MinValue;
        lastMarkSeenUtc = DateTime.MinValue;
        PrepareCurrentTravel();

        // Prefer the alert coordinates whenever they exist. Object resolution starts only near
        // those coordinates; the local scan fallback is reserved for an in-zone SS that was
        // discovered directly from the game object table and therefore has no map coordinates.
        var visibleSs = currentMapLink is null ? FindMark() : null;
        if (visibleSs is not null)
        {
            mark = visibleSs;
            MarkWasIdentified(visibleSs);
            SetState(SentinelState.LocateMark,
                $"{source}: {alert.CreatureName} is already visible; switching to dynamic safe parking");
            return;
        }

        if (!vnav.IsReadySafe())
        {
            BeginMeshWait($"{source}: prioritizing {alert.CreatureName} directly without an Ul'dah reset");
            return;
        }

        SetState(SentinelState.WaitForFlag,
            $"{source}: prioritizing {alert.CreatureName} directly from its alert coordinates");
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
        if (map.RowId != 0 && current.MapX > 0f && current.MapY > 0f)
            currentMapLink = new MapLinkPayload(current.TerritoryId, map.RowId, current.MapX, current.MapY);
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var now = DateTime.UtcNow;
        if ((now - lastTickUtc).TotalMilliseconds < 250)
            return;
        lastTickUtc = now;

        try
        {
            CompleteFaloopLoginIfReady();
            if (!config.Enabled)
                return;
            if (config.EnableFaloop)
                DrainFaloopEvents();
            if (current is null && state == SentinelState.Idle)
                return;
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
            IBattleChara? visibleMark = null;
            if (CanResolveMarkInState())
            {
                visibleMark = FindMark();
                mark = visibleMark;
                if (visibleMark is not null)
                {
                    MarkWasIdentified(visibleMark);
                    if (visibleMark.IsDead || visibleMark.CurrentHp == 0)
                    {
                        ConfirmKill($"Previously identified {visibleMark.Name.TextValue} is visibly dead");
                        return;
                    }
                }
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
            case SentinelState.WaitForPlayerReady:
                TickWaitForPlayerReady(now);
                return;
            case SentinelState.WaitForMesh:
                TickWaitForMesh();
                return;
            case SentinelState.WaitForFlag:
                TickWaitForFlag(now);
                return;
            case SentinelState.ApproachFlag:
                TickApproachFlag(now);
                return;
            case SentinelState.LocateMark:
                TickLocateMark(now);
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
            case SentinelState.PostKillSsGrace:
                TickPostKillSsGrace(now);
                return;
            case SentinelState.SsWatch:
                TickSsWatch(now);
                return;
        }
    }

    private void TickResetToUldah(DateTime now)
    {
        if (travel.IsBusy)
            return;

        if (travel.IsInUldah(clientState.TerritoryType))
        {
            if (killConfirmed)
            {
                var finished = current?.CreatureName ?? "alert";
                ClearCurrent();
                if (TryDequeueNextValid(out var next))
                {
                    StartAlert(next, $"{finished} cleared; next queued alert");
                    return;
                }
                SetState(SentinelState.Idle,
                    $"{finished} cleared; standing by in Ul'dah on {travel.CurrentWorld}");
                return;
            }

            if (discardAtUldah)
            {
                var abandoned = current?.CreatureName ?? "alert";
                ClearCurrent();
                if (TryDequeueNextValid(out var next))
                {
                    StartAlert(next, $"{abandoned} reset without a confirmed kill; next queued alert");
                    return;
                }
                SetState(SentinelState.Idle,
                    $"{abandoned} reset without a confirmed kill; standing by in Ul'dah on {travel.CurrentWorld}");
                return;
            }

            if (current is null)
            {
                if (TryDequeueNextValid(out var next))
                {
                    StartAlert(next, "Ul'dah reset complete; next queued alert");
                    return;
                }
                SetState(SentinelState.Idle,
                    $"Reset complete; standing by in Ul'dah on {travel.CurrentWorld}");
                return;
            }

            // This is the last gate before leaving Ul'dah. An alert can expire or be
            // reported dead while the mandatory reset/World Visit setup is underway.
            if (!IsAlertFresh(current, now))
            {
                var skipped = current.CreatureName;
                ClearCurrent();
                if (TryDequeueNextValid(out var next))
                {
                    StartAlert(next, $"Skipped stale/killed {skipped}; next queued alert");
                    return;
                }
                SetState(SentinelState.Idle,
                    $"Skipped stale/killed {skipped}; standing by in Ul'dah on {travel.CurrentWorld}");
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

    private string WorldVisitTarget() => current?.World ?? string.Empty;

    private void CompleteWorldVisit(string targetWorld)
    {
        vnav.StopSafe();
        SetState(SentinelState.TeleportToTerritory,
            $"Arrived on {targetWorld}; preparing territory teleport");
    }

    private void FailWorldVisit(string reason)
    {
        vnav.StopSafe();
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
            BeginPlayerReadyWait("Territory and instance selected");
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
            BeginPlayerReadyWait("Correct instance reached");
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
            BeginPlayerReadyWait("Correct instance reached");
            return;
        }
        if (TravelTimedOut(now))
            FailCurrent($"Arrival in instance {current.Instance} timed out");
    }

    private void BeginPlayerReadyWait(string reason)
    {
        vnav.StopSafe();
        playerReadySinceUtc = DateTime.MinValue;
        nextActionUtc = DateTime.MinValue;
        SetState(SentinelState.WaitForPlayerReady,
            $"{reason}; waiting at the aetheryte for zoning and player readiness");
    }

    private void TickWaitForPlayerReady(DateTime now)
    {
        if (current is null)
            return;

        var instance = travel.CurrentInstance;
        var correctInstance = instance == current.Instance || (current.Instance == 1 && instance == 0);
        if (travel.IsBusy || objects.LocalPlayer is null || clientState.TerritoryType != current.TerritoryId ||
            !correctInstance)
        {
            playerReadySinceUtc = DateTime.MinValue;
            status = "Waiting at the aetheryte for zoning, the player object, and the requested instance to become ready";
            return;
        }

        if (playerReadySinceUtc == DateTime.MinValue)
        {
            playerReadySinceUtc = now;
            status = "Zoning complete; allowing the local player state to settle at the aetheryte";
            return;
        }

        if ((now - playerReadySinceUtc).TotalSeconds < 2)
            return;

        BeginMeshWait("Zoning and player readiness confirmed");
    }

    private void BeginMeshWait(string reason)
    {
        vnav.StopSafe();
        nextActionUtc = DateTime.MinValue;
        SetState(SentinelState.WaitForMesh,
            $"{reason}; holding at the aetheryte until vnavmesh finishes preparing this territory");
    }

    private void TickWaitForMesh()
    {
        if (current is null)
            return;

        // Mesh downloads/generation can take minutes. This state intentionally has no timeout,
        // performs no movement or mount toggles, and keeps the active hunt reserved while newer
        // alerts continue to enter the persistent queue.
        if (!vnav.IsReadySafe())
        {
            status = $"Waiting at the aetheryte for vnavmesh mesh readiness; " +
                     $"{pendingAlerts.Count} newer hunt(s) queued without replacing {current.CreatureName}";
            return;
        }

        if (markEverIdentified && currentMapLink is null)
        {
            SetState(SentinelState.LocateMark,
                "vnavmesh mesh is fully ready; resuming entity resolution for the previously identified mark");
            return;
        }

        SetState(SentinelState.WaitForFlag,
            "vnavmesh mesh is fully ready; resolving the active hunt's stored alert coordinates");
    }

    private void TickWaitForFlag(DateTime now)
    {
        if (current is null)
            return;
        if (!vnav.IsReadySafe())
        {
            BeginMeshWait("vnavmesh mesh readiness was lost");
            return;
        }

        if (currentMapLink is null)
        {
            status = $"Waiting for usable alert coordinates for {current.CreatureName}; the hunt remains active";
            return;
        }

        if (!flagPrepared)
        {
            if (now < nextActionUtc)
                return;
            flagPrepared = gameGui.OpenMapWithMapLink(currentMapLink);
            nextActionUtc = now.AddSeconds(2);
            if (!flagPrepared)
            {
                status = $"Could not prepare {current.CreatureName}'s alert coordinates yet; holding and retrying";
                return;
            }
        }

        flagPoint = vnav.FlagToPointSafe();
        if (flagPoint is null)
        {
            if (now >= nextActionUtc)
            {
                flagPrepared = false;
                nextActionUtc = now.AddSeconds(2);
            }
            status = $"Alert coordinates for {current.CreatureName} are not projected yet; " +
                     "holding the active hunt and retrying without treating it as dead";
            return;
        }

        if (now < nextActionUtc)
            return;
        if (!EnsureMounted(now))
            return;
        if (vnav.MoveCloseToSafe(flagPoint.Value, true, config.FlagApproachDistance))
        {
            nextActionUtc = now.AddSeconds(3);
            SetState(SentinelState.ApproachFlag,
                $"Flying toward {current.CreatureName}'s reported coordinates; " +
                $"entity resolution waits until within about {config.FlagApproachDistance:0}y");
            return;
        }

        nextActionUtc = now.AddSeconds(3);
        status = $"No route to {current.CreatureName}'s alert coordinates is available yet; holding and retrying";
    }

    private void TickApproachFlag(DateTime now)
    {
        if (current is null)
            return;
        if (!vnav.IsReadySafe())
        {
            vnav.StopSafe();
            BeginMeshWait("vnavmesh readiness was lost during the coordinate approach");
            return;
        }
        if (flagPoint is null)
        {
            SetState(SentinelState.WaitForFlag,
                "Alert coordinate projection was lost; rebuilding it without abandoning the active hunt");
            return;
        }

        var distance = HorizontalDistance(PlayerPosition(), flagPoint.Value);
        if (distance <= config.FlagApproachDistance + 8f)
        {
            vnav.StopSafe();
            SetState(SentinelState.LocateMark,
                $"Reached {current.CreatureName}'s reported area; beginning positive entity resolution");
            return;
        }

        if (vnav.IsPathRunningSafe() || vnav.IsPathfindInProgressSafe())
        {
            status = $"Approaching {current.CreatureName}'s reported coordinates ({distance:0}y remaining); " +
                     "not scanning for the entity until nearby";
            return;
        }

        if (now < nextActionUtc)
            return;
        if (!EnsureMounted(now))
            return;

        if (vnav.MoveCloseToSafe(flagPoint.Value, true, config.FlagApproachDistance))
            status = $"Coordinate route stopped early; retrying while keeping {current.CreatureName} active";
        else
            status = $"Coordinate route is currently unavailable; holding position and retrying {current.CreatureName}";
        nextActionUtc = now.AddSeconds(3);
    }

    private void TickLocateMark(DateTime now)
    {
        if (current is null)
            return;

        mark = FindMark();
        if (mark is not null)
        {
            MarkWasIdentified(mark);
            if (mark.IsDead || mark.CurrentHp == 0)
            {
                ConfirmKill($"Positively identified {mark.Name.TextValue} is visibly dead");
                return;
            }

            if (now < nextActionUtc)
            {
                status = $"{mark.Name.TextValue} positively identified; waiting briefly before retrying safe parking";
                return;
            }

            vnav.StopSafe();
            BeginSafeParking(mark, true);
            return;
        }

        vnav.StopSafe();
        var elapsed = (now - stateSinceUtc).TotalSeconds;
        if (elapsed >= config.LocateTimeoutSeconds)
        {
            stateSinceUtc = now;
            status = $"{current.CreatureName} was not visible during the latest scan window; " +
                     "remaining near its alert coordinates and continuing to rescan—no kill is inferred";
            return;
        }

        var remaining = Math.Max(0, config.LocateTimeoutSeconds - elapsed);
        status = $"Near {current.CreatureName}'s alert coordinates; rescanning for the actual entity " +
                 $"({remaining:0}s in this scan window). Missing does not mean dead";
    }

    private void TickMoveToSafePoint(DateTime now)
    {
        mark = FindMark();
        if (mark is null)
        {
            vnav.StopSafe();
            safePoint = null;
            parkingCandidates.Clear();
            nextActionUtc = now.AddSeconds(1);
            SetState(SentinelState.LocateMark,
                "The previously identified mark temporarily left object range while parking; holding and rescanning");
            return;
        }
        MarkWasIdentified(mark);
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
                vnav.StopSafe();
                nextActionUtc = now.AddSeconds(3);
                SetState(SentinelState.LocateMark,
                    "All sampled parking routes were temporarily unavailable; keeping the hunt active and retrying");
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

    private void TickPostKillSsGrace(DateTime now)
    {
        if (!ValidateSsWatchContext("post-kill SS check"))
            return;

        ScanForSsEvidence(now);
        if (state != SentinelState.PostKillSsGrace)
            return;

        if (combat.IsPlayerDead)
        {
            status = combat.TryAcceptRaise()
                ? $"Accepted Raise during the {activeSsProfile!.ExpansionName} SS grace; Return remains locked"
                : $"Dead during the {activeSsProfile!.ExpansionName} SS grace; waiting for Raise and refusing Return";
            return;
        }

        if (now < postKillSsGraceDeadlineUtc)
        {
            var remaining = Math.Max(0, (postKillSsGraceDeadlineUtc - now).TotalSeconds);
            status = $"Post-kill SS check: {activeSsProfile!.ExpansionName}, {remaining:0.0}s remaining";
            return;
        }

        nextActionUtc = now;
        SetState(SentinelState.ResetToUldah,
            $"No {activeSsProfile!.ExpansionName} SS evidence within {config.PostKillSsGraceSeconds}s; " +
            "returning to Ul'dah on the current world");
    }

    private void TickSsWatch(DateTime now)
    {
        if (!ValidateSsWatchContext("SS watch"))
            return;

        ScanForSsEvidence(now);
        if (state != SentinelState.SsWatch || current is null || activeSsProfile is null)
            return;

        if (now >= ssWatchDeadlineUtc)
        {
            nextActionUtc = now;
            SetState(SentinelState.ResetToUldah,
                $"{activeSsProfile.SsName} opportunity expired; returning to Ul'dah on the current world");
            return;
        }

        if (combat.IsPlayerDead)
        {
            status = combat.TryAcceptRaise()
                ? "Accepted Raise during SS watch; Return remains locked until the opportunity ends"
                : "Dead during SS watch; waiting for Raise and refusing to use Return";
            return;
        }

        var remaining = Math.Max(0, (int)Math.Ceiling((ssWatchDeadlineUtc - now).TotalSeconds));
        status = $"{activeSsProfile.ExpansionName} SS watch: " +
                 $"{(ssSpawnAnnounced ? activeSsProfile.SsName + " announced" : activeSsProfile.PrecursorName + " chain active")}; " +
                 $"holding position without targeting precursors ({remaining}s remaining)";
    }

    private bool ValidateSsWatchContext(string phase)
    {
        if (current is null || activeSsProfile is null)
        {
            SetState(SentinelState.ResetToUldah, $"{phase} lost its completed S-rank context");
            return false;
        }

        if (!travel.CurrentWorld.Equals(current.World, StringComparison.OrdinalIgnoreCase) ||
            clientState.TerritoryType != current.TerritoryId)
        {
            SetState(SentinelState.ResetToUldah,
                $"Left the completed S-rank territory during {phase}; returning to Ul'dah on the current world");
            return false;
        }

        return true;
    }

    private void ScanForSsEvidence(DateTime now)
    {
        if (current is null || activeSsProfile is null)
            return;

        var queuedSs = pendingAlerts.FirstOrDefault(alert =>
            alert.World.Equals(current.World, StringComparison.OrdinalIgnoreCase) &&
            alert.TerritoryId == current.TerritoryId &&
            HuntCatalog.IsSsName(alert.CreatureName, activeSsProfile));
        if (queuedSs is not null)
        {
            var survivors = pendingAlerts.Where(alert => alert.Key != queuedSs.Key).ToArray();
            pendingAlerts.Clear();
            foreach (var survivor in survivors)
                pendingAlerts.Enqueue(survivor);
            PersistQueue();
            StartSsAlertDirect(queuedSs, "queued direct SS alert");
            return;
        }

        var visibleSs = FindBattleNpc(activeSsProfile.SsDataId, activeSsProfile.SsName);
        if (visibleSs is not null)
        {
            var ss = new HuntAlertSnapshot(
                "ssrank", current.World, activeSsProfile.SsName, current.TerritoryId,
                activeSsProfile.SsDataId, current.PreferredAetheryteId,
                travel.CurrentInstance > 0 ? travel.CurrentInstance : current.Instance,
                0f, 0f, now);
            StartSsAlertDirect(ss, "game object scan");
            return;
        }

        var precursorCount = objects.OfType<IBattleChara>().Count(actor =>
            actor.ObjectKind == ObjectKind.BattleNpc && !actor.IsDead &&
            HuntCatalog.IsPrecursorName(actor.Name.TextValue, activeSsProfile));
        if (precursorCount > 0)
            ObserveSsChain(activeSsProfile,
                $"{precursorCount} {activeSsProfile.PrecursorName} precursor(s) visible; ignoring them safely");
    }

    private void BeginSafeParking(IBattleChara target, bool fly)
    {
        if (!vnav.IsReadySafe())
        {
            BeginMeshWait("vnavmesh readiness was lost before dynamic safe parking");
            return;
        }
        if (fly && !EnsureMounted(DateTime.UtcNow))
            return;
        PrepareParkingCandidates(target, config.WaitingDistance);
        if (!TryStartNextParkingRoute(fly))
        {
            vnav.StopSafe();
            nextActionUtc = DateTime.UtcNow.AddSeconds(3);
            SetState(SentinelState.LocateMark,
                "No sampled safe parking route is available yet; keeping the hunt active and retrying near the alert area");
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
        return FindBattleNpc(current.MarkDataId, current.CreatureName);
    }

    private IBattleChara? FindBattleNpc(uint dataId, string name)
    {
        return objects.OfType<IBattleChara>().FirstOrDefault(actor =>
            actor.ObjectKind == ObjectKind.BattleNpc &&
            ((dataId != 0 && actor.BaseId == dataId) ||
             actor.Name.TextValue.Equals(name, StringComparison.OrdinalIgnoreCase)));
    }

    private bool SsAlertMatchesCurrent(string? world, uint territoryId, int instance)
    {
        if (state is not (SentinelState.PostKillSsGrace or SentinelState.SsWatch) || current is null ||
            (territoryId != 0 && current.TerritoryId != territoryId))
            return false;
        var alertWorld = string.IsNullOrWhiteSpace(world) ? travel.CurrentWorld : world.Trim();
        var currentInstance = travel.CurrentInstance > 0 ? travel.CurrentInstance : current.Instance;
        return current.World.Equals(alertWorld, StringComparison.OrdinalIgnoreCase) &&
               (instance <= 0 || currentInstance == Math.Max(1, instance));
    }

    private void ObserveSsChain(SsProfile profile, string reason)
    {
        if (state is not (SentinelState.PostKillSsGrace or SentinelState.SsWatch) ||
            activeSsProfile != profile)
            return;
        if (!ssChainObserved)
        {
            ssChainObserved = true;
            ssWatchDeadlineUtc = DateTime.UtcNow.AddSeconds(config.SsChainTimeoutSeconds);
        }
        if (state == SentinelState.PostKillSsGrace)
            SetState(SentinelState.SsWatch,
                $"{reason}; staying in-zone for {profile.SsName}");
        status = $"{reason}; {profile.PrecursorName} will not be targeted or approached";
    }

    private void ConfirmKill(string reason)
    {
        if (current is null || killConfirmed)
            return;
        killConfirmed = true;
        discardAtUldah = false;
        vnav.StopSafe();
        var now = DateTime.UtcNow;
        MarkKilled(current, now);
        if (HuntCatalog.IsSupportedNormalS(current.TerritoryId, current.CreatureName) &&
            clientState.TerritoryType == current.TerritoryId &&
            travel.CurrentWorld.Equals(current.World, StringComparison.OrdinalIgnoreCase))
        {
            activeSsProfile = HuntCatalog.GetSsProfileForTerritory(current.TerritoryId);
            ssChainObserved = false;
            ssSpawnAnnounced = false;
            postKillSsGraceDeadlineUtc = now.AddSeconds(config.PostKillSsGraceSeconds);
            ssWatchDeadlineUtc = DateTime.MinValue;
            nextActionUtc = DateTime.MinValue;
            SetState(SentinelState.PostKillSsGrace,
                $"{reason}; checking for {activeSsProfile!.PrecursorName}/{activeSsProfile.SsName} evidence for " +
                $"{config.PostKillSsGraceSeconds}s");
            return;
        }

        nextActionUtc = now.AddSeconds(2);
        SetState(SentinelState.ResetToUldah,
            $"{reason}; returning to Ul'dah on the current visited world");
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
        markEverIdentified = false;
        markCombatObserved = false;
        activeTagActionId = 0;
        discardAtUldah = false;
        ssChainObserved = false;
        ssSpawnAnnounced = false;
        activeSsProfile = null;
        postKillSsGraceDeadlineUtc = DateTime.MinValue;
        ssWatchDeadlineUtc = DateTime.MinValue;
        playerReadySinceUtc = DateTime.MinValue;
        lastMarkSeenUtc = DateTime.MinValue;
        parkingCandidates.Clear();
    }

    private bool CanResolveMarkInState() => state is
        SentinelState.LocateMark or
        SentinelState.MoveToSafePoint or
        SentinelState.Landing or
        SentinelState.SafeWait or
        SentinelState.TagApproach or
        SentinelState.GroundRetreat;

    private void MarkWasIdentified(IBattleChara target)
    {
        mark = target;
        markEverIdentified = true;
        lastMarkSeenUtc = DateTime.UtcNow;
        if (CombatController.IsMarkInCombat(target))
            markCombatObserved = true;
    }

    private static bool IsKillEventType(string? huntType)
    {
        if (string.IsNullOrWhiteSpace(huntType))
            return false;
        var normalized = new string(huntType.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return normalized is "kill" or "killed" or "death" or "dead" or
            "srankkill" or "srankkilled" or "srankdeath" or
            "ssrankkill" or "ssrankkilled" or "ssrankdeath";
    }

    private bool IsPositiveGameKillMessage(string text)
    {
        if (current is null || killConfirmed || string.IsNullOrWhiteSpace(text) ||
            clientState.TerritoryType != current.TerritoryId ||
            !travel.CurrentWorld.Equals(current.World, StringComparison.OrdinalIgnoreCase))
            return false;

        var mentionsMark = HuntCatalog.TextMentionsMark(text, current.CreatureName);
        var hasKillWording = text.Contains("was defeated", StringComparison.OrdinalIgnoreCase) ||
                             text.Contains("has been defeated", StringComparison.OrdinalIgnoreCase) ||
                             text.Contains("was slain", StringComparison.OrdinalIgnoreCase) ||
                             text.Contains("has been slain", StringComparison.OrdinalIgnoreCase) ||
                             text.Contains("was vanquished", StringComparison.OrdinalIgnoreCase) ||
                             text.Contains("has been vanquished", StringComparison.OrdinalIgnoreCase) ||
                             text.Contains("was just killed", StringComparison.OrdinalIgnoreCase) ||
                             text.Contains("you defeat", StringComparison.OrdinalIgnoreCase);
        if (mentionsMark && hasKillWording)
            return true;

        // Hunt reward lines do not always name the mark. Accept them only after this exact mark
        // was positively resolved and observed in combat very recently. This prevents an unrelated
        // reward message received while merely traveling from clearing the active hunt.
        if (!markEverIdentified || !markCombatObserved ||
            DateTime.UtcNow - lastMarkSeenUtc > TimeSpan.FromSeconds(15))
            return false;

        var isHuntReward = (text.Contains("Sack of Nuts", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("Sacks of Nuts", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("Allied Seal", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("Centurio Seal", StringComparison.OrdinalIgnoreCase)) &&
                           (text.Contains("obtain", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("receive", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("earn", StringComparison.OrdinalIgnoreCase));
        return isHuntReward;
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
        var removed = pendingAlerts.Where(alert =>
            HuntCatalog.TextMentionsMark(sonarText, alert.CreatureName) &&
            (string.IsNullOrWhiteSpace(killedWorld)
                ? alert.World.Equals(travel.CurrentWorld, StringComparison.OrdinalIgnoreCase)
                : alert.World.Equals(killedWorld, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (removed.Length == 0)
            return;

        var removedKeys = removed.Select(alert => alert.Key).ToHashSet(StringComparer.Ordinal);
        var survivors = pendingAlerts.Where(alert => !removedKeys.Contains(alert.Key)).ToArray();
        pendingAlerts.Clear();
        foreach (var alert in survivors)
            pendingAlerts.Enqueue(alert);
        var now = DateTime.UtcNow;
        foreach (var alert in removed)
            killedAlerts[alert.Key] = now;
        PersistQueue();
        status = $"Removed {removed.Length} queued hunt(s) already reported killed";
    }

    private void InvalidateExternalDeath(
        string world,
        string creature,
        uint territory,
        int instance,
        DateTime deathAtUtc,
        string source)
    {
        bool Matches(HuntAlertSnapshot alert) =>
            alert.World.Equals(world, StringComparison.OrdinalIgnoreCase) &&
            HuntCatalog.NamesMatch(alert.CreatureName, creature) &&
            (territory == 0 || alert.TerritoryId == territory) &&
            (instance <= 0 || alert.Instance == instance) &&
            deathAtUtc >= alert.ReceivedAtUtc.AddMinutes(-1);

        var currentMatched = current is not null && Matches(current);
        var removed = pendingAlerts.Where(Matches).ToArray();
        if (removed.Length > 0)
        {
            var removedKeys = removed.Select(alert => alert.Key).ToHashSet(StringComparer.Ordinal);
            var survivors = pendingAlerts.Where(alert => !removedKeys.Contains(alert.Key)).ToArray();
            pendingAlerts.Clear();
            foreach (var alert in survivors)
                pendingAlerts.Enqueue(alert);
            var now = DateTime.UtcNow;
            foreach (var alert in removed)
                killedAlerts[alert.Key] = now;
            PersistQueue();
        }

        if (currentMatched)
        {
            ConfirmKill($"{source} confirmed {current!.CreatureName} was killed");
            return;
        }

        if (removed.Length > 0)
            status = $"{source} removed {removed.Length} killed queued hunt(s)";
    }

    private void RestorePersistentQueue()
    {
        var now = DateTime.UtcNow;
        foreach (var killed in config.KilledAlerts ?? [])
        {
            if (!string.IsNullOrWhiteSpace(killed.Key) && IsWithinFreshnessWindow(killed.KilledAtUtc, now))
                killedAlerts[killed.Key] = killed.KilledAtUtc;
        }

        foreach (var persisted in config.PendingAlerts ?? [])
        {
            var alert = persisted.ToSnapshot();
            if (IsAlertFresh(alert, now) && pendingAlerts.All(existing => existing.Key != alert.Key))
                pendingAlerts.Enqueue(alert);
        }

        PersistQueue();
    }

    private void EnqueuePersistent(HuntAlertSnapshot alert)
    {
        pendingAlerts.Enqueue(alert);
        PersistQueue();
    }

    private bool TryDequeueNextValid(out HuntAlertSnapshot alert)
    {
        var now = DateTime.UtcNow;
        var skipped = 0;
        while (pendingAlerts.TryDequeue(out var candidate))
        {
            if (!IsAlertFresh(candidate, now))
            {
                skipped++;
                continue;
            }

            alert = candidate;
            PersistQueue();
            if (skipped > 0)
                log.Information("Skipped {Count} killed, stale, or no-longer-eligible queued alerts", skipped);
            return true;
        }

        alert = null!;
        PersistQueue();
        if (skipped > 0)
            status = $"Skipped {skipped} killed, stale, or no-longer-eligible queued alert(s)";
        return false;
    }

    private bool IsAlertFresh(HuntAlertSnapshot alert, DateTime now) =>
        IsWithinFreshnessWindow(alert.ReceivedAtUtc, now) &&
        !killedAlerts.ContainsKey(alert.Key) &&
        travel.IsSameDataCenter(alert.World) &&
        HuntCatalog.IsSupportedTerritory(alert.TerritoryId) &&
        HuntCatalog.Resolve(alert.TerritoryId, alert.CreatureName) is not null;

    private bool IsWithinFreshnessWindow(DateTime timestamp, DateTime now)
    {
        var age = now - timestamp;
        return age.TotalMinutes >= -5 && age.TotalMinutes <= Math.Max(10, config.AlertFreshnessMinutes);
    }

    private void MarkKilled(HuntAlertSnapshot alert, DateTime killedAtUtc)
    {
        killedAlerts[alert.Key] = killedAtUtc;
        PersistQueue();
    }

    private void PruneKilledAlerts()
    {
        var now = DateTime.UtcNow;
        var expired = killedAlerts
            .Where(pair => !IsWithinFreshnessWindow(pair.Value, now))
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var key in expired)
            killedAlerts.Remove(key);
    }

    private void PersistQueue()
    {
        PruneKilledAlerts();
        config.PendingAlerts = pendingAlerts.Select(PersistedHuntAlert.From).ToList();
        config.KilledAlerts = killedAlerts
            .Select(pair => new KilledHuntRecord { Key = pair.Key, KilledAtUtc = pair.Value })
            .ToList();
        config.Save();
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
        ImGui.SetNextWindowSize(new Vector2(600, 610), ImGuiCond.FirstUseEver);
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
            {
                vnav.StopSafe();
                faloop.Stop("Sentinel disabled");
            }
            else if (config.EnableFaloop)
            {
                faloop.Start(config.FaloopSessionId);
            }
            config.Save();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("STANDALONE S-RANK ORCHESTRATOR");
        ImGui.TextWrapped("Faloop, HuntAlerts, and Sonar supply alerts only. Sentinel owns Ul'dah reset, World Visit, teleport, instance selection, safe vnavmesh movement, one gated ranged tag, ShB/EW/DT SS watch, and post-kill recovery.");
        ImGui.Spacing();
        ImGui.TextUnformatted($"State: {state}");
        ImGui.TextWrapped($"Status: {status}");
        if (current is not null)
            ImGui.TextWrapped($"Current: {current.CreatureName} | {current.World} | territory {current.TerritoryId} | instance {current.Instance}");
        if (pendingAlerts.Count > 0)
            ImGui.TextWrapped($"Queued: {pendingAlerts.Count} | Next: {pendingAlerts.Peek().CreatureName}");

        ImGui.Separator();
        ImGui.TextUnformatted("ALERT SOURCES");
        var enableFaloop = config.EnableFaloop;
        if (ImGui.Checkbox("Direct Faloop feed (experimental primary)", ref enableFaloop))
        {
            config.EnableFaloop = enableFaloop;
            if (enableFaloop && config.Enabled)
                faloop.Start(config.FaloopSessionId);
            else
                faloop.Stop("Direct Faloop feed disabled");
            config.Save();
        }
        ImGui.TextWrapped($"Faloop: {faloop.Status}");
        if (faloop.LastEventUtc != DateTime.MinValue)
            ImGui.TextWrapped($"Last feed event: {(DateTime.UtcNow - faloop.LastEventUtc).TotalSeconds:0}s ago");
        ImGui.SetNextItemWidth(250f);
        ImGui.InputText("Faloop username", ref faloopUsername, 128);
        ImGui.SetNextItemWidth(250f);
        ImGui.InputText("Faloop password (never saved)", ref faloopPassword, 256,
            ImGuiInputTextFlags.Password);
        if (ImGui.Button(faloopLoginTask is { IsCompleted: false } ? "Authenticating..." : "Authenticate / refresh session") &&
            faloopLoginTask is not { IsCompleted: false })
            BeginFaloopLogin();
        if (!string.IsNullOrWhiteSpace(faloopLoginStatus))
            ImGui.TextWrapped(faloopLoginStatus);
        if (!string.IsNullOrWhiteSpace(config.FaloopSessionId))
        {
            ImGui.SameLine();
            if (ImGui.Button("Forget saved session"))
            {
                config.FaloopSessionId = string.Empty;
                config.Save();
                faloop.Stop("Saved Faloop session removed");
                faloopLoginStatus = "Saved Faloop session removed; authenticate again to reconnect.";
            }
        }
        ImGui.TextWrapped("Only the resulting Faloop session is saved for reconnects; the account password is never stored or logged. ShB/EW/DT are enforced locally regardless of website filters.");

        var huntAlertsFallback = config.EnableHuntAlertsFallback;
        if (ImGui.Checkbox("HuntAlerts fallback", ref huntAlertsFallback))
        {
            config.EnableHuntAlertsFallback = huntAlertsFallback;
            config.Save();
        }
        var sonarFallback = config.EnableSonarFallback;
        if (ImGui.Checkbox("Sonar fallback", ref sonarFallback))
        {
            config.EnableSonarFallback = sonarFallback;
            config.Save();
        }

        ImGui.Separator();
        config.FlagApproachDistance = DrawFloat("Initial flag stop", config.FlagApproachDistance, 35f, 90f);
        config.WaitingDistance = DrawFloat("Safe parking clearance", config.WaitingDistance, 35f, 70f);
        config.EmergencyDistance = DrawFloat("Emergency clearance", config.EmergencyDistance, 20f, 50f);
        config.EngageHpPercent = DrawFloat("Engage only at/below HP %", config.EngageHpPercent, 1f, 99f);
        ImGui.TextWrapped($"ShB/EW/DT SS watch: {config.PostKillSsGraceSeconds}s post-kill evidence check, " +
                          $"{config.SsChainTimeoutSeconds}s after a precursor is detected.");
        var freshnessMinutes = config.AlertFreshnessMinutes;
        if (ImGui.InputInt("Queued-alert freshness (minutes)", ref freshnessMinutes))
            config.AlertFreshnessMinutes = Math.Clamp(freshnessMinutes, 10, 180);
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
            PersistQueue();
            if (current is null)
                SetState(SentinelState.ResetToUldah, "Manual reset requested");
            else
                FailCurrent("Stopped manually");
        }

        ImGui.Separator();
        ImGui.TextWrapped("Safety gates: the active S/SS mark itself must already be in combat and at/below the configured HP threshold. Sentinel targets it, attempts one job-appropriate ranged action, permanently closes the attack gate for that mark, and never runs a rotation. A missing entity or failed route never means cleared; only a positive death event/message or a visibly dead identified mark can complete the hunt. Forgiven Gossip, Ker Shroud, and Crystal Incarnation precursors are observation-only and are never targeted, approached, or attacked.");
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
        WaitForPlayerReady,
        WaitForMesh,
        WaitForFlag,
        ApproachFlag,
        LocateMark,
        MoveToSafePoint,
        Landing,
        SafeWait,
        TagApproach,
        GroundRetreat,
        PostKillSsGrace,
        SsWatch,
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
