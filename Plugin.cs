using Dalamud.Bindings.ImGui;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
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
    private const double ReturnDialogTimeoutSeconds = 6;
    private const double ReturnConfirmationRetrySeconds = 2;
    private const double ReturnActionRetrySeconds = 10;
    private const double ReturnTransitionTimeoutSeconds = 45;
    private const double ReturnRecoveryWatchdogSeconds = 120;
    private const float CrowdSearchRadius = 90f;
    private const float CrowdClusterLinkDistance = 14f;
    private const float CrowdRevalidationRadius = 18f;
    private const float CrowdMovementTolerance = 10f;
    private const int CrowdMinimumPlayers = 3;
    private const double CrowdPathQueryTimeoutSeconds = 20;
    private const double FaloopLocationEnrichmentTimeoutSeconds = 300;
    private const double FaloopLocationEnrichmentInitialRetrySeconds = 5;
    private const double FaloopLocationEnrichmentMaximumRetrySeconds = 30;
    private const float PullResetMinimumHpPercent = 99f;
    private const double PullResetConfirmationSeconds = 4;
    private const double IncidentalAggroClearConfirmationSeconds = 2;
    private const double IncidentalAggroRouteRetrySeconds = 6;
    private const float IncidentalAggroThreatRadius = 60f;
    private const float IncidentalAggroEscapeDistance = 55f;
    private const double SsStagingPathQueryTimeoutSeconds = 20;
    private const double SsStagingRouteRetrySeconds = 3;
    private const float SsStagingArrivalDistance = 5f;
    private static readonly IReadOnlyDictionary<uint, TerritoryAetheryteOverride> TerritoryAetheryteOverrides =
        new Dictionary<uint, TerritoryAetheryteOverride>
        {
            // Macarenses Angle is unsuitable for S-rank travel. This hard route override is
            // intentionally centralized so future territory-specific exceptions stay declarative.
            [818] = new(147, "The Tempest", "The Ondo Cups"),
        };
    private readonly IDalamudPluginInterface pi;
    private readonly ICommandManager commands;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IChatGui chat;
    private readonly IObjectTable objects;
    private readonly IDataManager data;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly VNavmeshIpc vnav;
    private readonly NativeTravel travel;
    private readonly CombatController combat;
    private readonly FaloopClient faloop;
    private readonly ICallGateSubscriber<HuntTrainMessageDto, object> huntAlerts;
    private readonly Configuration config;
    private readonly ConcurrentQueue<FaloopFeedEvent> faloopEvents = new();
    private readonly ConcurrentQueue<byte> faloopSessionRejections = new();
    private readonly Queue<HuntAlertSnapshot> pendingAlerts = new();
    private readonly Dictionary<string, PendingFaloopLocation> unresolvedFaloopAlerts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> killedAlerts = new(StringComparer.Ordinal);
    private readonly Queue<ParkingCandidate> parkingCandidates = new();
    private readonly Queue<SsStagingCandidate> ssStagingCandidates = new();
    private readonly Queue<Vector3> approachProjectionCandidates = new();

    private bool configOpen;
    private HuntAlertSnapshot? current;
    private IBattleChara? mark;
    private Vector3? alertPoint;
    private Vector3? approachPoint;
    private Vector3? safePoint;
    private ParkingCandidate? selectedParkingCandidate;
    private Task<List<Vector3>>? parkingPathTask;
    private DateTime parkingPathStartedUtc = DateTime.MinValue;
    private List<Vector3>? selectedParkingPath;
    private bool crowdFallbackAnnounced;
    private bool postTagRetreatActive;
    private bool parkingPathUsesFlight;
    private uint territoryAetheryteId;
    private SentinelState state = SentinelState.Idle;
    private DateTime stateSinceUtc = DateTime.UtcNow;
    private DateTime lastTickUtc = DateTime.MinValue;
    private DateTime nextActionUtc = DateTime.MinValue;
    private bool killConfirmed;
    private bool tagAttempted;
    private bool markEverIdentified;
    private bool markCombatObserved;
    private bool pullCycleCombatObserved;
    private bool pullCycleTagged;
    private ulong identifiedMarkGameObjectId;
    private int pullCycle = 1;
    private DateTime pullResetCandidateSinceUtc = DateTime.MinValue;
    private uint activeTagActionId;
    private bool discardAtUldah;
    private string discardReason = string.Empty;
    private bool ssChainObserved;
    private bool ssSpawnAnnounced;
    private SsProfile? activeSsProfile;
    private SsStagingLocation? activeSsStagingLocation;
    private Vector3? ssStagingAnchor;
    private Vector3? ssStagingDestination;
    private SsStagingCandidate? selectedSsStagingCandidate;
    private Task<List<Vector3>>? ssStagingPathTask;
    private List<Vector3>? selectedSsStagingPath;
    private DateTime ssStagingPathStartedUtc = DateTime.MinValue;
    private DateTime nextSsStagingAttemptUtc = DateTime.MinValue;
    private bool ssStagingArrived;
    private bool ssStagingProjectionFailureLogged;
    private DateTime postKillSsGraceDeadlineUtc = DateTime.MinValue;
    private DateTime ssWatchDeadlineUtc = DateTime.MinValue;
    private DateTime playerReadySinceUtc = DateTime.MinValue;
    private DateTime lastMarkSeenUtc = DateTime.MinValue;
    private DateTime returnRecoveryStartedUtc = DateTime.MinValue;
    private DateTime returnActionIssuedUtc = DateTime.MinValue;
    private DateTime returnConfirmedUtc = DateTime.MinValue;
    private DateTime nextReturnConfirmationAttemptUtc = DateTime.MinValue;
    private string returnExpectedWorld = string.Empty;
    private bool returnInitiatedBySentinel;
    private bool returnConfirmationObserved;
    private bool returnConfirmed;
    private bool returnWatchdogWarning;
    private int returnActionAttempts;
    private int returnConfirmationAttempts;
    private SentinelState incidentalAggroResumeState = SentinelState.Idle;
    private DateTime incidentalAggroStartedUtc = DateTime.MinValue;
    private DateTime incidentalAggroClearSinceUtc = DateTime.MinValue;
    private DateTime incidentalAggroLastProgressUtc = DateTime.MinValue;
    private Vector3 incidentalAggroLastPosition;
    private int incidentalAggroEscapeAttempt;
    private Task<FaloopAuthenticationResult>? faloopLoginTask;
    private bool faloopLoginWasAutomatic;
    private bool automaticFaloopReauthenticationAttempted;
    private int faloopCredentialGeneration;
    private int faloopLoginStartedGeneration;
    private string pendingProtectedFaloopPassword = string.Empty;
    private string faloopUsername = string.Empty;
    private string faloopPassword = string.Empty;
    private string faloopLoginStatus = string.Empty;
    private string lastFaloopDecision = "No recognized Faloop hunt event processed yet";
    private DateTime lastFaloopDecisionUtc = DateTime.MinValue;
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
        this.framework = framework;
        log = pluginLog;

        config = pi.GetPluginConfig() as Configuration ?? new Configuration();
        config.Initialize(pi);
        vnav = new VNavmeshIpc(pi);
        travel = new NativeTravel(gameGui, objectTable, targetManager, condition, dataManager);
        combat = new CombatController(gameGui, condition, objectTable, targetManager);
        faloop = new FaloopClient(pluginLog);
        faloop.EventReceived += OnFaloopEvent;
        faloop.SessionRejected += OnFaloopSessionRejected;
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

        var coverageAudit = FaloopCatalog.Audit(HuntCatalog.SupportedTerritoryIds);
        if (coverageAudit.Issues.Count == 0)
            log.Information("Faloop coordinate coverage audit passed: {Territories} supported territories, {Pois} POIs",
                coverageAudit.TerritoryCount, coverageAudit.PoiCount);
        else
            foreach (var issue in coverageAudit.Issues)
                log.Error("Faloop coordinate coverage audit: {Issue}", issue);

        var ssStagingAudit = HuntCatalog.AuditSsStagingLocations();
        if (ssStagingAudit.Issues.Count == 0)
            log.Information(
                "Fixed SS staging coverage audit passed: {Configured}/{Expected} ShB/EW/DT territories",
                ssStagingAudit.ConfiguredCount, ssStagingAudit.ExpectedCount);
        else
            foreach (var issue in ssStagingAudit.Issues)
                log.Error("Fixed SS staging coverage audit: {Issue}", issue);

        RestorePersistentQueue();
        if (TryDequeueNextValid(out var restored))
            StartAlert(restored, "restored persistent queue");

        StartFaloopWithSavedAuthentication();

        log.Information("S Rank Sentinel standalone orchestrator loaded.");
    }

    public void Dispose()
    {
        vnav.StopSafe();
        faloop.EventReceived -= OnFaloopEvent;
        faloop.SessionRejected -= OnFaloopSessionRejected;
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

    private void OnFaloopSessionRejected() => faloopSessionRejections.Enqueue(0);

    private void StartFaloopWithSavedAuthentication()
    {
        if (!config.Enabled || !config.EnableFaloop)
            return;
        if (!string.IsNullOrWhiteSpace(config.FaloopSessionId))
        {
            faloop.Start(config.FaloopSessionId);
            return;
        }
        if (config.RememberFaloopLogin && !string.IsNullOrWhiteSpace(config.FaloopProtectedPassword))
        {
            BeginRememberedFaloopLogin("No saved Faloop session remains");
            return;
        }
        faloop.Start(string.Empty);
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

        var protectedPassword = string.Empty;
        if (config.RememberFaloopLogin &&
            !FaloopCredentialProtection.TryProtect(
                faloopPassword, out protectedPassword, out var protectionError))
        {
            faloopLoginStatus = protectionError;
            return;
        }

        config.FaloopUsername = faloopUsername.Trim();
        config.EnableFaloop = true;
        config.Save();
        faloopLoginStatus = "Authenticating with Faloop...";
        faloopLoginWasAutomatic = false;
        automaticFaloopReauthenticationAttempted = false;
        pendingProtectedFaloopPassword = protectedPassword;
        faloopLoginStartedGeneration = faloopCredentialGeneration;
        faloopLoginTask = faloop.AuthenticateAsync(config.FaloopUsername, faloopPassword);
        faloopPassword = string.Empty;
    }

    private void BeginRememberedFaloopLogin(string reason)
    {
        if (faloopLoginTask is { IsCompleted: false } || automaticFaloopReauthenticationAttempted)
            return;
        automaticFaloopReauthenticationAttempted = true;

        if (!config.RememberFaloopLogin || string.IsNullOrWhiteSpace(config.FaloopUsername) ||
            string.IsNullOrWhiteSpace(config.FaloopProtectedPassword))
        {
            faloop.Stop("Faloop login required; enter credentials in S Rank Sentinel");
            faloopLoginStatus = "The Faloop session expired. Re-enter the username and password.";
            return;
        }
        if (!FaloopCredentialProtection.TryUnprotect(
                config.FaloopProtectedPassword, out var password, out var unlockError))
        {
            faloop.Stop("Remembered Faloop login could not be unlocked");
            faloopLoginStatus = unlockError;
            return;
        }

        faloopLoginWasAutomatic = true;
        pendingProtectedFaloopPassword = string.Empty;
        faloopLoginStatus = $"{reason}; securely re-authenticating once...";
        faloopLoginStartedGeneration = faloopCredentialGeneration;
        faloopLoginTask = faloop.AuthenticateAsync(config.FaloopUsername, password);
        password = string.Empty;
    }

    private void DrainFaloopSessionRejections()
    {
        var rejected = false;
        while (faloopSessionRejections.TryDequeue(out _))
            rejected = true;
        if (!rejected)
            return;

        config.FaloopSessionId = string.Empty;
        config.Save();
        if (config.RememberFaloopLogin && !string.IsNullOrWhiteSpace(config.FaloopProtectedPassword))
        {
            BeginRememberedFaloopLogin("The saved Faloop session expired");
            return;
        }

        faloop.Stop("Faloop session expired; login required");
        faloopLoginStatus = "The saved Faloop session expired. Re-enter the Faloop credentials.";
    }

    private void CompleteFaloopLoginIfReady()
    {
        if (faloopLoginTask is not { IsCompleted: true } completed)
            return;
        faloopLoginTask = null;
        if (faloopLoginStartedGeneration != faloopCredentialGeneration)
        {
            pendingProtectedFaloopPassword = string.Empty;
            faloopLoginWasAutomatic = false;
            return;
        }
        try
        {
            var result = completed.GetAwaiter().GetResult();
            if (!result.Success)
            {
                pendingProtectedFaloopPassword = string.Empty;
                if (faloopLoginWasAutomatic)
                {
                    faloop.Stop("Automatic Faloop login failed; re-enter credentials");
                    config.FaloopSessionId = string.Empty;
                    config.Save();
                    faloopLoginStatus = $"{result.Error} Automatic login stopped; re-enter the Faloop credentials.";
                }
                else
                {
                    faloopLoginStatus = result.Error;
                }
                faloopLoginWasAutomatic = false;
                return;
            }

            config.FaloopSessionId = result.SessionId;
            config.EnableFaloop = true;
            var wasAutomatic = faloopLoginWasAutomatic;
            if (!wasAutomatic)
            {
                config.FaloopProtectedPassword = config.RememberFaloopLogin
                    ? pendingProtectedFaloopPassword
                    : string.Empty;
            }
            config.Save();
            faloopLoginWasAutomatic = false;
            // An automatic refresh is not considered healthy until the feed accepts its new
            // session. This prevents a login-success/feed-rejection cycle from retrying forever.
            if (!wasAutomatic)
                automaticFaloopReauthenticationAttempted = false;
            pendingProtectedFaloopPassword = string.Empty;
            if (config.Enabled && config.EnableFaloop)
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
            pendingProtectedFaloopPassword = string.Empty;
            if (faloopLoginWasAutomatic)
            {
                faloop.Stop("Automatic Faloop login failed; re-enter credentials");
                faloopLoginStatus = "Automatic Faloop login failed and was stopped; re-enter the credentials.";
            }
            else
            {
                faloopLoginStatus = "Faloop login failed; see the plugin log.";
            }
            faloopLoginWasAutomatic = false;
            log.Warning("Could not complete Faloop login: {Error}", ex.Message);
        }
    }

    private void ForgetFaloopLogin()
    {
        config.FaloopSessionId = string.Empty;
        config.FaloopUsername = string.Empty;
        config.RememberFaloopLogin = false;
        config.FaloopProtectedPassword = string.Empty;
        config.Save();
        faloopUsername = string.Empty;
        faloopPassword = string.Empty;
        pendingProtectedFaloopPassword = string.Empty;
        faloopLoginWasAutomatic = false;
        automaticFaloopReauthenticationAttempted = false;
        faloopCredentialGeneration++;
        faloop.Stop("Saved Faloop session and remembered login removed");
        faloopLoginStatus = "Saved Faloop session, username, and remembered login were removed.";
    }

    private void DrainFaloopEvents()
    {
        while (faloopEvents.TryDequeue(out var feedEvent))
            HandleFaloopEvent(feedEvent);
    }

    private void HandleFaloopEvent(FaloopFeedEvent feedEvent)
    {
        PruneUnresolvedFaloopAlerts();
        var world = ResolveFaloopWorld(feedEvent.WorldSlug);
        var creature = FaloopCatalog.DisplayName(feedEvent.MobSlug);
        var hasTerritory = FaloopCatalog.TryResolveTerritory(feedEvent.ZoneSlug, out var territory);
        SetFaloopDecision(
            $"Recognized {feedEvent.Action}: {creature} / {world} / " +
            $"{(feedEvent.ZoneSlug ?? "territory missing")} / {feedEvent.EventType}/" +
            $"{(string.IsNullOrWhiteSpace(feedEvent.EventSubType) ? "subtype missing" : feedEvent.EventSubType)}");

        if (feedEvent.Action == FaloopEventAction.Death)
        {
            if (!IsWithinFreshnessWindow(feedEvent.OccurredAtUtc, DateTime.UtcNow))
            {
                SetFaloopDecision($"Rejected stale death event: {creature} on {world}");
                return;
            }
            SetFaloopDecision($"Accepted death evidence: {creature} on {world}");
            InvalidateExternalDeath(world, creature, hasTerritory ? territory : 0,
                feedEvent.Instance, feedEvent.OccurredAtUtc, "Faloop");
            return;
        }

        var precursorProfile = HuntCatalog.GetSsProfileForPrecursorName(creature);
        if (precursorProfile is not null)
        {
            if (hasTerritory && SsAlertMatchesCurrent(world, territory, feedEvent.Instance))
            {
                SetFaloopDecision($"Accepted SS precursor evidence: {creature} on {world}");
                ObserveSsChain(precursorProfile,
                    $"Faloop reported a {precursorProfile.PrecursorName} precursor");
            }
            else
            {
                SetFaloopDecision($"Ignored unrelated SS precursor: {creature} on {world}");
            }
            return;
        }

        if (!hasTerritory && HuntCatalog.ResolveUniqueName(creature) is { } uniqueDefinition)
        {
            territory = uniqueDefinition.TerritoryId;
            hasTerritory = true;
            // Lightweight sighting_set reports may omit zoneId even though the mark identity is
            // unambiguous. Preserve the inferred territory through POI resolution/enrichment.
            feedEvent = feedEvent with { ZoneSlug = territory.ToString() };
            SetFaloopDecision(
                $"Inferred territory {territory} for {uniqueDefinition.Name} from its unique supported mark identity");
        }
        if (!hasTerritory)
        {
            SetFaloopDecision($"Rejected {creature} on {world}: territory/zone was missing or unknown");
            return;
        }
        var definition = HuntCatalog.ResolveStrict(territory, creature);
        if (definition is null)
        {
            SetFaloopDecision($"Ignored {creature} on {world}: not a configured S/SS for territory {territory}");
            return; // Faloop reports many ranks; only the strict configured S/SS catalog is eligible.
        }
        var expansion = HuntCatalog.GetExpansion(territory);
        if (!config.IsExpansionEnabled(expansion))
        {
            SetFaloopDecision($"Ignored {definition.Name} on {world}: {HuntCatalog.ExpansionName(expansion)} hunting is disabled");
            return;
        }
        if (!travel.IsSameDataCenter(world))
        {
            SetFaloopDecision($"Ignored {definition.Name} on {world}: world is outside the current data center");
            return;
        }
        if (!IsWithinFreshnessWindow(feedEvent.OccurredAtUtc, DateTime.UtcNow))
        {
            SetFaloopDecision($"Rejected stale spawn event: {definition.Name} on {world}");
            return;
        }

        var reportId = string.IsNullOrWhiteSpace(feedEvent.EventId) ? "(missing)" : feedEvent.EventId;
        log.Information(
            "Faloop spawn received: {Mark} / {World} / {Territory} / reportId={ReportId}; event={EventType}/{SubType}, instance={Instance}, POI={Poi}, map={MapId}, directXY={DirectX},{DirectY}, location={Location}, timestamp={Timestamp:O} ({TimestampSource})",
            definition.Name, world, feedEvent.ZoneSlug ?? territory.ToString(), reportId,
            feedEvent.EventType, string.IsNullOrWhiteSpace(feedEvent.EventSubType) ? "(missing)" : feedEvent.EventSubType,
            Math.Max(1, feedEvent.Instance), feedEvent.RawPoiKey, feedEvent.RawMapId,
            feedEvent.DirectMapX, feedEvent.DirectMapY, feedEvent.RawLocation ?? "(missing)",
            feedEvent.OccurredAtUtc, feedEvent.TimestampSource);

        if (!FaloopCatalog.TryResolveEventCoordinates(
                feedEvent.ZoneSlug, feedEvent.PoiId,
                feedEvent.DirectMapX, feedEvent.DirectMapY, feedEvent.RawLocation,
                out territory, out var mapX, out var mapY, out var coordinateSource))
        {
            var unresolved = new HuntAlertSnapshot(
                HuntCatalog.IsAnySsName(definition.Name) ? "ssrank" : "srank",
                world, definition.Name, territory, definition.DataId, definition.PreferredAetheryteId,
                Math.Max(1, feedEvent.Instance), 0, 0, feedEvent.OccurredAtUtc);
            if (current?.Key != unresolved.Key && pendingAlerts.All(alert => alert.Key != unresolved.Key))
            {
                if (unresolvedFaloopAlerts.TryGetValue(unresolved.Key, out var existing))
                {
                    existing.FeedEvent = feedEvent;
                    existing.NextAttemptUtc = DateTime.UtcNow;
                }
                else
                {
                    unresolvedFaloopAlerts[unresolved.Key] = new PendingFaloopLocation(
                        unresolved,
                        feedEvent,
                        travel.GetDataCenterSlug(world),
                        DateTime.UtcNow);
                }
            }

            var rawPoi = string.IsNullOrWhiteSpace(feedEvent.RawPoiKey)
                ? $"POI id {feedEvent.PoiId}"
                : feedEvent.RawPoiKey;
            status = $"Waiting for Faloop location enrichment: {definition.Name} on {world} ({rawPoi})";
            SetFaloopDecision($"Location missing; waiting for enrichment: {definition.Name} on {world}");
            log.Information(
                "Location missing; waiting for enrichment: {Mark} / {World} / {Territory} / reportId={ReportId}",
                definition.Name, world, feedEvent.ZoneSlug ?? territory.ToString(), reportId);
            log.Warning(
                "Faloop spawn is waiting for location enrichment: eventType={EventType}, subType={SubType}, eventId={EventId}, mark={Mark}, markId={MarkId}, world={World}, territory={Zone} ({TerritoryId}), map={MapId}, rawPoi={RawPoi}, directXY={DirectX},{DirectY}, location={Location}, coordinateData={CoordinateData}, timestampSource={TimestampSource}",
                feedEvent.EventType, string.IsNullOrWhiteSpace(feedEvent.EventSubType) ? "(missing)" : feedEvent.EventSubType,
                string.IsNullOrWhiteSpace(feedEvent.EventId) ? "(missing)" : feedEvent.EventId,
                definition.Name, feedEvent.MobSlug, world, feedEvent.ZoneSlug ?? "(missing)", territory,
                feedEvent.RawMapId, rawPoi,
                feedEvent.DirectMapX, feedEvent.DirectMapY, feedEvent.RawLocation ?? "(missing)",
                feedEvent.RawCoordinateData, feedEvent.TimestampSource);
            return;
        }

        unresolvedFaloopAlerts.Remove(new HuntAlertSnapshot(
            HuntCatalog.IsAnySsName(definition.Name) ? "ssrank" : "srank",
            world, definition.Name, territory, definition.DataId, definition.PreferredAetheryteId,
            Math.Max(1, feedEvent.Instance), mapX, mapY, feedEvent.OccurredAtUtc).Key);
        var resolutionKind = coordinateSource.StartsWith("direct", StringComparison.OrdinalIgnoreCase)
            ? "Direct coordinates received"
            : "POI mapped successfully";
        log.Information(
            "{ResolutionKind} for Faloop {Mark} on {World}: eventType={EventType}/{SubType}, eventId={EventId}, zone={Zone}, map={MapId}, rawPoi={RawPoi} -> ({MapX:0.0}, {MapY:0.0}) via {CoordinateSource}; event coordinates {EventCoordinates}",
            resolutionKind, definition.Name, world, feedEvent.EventType, feedEvent.EventSubType,
            feedEvent.EventId, feedEvent.ZoneSlug ?? "(missing)", feedEvent.RawMapId, feedEvent.RawPoiKey,
            mapX, mapY, coordinateSource, feedEvent.RawCoordinateData);
        log.Information("Resolved destination: {Mark} / {World} / X{MapX:0.0} Y{MapY:0.0}; activating travel",
            definition.Name, world, mapX, mapY);
        SetFaloopDecision(
            $"Eligible {definition.Name} on {world}; destination X{mapX:0.0} Y{mapY:0.0} resolved; activating travel");

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

    private void SetFaloopDecision(string decision)
    {
        lastFaloopDecision = decision;
        lastFaloopDecisionUtc = DateTime.UtcNow;
        log.Information("Faloop pipeline decision: {Decision}", decision);
    }

    private string ResolveFaloopWorld(string worldId)
    {
        if (!uint.TryParse(worldId, out var numericId))
            return FaloopCatalog.DisplayName(worldId);
        var world = data.GetExcelSheet<World>().FirstOrDefault(row => row.RowId == numericId);
        return world.RowId == 0 ? worldId : world.Name.ToString();
    }

    private void TickFaloopLocationEnrichment(DateTime now)
    {
        PruneUnresolvedFaloopAlerts();
        var resolvedEvents = new List<FaloopFeedEvent>();
        foreach (var (key, pending) in unresolvedFaloopAlerts.ToArray())
        {
            if (now - pending.FirstObservedUtc >= TimeSpan.FromSeconds(FaloopLocationEnrichmentTimeoutSeconds))
            {
                unresolvedFaloopAlerts.Remove(key);
                var rawPoi = pending.FeedEvent.PoiId > 0
                    ? pending.FeedEvent.PoiId.ToString()
                    : "(missing)";
                status = pending.FeedEvent.PoiId > 0
                    ? $"Rejected: unknown POI {rawPoi} for {pending.Alert.CreatureName} after enrichment timeout"
                    : $"Rejected: no usable location after timeout for {pending.Alert.CreatureName} on {pending.Alert.World}";
                log.Warning("{Status}; last enrichment result: {Detail}", status, pending.LastDetail);
                log.Warning(
                    "Location enrichment timed out after {Seconds:0}s: {Mark} / {World} / reportId={ReportId}; attempts={Attempts}, rawPoi={RawPoi}, directXY={DirectX},{DirectY}, location={Location}",
                    FaloopLocationEnrichmentTimeoutSeconds, pending.Alert.CreatureName, pending.Alert.World,
                    string.IsNullOrWhiteSpace(pending.FeedEvent.EventId) ? "(missing)" : pending.FeedEvent.EventId,
                    pending.Attempts, pending.FeedEvent.RawPoiKey, pending.FeedEvent.DirectMapX,
                    pending.FeedEvent.DirectMapY, pending.FeedEvent.RawLocation ?? "(missing)");
                continue;
            }

            if (pending.ActiveTask is { IsCompleted: true } task)
            {
                pending.ActiveTask = null;
                FaloopLocationEnrichmentResult result;
                try
                {
                    result = task.GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    result = FaloopLocationEnrichmentResult.Pending(pending.FeedEvent,
                        $"enrichment task failed: {ex.Message}");
                }

                pending.LastDetail = result.Detail;
                pending.Attempts++;
                if (result.Success)
                {
                    pending.FeedEvent = result.Event;
                    if (FaloopCatalog.TryResolveEventCoordinates(
                            result.Event.ZoneSlug, result.Event.PoiId,
                            result.Event.DirectMapX, result.Event.DirectMapY, result.Event.RawLocation,
                            out _, out var mapX, out var mapY, out var coordinateSource))
                    {
                        unresolvedFaloopAlerts.Remove(key);
                        log.Information(
                            "Matched location update to reportId={ReportId}: {Mark} on {World}; {Detail}",
                            string.IsNullOrWhiteSpace(result.Event.EventId) ? "(missing)" : result.Event.EventId,
                            pending.Alert.CreatureName, pending.Alert.World, result.Detail);
                        log.Information(
                            "Resolved destination: {Mark} / {World} / X{MapX:0.0} Y{MapY:0.0} via {Source}; activating travel",
                            pending.Alert.CreatureName, pending.Alert.World, mapX, mapY, coordinateSource);
                        resolvedEvents.Add(result.Event);
                        continue;
                    }
                    pending.LastDetail = $"state returned unknown POI {result.Event.PoiId}";
                }

                var retrySeconds = Math.Min(FaloopLocationEnrichmentMaximumRetrySeconds,
                    FaloopLocationEnrichmentInitialRetrySeconds * Math.Pow(2, Math.Min(3, pending.Attempts)));
                pending.NextAttemptUtc = now.AddSeconds(retrySeconds);
                status = $"Waiting for Faloop location enrichment: {pending.Alert.CreatureName} on {pending.Alert.World}";
            }

            if (pending.ActiveTask is null && now >= pending.NextAttemptUtc)
            {
                pending.ActiveTask = faloop.TryEnrichLocationAsync(pending.FeedEvent, pending.DataCenterSlug);
                pending.NextAttemptUtc = now.AddSeconds(FaloopLocationEnrichmentMaximumRetrySeconds);
            }
        }

        foreach (var feedEvent in resolvedEvents)
            HandleFaloopEvent(feedEvent);
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
                    ssWatchDeadlineUtc = DateTime.MaxValue;
                    log.Information(
                        "Actual SS spawn announced: {Ss}; disabling the precursor timeout while its alert/entity is resolved",
                        activeSsProfile.SsName);
                    status = $"{activeSsProfile.SsName} announced; waiting for its alert or game object location";
                }
                if (text.Contains(activeSsProfile.PrecursorName, StringComparison.OrdinalIgnoreCase))
                    ObserveSsChain(activeSsProfile,
                        $"{activeSsProfile.PrecursorName} observed; continuing fixed-location staging without targeting it");
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
        var expansion = HuntCatalog.GetExpansion(territory);
        if (expansion is SupportedExpansion.None)
        {
            status = $"Ignored {source} alert for {creature.Trim()}: its expansion is not supported";
            return;
        }
        if (!config.IsExpansionEnabled(expansion))
        {
            status = $"Ignored {source} alert for {creature.Trim()}: " +
                     $"{HuntCatalog.ExpansionName(expansion)} hunting is disabled";
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
        if (mapX > 0f && mapY > 0f && unresolvedFaloopAlerts.Remove(incoming.Key))
            log.Information("{Source} enriched unresolved Faloop alert for {Mark} on {World} with coordinates ({MapX:0.0}, {MapY:0.0})",
                source, incoming.CreatureName, incoming.World, mapX, mapY);
        if (!IsWithinFreshnessWindow(incoming.ReceivedAtUtc, DateTime.UtcNow))
        {
            status = $"Ignored stale {source} alert for {incoming.CreatureName} on {incoming.World}";
            return;
        }
        PruneKilledAlerts();
        if (killedAlerts.ContainsKey(incoming.Key) || current?.Key == incoming.Key ||
            pendingAlerts.Any(alert => alert.Key == incoming.Key))
            return;

        log.Information(
            "New {Source} alert accepted: {Mark} on {World}, territory {Territory}, instance {Instance}, destination ({MapX:0.0}, {MapY:0.0})",
            source, incoming.CreatureName, incoming.World, incoming.TerritoryId, incoming.Instance,
            incoming.MapX, incoming.MapY);

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
            log.Information("Eligible alert queued without replacing active hunt: {Mark} on {World}; {Count} pending",
                incoming.CreatureName, incoming.World, pendingAlerts.Count);
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
        postTagRetreatActive = false;
        markEverIdentified = false;
        markCombatObserved = false;
        pullCycleCombatObserved = false;
        pullCycleTagged = false;
        identifiedMarkGameObjectId = 0;
        pullCycle = 1;
        pullResetCandidateSinceUtc = DateTime.MinValue;
        activeTagActionId = 0;
        discardAtUldah = false;
        discardReason = string.Empty;
        ssChainObserved = false;
        ssSpawnAnnounced = false;
        activeSsProfile = null;
        postKillSsGraceDeadlineUtc = DateTime.MinValue;
        ssWatchDeadlineUtc = DateTime.MinValue;
        ResetSsStagingTracking();
        playerReadySinceUtc = DateTime.MinValue;
        lastMarkSeenUtc = DateTime.MinValue;
        ResetReturnRecoveryTracking();
        ResetIncidentalAggroTracking();
        PrepareCurrentTravel();
        log.Information(
            "Hunt activated: {Mark} on {World}, territory {Territory}, instance {Instance}; positive kill evidence remains required",
            alert.CreatureName, alert.World, alert.TerritoryId, alert.Instance);
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
        postTagRetreatActive = false;
        markEverIdentified = false;
        markCombatObserved = false;
        pullCycleCombatObserved = false;
        pullCycleTagged = false;
        identifiedMarkGameObjectId = 0;
        pullCycle = 1;
        pullResetCandidateSinceUtc = DateTime.MinValue;
        activeTagActionId = 0;
        discardAtUldah = false;
        discardReason = string.Empty;
        ssChainObserved = true;
        ssSpawnAnnounced = true;
        activeSsProfile = HuntCatalog.GetSsProfileForSsName(alert.CreatureName) ??
                          HuntCatalog.GetSsProfileForTerritory(alert.TerritoryId);
        postKillSsGraceDeadlineUtc = DateTime.MinValue;
        ssWatchDeadlineUtc = DateTime.MinValue;
        ResetSsStagingTracking();
        playerReadySinceUtc = DateTime.MinValue;
        lastMarkSeenUtc = DateTime.MinValue;
        ResetReturnRecoveryTracking();
        ResetIncidentalAggroTracking();
        PrepareCurrentTravel();

        // Prefer the alert coordinates whenever they exist. Object resolution starts only near
        // those coordinates; the local scan fallback is reserved for an in-zone SS that was
        // discovered directly from the game object table and therefore has no map coordinates.
        var visibleSs = alertPoint is null ? FindMark() : null;
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

        SetState(SentinelState.PrepareApproachDestination,
            $"{source}: prioritizing {alert.CreatureName} directly from its alert coordinates");
    }

    private void PrepareCurrentTravel()
    {
        territoryAetheryteId = 0;
        alertPoint = null;
        approachPoint = null;
        safePoint = null;
        selectedParkingCandidate = null;
        parkingPathTask = null;
        parkingPathStartedUtc = DateTime.MinValue;
        selectedParkingPath = null;
        crowdFallbackAnnounced = false;
        parkingCandidates.Clear();
        approachProjectionCandidates.Clear();
        nextActionUtc = DateTime.MinValue;

        if (current is null)
            return;

        var map = data.GetExcelSheet<Map>()
            .FirstOrDefault(row => row.TerritoryType.RowId == current.TerritoryId);
        if (map.RowId != 0 && current.MapX > 0f && current.MapY > 0f)
        {
            // MapLinkPayload performs Dalamud's canonical map-coordinate conversion. Its RawX/RawY
            // values are local game-world X/Z positions scaled by 1000. Preserve that destination
            // independently of the game's global map flag so direct Faloop navigation survives
            // World Visit, teleport, zoning, and instance transitions without a fallback plugin.
            var mapped = new MapLinkPayload(current.TerritoryId, map.RowId, current.MapX, current.MapY);
            alertPoint = new Vector3(mapped.RawX / 1000f, 1024f, mapped.RawY / 1000f);
            log.Information(
                "Preserved alert destination for {Mark}: map ({MapX:0.0}, {MapY:0.0}) -> local ({LocalX:0.0}, {LocalZ:0.0})",
                current.CreatureName, current.MapX, current.MapY, alertPoint.Value.X, alertPoint.Value.Z);
        }

        // Aetheryte data can contain sparse/invalid linked Level rows. Resolution is isolated
        // so one bad game-data reference can never unwind alert acceptance or discard the hunt.
        TryResolveTerritoryAetheryte();
    }

    private bool TryResolveTerritoryAetheryte()
    {
        territoryAetheryteId = 0;
        if (current is null)
            return false;

        try
        {
            ResolveTerritoryAetheryte();
            return territoryAetheryteId != 0;
        }
        catch (Exception ex)
        {
            log.Error(ex,
                "Aetheryte resolution failed for {Mark} in territory {Territory}; retaining the active hunt for retry",
                current.CreatureName, current.TerritoryId);
            territoryAetheryteId = 0;
            return false;
        }
    }

    private void ResolveTerritoryAetheryte()
    {
        if (current is null)
            return;

        territoryAetheryteId = 0;

        // The Dravanian Hinterlands has no main teleport crystal. Its normal-game route is
        // Idyllshire followed by the Prologue Gate aethernet destination.
        if (current.TerritoryId == HuntCatalog.DravanianHinterlandsTerritoryId &&
            travel.CanTeleportTo(HuntCatalog.IdyllshireAetheryteId))
        {
            territoryAetheryteId = HuntCatalog.IdyllshireAetheryteId;
            return;
        }

        var attuned = data.GetExcelSheet<Aetheryte>()
            .Where(row => row.IsAetheryte && row.Territory.RowId == current.TerritoryId)
            .Where(row => travel.CanTeleportTo(row.RowId))
            .ToArray();

        if (TerritoryAetheryteOverrides.TryGetValue(current.TerritoryId, out var territoryOverride))
        {
            if (attuned.Any(row => row.RowId == territoryOverride.AetheryteId))
                territoryAetheryteId = territoryOverride.AetheryteId;
            log.Information("Territory override: {Territory} -> {Aetheryte}",
                territoryOverride.TerritoryName, territoryOverride.AetheryteName);
            if (territoryAetheryteId == 0)
                log.Warning(
                    "Required territory override {Aetheryte} ({AetheryteId}) is not currently usable; refusing to choose another aetheryte in {Territory}",
                    territoryOverride.AetheryteName, territoryOverride.AetheryteId, territoryOverride.TerritoryName);
        }
        else if (alertPoint is not null)
        {
            territoryAetheryteId = SelectNearestUsableAetheryte(attuned, alertPoint.Value);
        }

        if (territoryAetheryteId == 0 && !TerritoryAetheryteOverrides.ContainsKey(current.TerritoryId))
            territoryAetheryteId = attuned.FirstOrDefault(row => row.RowId == current.PreferredAetheryteId).RowId;
        if (territoryAetheryteId == 0 && !TerritoryAetheryteOverrides.ContainsKey(current.TerritoryId))
            territoryAetheryteId = attuned.FirstOrDefault().RowId;
    }

    private uint SelectNearestUsableAetheryte(
        IReadOnlyCollection<Aetheryte> attuned,
        Vector3 destination)
    {
        var candidates = new List<(uint Id, string Name, float Distance)>();
        foreach (var aetheryte in attuned)
        {
            var levelReference = aetheryte.Level.FirstOrDefault(reference =>
                reference.RowId != 0 &&
                reference.IsValid &&
                reference.Value.Territory.RowId == current?.TerritoryId);
            if (levelReference.RowId == 0 || !levelReference.IsValid)
                continue;

            var level = levelReference.Value;
            var distanceYalms = HorizontalDistance(destination, new Vector3(level.X, level.Y, level.Z));
            var name = aetheryte.PlaceName.IsValid
                ? aetheryte.PlaceName.Value.Name.ToString()
                : $"Aetheryte {aetheryte.RowId}";
            candidates.Add((aetheryte.RowId, name, distanceYalms));
            log.Information("Teleport candidate: {Aetheryte} - {Distance:0}y from mark",
                name, distanceYalms);
        }

        var selected = candidates.OrderBy(candidate => candidate.Distance).FirstOrDefault();
        if (selected.Id == 0)
        {
            log.Warning("Usable aetherytes were found for territory {TerritoryId}, but none had a linked territory Level position; using the configured fallback",
                current?.TerritoryId ?? 0);
            return 0;
        }

        log.Information("Selected nearest aetheryte: {Aetheryte} ({Distance:0}y from mark)",
            selected.Name, selected.Distance);
        return selected.Id;
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
            {
                DrainFaloopSessionRejections();
                DrainFaloopEvents();
                TickFaloopLocationEnrichment(now);
                if (faloop.IsConnected)
                    automaticFaloopReauthenticationAttempted = false;
            }
            if (current is null && state == SentinelState.Idle)
                return;
            Tick(now);
        }
        catch (Exception ex)
        {
            log.Error(ex, "S Rank Sentinel state machine failed; retaining the active hunt safely.");
            vnav.StopSafe();
            if (current is null)
            {
                SetState(SentinelState.Idle, "Internal error while idle; no hunt was active");
                return;
            }

            discardAtUldah = false;
            discardReason = string.Empty;
            nextActionUtc = now.AddSeconds(3);
            SetState(SentinelState.ResetToUldah,
                $"Internal error while handling {current.CreatureName}; active hunt retained and retrying through Ul'dah");
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
                    if (ObservePullCycleReset(visibleMark, now))
                        return;
                }
                else
                    pullResetCandidateSinceUtc = DateTime.MinValue;
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

        if (state != SentinelState.AvoidIncidentalAggro && ShouldBeginIncidentalAggroAvoidance(out var threat))
        {
            BeginIncidentalAggroAvoidance(threat, now);
            return;
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
            case SentinelState.OpenHinterlandsGateway:
                TickOpenHinterlandsGateway(now);
                return;
            case SentinelState.SelectHinterlandsGateway:
                TickSelectHinterlandsGateway(now);
                return;
            case SentinelState.SelectHinterlandsDestination:
                TickSelectHinterlandsDestination(now);
                return;
            case SentinelState.WaitForHinterlands:
                TickWaitForHinterlands(now);
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
            case SentinelState.PrepareApproachDestination:
                TickPrepareApproachDestination(now);
                return;
            case SentinelState.ApproachAlertCoordinates:
                TickApproachAlertCoordinates(now);
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
            case SentinelState.AvoidIncidentalAggro:
                TickAvoidIncidentalAggro(now);
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
        UpdateReturnRecoveryWatchdog(now);

        // Return uses SelectYesno, but only touch that dialog after Sentinel itself has
        // requested Return while this state is active. This must never become a generic
        // Yes/No-dialog accepter.
        if (returnInitiatedBySentinel && travel.ReturnConfirmationIsOpen(out var returnPrompt))
        {
            if (!returnConfirmationObserved)
            {
                returnConfirmationObserved = true;
                log.Information("Return confirmation detected: {Prompt}", returnPrompt);
                status = "Return confirmation detected";
            }

            if (now < nextReturnConfirmationAttemptUtc)
                return;

            returnConfirmationAttempts++;
            if (travel.ConfirmReturnToUldah())
            {
                if (!returnConfirmed)
                    returnConfirmedUtc = now;
                returnConfirmed = true;
                nextReturnConfirmationAttemptUtc = now.AddSeconds(3);
                status = returnWatchdogWarning
                    ? $"Return recovery failure: confirmation remains open after {returnConfirmationAttempts} attempts; retrying"
                    : "Return confirmed; waiting for zone transition";
                log.Information("Return confirmed; waiting for zone transition to Ul'dah on {World}",
                    returnExpectedWorld);
            }
            else
            {
                nextReturnConfirmationAttemptUtc = now.AddSeconds(ReturnConfirmationRetrySeconds);
                status = $"Return confirmation is visible but confirmation attempt {returnConfirmationAttempts} failed; retrying";
                log.Warning("Return confirmation attempt {Attempt} failed; retrying", returnConfirmationAttempts);
            }
            return;
        }

        if (travel.IsBusy)
        {
            if (returnInitiatedBySentinel)
                status = "Return confirmed; waiting for zone transition";
            return;
        }

        if (travel.IsInUldah(clientState.TerritoryType))
        {
            var completingReturnRecovery = returnRecoveryStartedUtc != DateTime.MinValue;
            if (completingReturnRecovery &&
                !string.IsNullOrWhiteSpace(returnExpectedWorld) &&
                !travel.CurrentWorld.Equals(returnExpectedWorld, StringComparison.OrdinalIgnoreCase))
            {
                ReportReturnRecoveryFailure(
                    $"Return reached Ul'dah on {travel.CurrentWorld}, but recovery must remain on {returnExpectedWorld}");
                return;
            }

            if (completingReturnRecovery)
            {
                status = $"Arrived in Ul'dah on {travel.CurrentWorld}";
                log.Information("Arrived in Ul'dah on {World}; Return recovery is complete", travel.CurrentWorld);
                ResetReturnRecoveryTracking();
            }

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
                var reason = string.IsNullOrWhiteSpace(discardReason) ? "unspecified failure" : discardReason;
                ClearCurrent();
                if (TryDequeueNextValid(out var next))
                {
                    StartAlert(next, $"{abandoned} abandoned without a confirmed kill ({reason}); next queued alert");
                    return;
                }
                SetState(SentinelState.Idle,
                    $"{abandoned} abandoned without a confirmed kill ({reason}); standing by in Ul'dah on {travel.CurrentWorld}");
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

            log.Information("Travel state entered for {Mark}: {World} -> territory {Territory}, instance {Instance}",
                current.CreatureName, current.World, current.TerritoryId, current.Instance);
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

            TickDeadReturnRecovery(now);
            return;
        }

        if (returnInitiatedBySentinel)
        {
            // The character can become conscious just before the loading flag is observable.
            // Give the confirmed Return a bounded window to begin before falling back to the
            // normal same-world Ul'dah teleport path.
            if (returnConfirmed && (now - returnConfirmedUtc).TotalSeconds < ReturnTransitionTimeoutSeconds)
            {
                status = "Return confirmed; waiting for zone transition";
                return;
            }

            if (!returnConfirmed && (now - returnActionIssuedUtc).TotalSeconds >= ReturnDialogTimeoutSeconds)
            {
                returnInitiatedBySentinel = false;
                returnConfirmationObserved = false;
                nextActionUtc = now;
                status = "Return confirmation did not appear; retrying recovery without clearing the hunt";
                log.Warning("Return confirmation did not appear after attempt {Attempt}; retrying", returnActionAttempts);
            }
            else if (returnConfirmed)
            {
                ReportReturnRecoveryFailure(
                    "Return was confirmed but no Ul'dah transition completed; falling back to normal same-world teleport");
                returnInitiatedBySentinel = false;
                nextActionUtc = now;
            }
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

    private bool ShouldBeginIncidentalAggroAvoidance(out IBattleChara? nearestThreat)
    {
        nearestThreat = FindIncidentalAggroThreats().FirstOrDefault();
        if (nearestThreat is not null)
            return true;

        // During post-kill/reset travel, any remaining combat flag blocks Teleport. The attacker
        // may already be outside object range, so recovery must still make a non-attacking escape
        // attempt rather than silently retrying Teleport forever.
        return state == SentinelState.ResetToUldah && condition[ConditionFlag.InCombat];
    }

    private IBattleChara[] FindIncidentalAggroThreats()
    {
        if (objects.LocalPlayer is not { } player)
            return [];

        return objects.OfType<IBattleChara>()
            .Where(actor => actor.ObjectKind == ObjectKind.BattleNpc &&
                            actor.GameObjectId != identifiedMarkGameObjectId &&
                            !actor.IsDead && actor.CurrentHp > 0 && actor.IsTargetable &&
                            actor.TargetObjectId == player.GameObjectId &&
                            HorizontalDistance(actor.Position, player.Position) <= IncidentalAggroThreatRadius)
            .OrderBy(actor => HorizontalDistance(actor.Position, player.Position))
            .ToArray();
    }

    private void BeginIncidentalAggroAvoidance(IBattleChara? threat, DateTime now)
    {
        incidentalAggroResumeState = state;
        incidentalAggroStartedUtc = now;
        incidentalAggroClearSinceUtc = DateTime.MinValue;
        incidentalAggroLastProgressUtc = now;
        incidentalAggroLastPosition = PlayerPosition();
        incidentalAggroEscapeAttempt = 0;
        vnav.StopSafe();
        parkingPathTask = null;
        selectedParkingPath = null;
        safePoint = null;
        var threatName = threat?.Name.TextValue ?? "an out-of-range overworld enemy";
        log.Warning(
            "Incidental aggro detected from {Threat} while state={State}, hunt={Mark}, killConfirmed={KillConfirmed}; fleeing without attacking and preserving the hunt/queue",
            threatName, state, current?.CreatureName ?? "(none)", killConfirmed);
        SetState(SentinelState.AvoidIncidentalAggro,
            $"Incidental aggro from {threatName}; escaping without attacking while preserving the active hunt");
    }

    private void TickAvoidIncidentalAggro(DateTime now)
    {
        if (current is null)
        {
            ResetIncidentalAggroTracking();
            SetState(SentinelState.ResetToUldah, "Incidental aggro recovery retained no active hunt; resetting through Ul'dah");
            return;
        }

        if (combat.IsPlayerDead)
        {
            vnav.StopSafe();
            if (killConfirmed)
            {
                ResetIncidentalAggroTracking();
                SetState(SentinelState.ResetToUldah,
                    $"Died to incidental aggro after {current.CreatureName} was confirmed dead; starting normal Return recovery");
            }
            else
            {
                status = $"Died to incidental aggro while {current.CreatureName} remains active; waiting for Raise and refusing Return";
            }
            return;
        }

        var threats = FindIncidentalAggroThreats();
        var recoveryCombatStillBlocking = incidentalAggroResumeState == SentinelState.ResetToUldah &&
                                          condition[ConditionFlag.InCombat];
        if (threats.Length == 0 && !recoveryCombatStillBlocking)
        {
            if (incidentalAggroClearSinceUtc == DateTime.MinValue)
            {
                incidentalAggroClearSinceUtc = now;
                vnav.StopSafe();
                status = "Incidental aggro ended; confirming combat is clear before resuming";
                return;
            }

            if ((now - incidentalAggroClearSinceUtc).TotalSeconds < IncidentalAggroClearConfirmationSeconds)
                return;

            var resume = incidentalAggroResumeState;
            log.Information(
                "Incidental aggro cleared after {Seconds:0.0}s; resuming state {State} with hunt {Mark} and killConfirmed={KillConfirmed}",
                (now - incidentalAggroStartedUtc).TotalSeconds, resume, current.CreatureName, killConfirmed);
            ResetIncidentalAggroTracking();
            if (killConfirmed || resume == SentinelState.ResetToUldah)
            {
                nextActionUtc = now;
                SetState(SentinelState.ResetToUldah,
                    $"Incidental aggro cleared; resuming same-world Ul'dah recovery for {current.CreatureName}");
                return;
            }

            mark = FindMark();
            if (mark is not null)
            {
                MarkWasIdentified(mark);
                BeginSafeParking(mark, fly: condition[ConditionFlag.Mounted] || condition[ConditionFlag.InFlight]);
                return;
            }

            SetState(SentinelState.LocateMark,
                $"Incidental aggro cleared; holding the active {current.CreatureName} hunt and resuming entity scans");
            return;
        }

        incidentalAggroClearSinceUtc = DateTime.MinValue;
        var playerPosition = PlayerPosition();
        if (HorizontalDistance(playerPosition, incidentalAggroLastPosition) >= 3f)
        {
            incidentalAggroLastPosition = playerPosition;
            incidentalAggroLastProgressUtc = now;
        }

        if (vnav.IsPathRunningSafe() || vnav.IsPathfindInProgressSafe())
        {
            if ((now - incidentalAggroLastProgressUtc).TotalSeconds < IncidentalAggroRouteRetrySeconds)
            {
                status = $"Escaping incidental aggro without attacking; preserving {current.CreatureName} and {pendingAlerts.Count} queued hunt(s)";
                return;
            }

            vnav.StopSafe();
            log.Warning("Incidental-aggro escape route made no progress; sampling another route");
        }

        if (!vnav.IsReadySafe())
        {
            status = "Incidental combat blocks travel and vnavmesh is not ready; holding the hunt and retrying safely";
            return;
        }

        incidentalAggroEscapeAttempt++;
        if (!TryStartIncidentalAggroEscape(threats, incidentalAggroEscapeAttempt, out var destination, out var fly))
        {
            status = "Incidental combat blocks travel; no safe escape route resolved yet, retaining the hunt and retrying";
            incidentalAggroLastProgressUtc = now.AddSeconds(2);
            return;
        }

        incidentalAggroLastPosition = playerPosition;
        incidentalAggroLastProgressUtc = now;
        log.Information(
            "Incidental-aggro escape attempt {Attempt}: moving {Distance:0}y without attacking (flight={Flight})",
            incidentalAggroEscapeAttempt, HorizontalDistance(playerPosition, destination), fly);
        status = $"Incidental combat blocks travel; escape attempt {incidentalAggroEscapeAttempt} underway without attacking";
    }

    private bool TryStartIncidentalAggroEscape(
        IReadOnlyCollection<IBattleChara> threats,
        int attempt,
        out Vector3 destination,
        out bool fly)
    {
        destination = default;
        fly = condition[ConditionFlag.Mounted] || condition[ConditionFlag.InFlight];
        var start = PlayerPosition();
        var threatCenter = threats.Count > 0
            ? new Vector3(threats.Average(actor => actor.Position.X), start.Y,
                threats.Average(actor => actor.Position.Z))
            : start;
        var away = new Vector2(start.X - threatCenter.X, start.Z - threatCenter.Z);
        if (away.LengthSquared() < 0.01f)
        {
            var seedAngle = attempt * 2.3999632f;
            away = new Vector2(MathF.Cos(seedAngle), MathF.Sin(seedAngle));
        }
        else
        {
            away = Vector2.Normalize(away);
        }

        var liveMark = FindMark();
        var angleOffsets = new[] { 0f, 0.55f, -0.55f, 1.1f, -1.1f, 1.65f, -1.65f, MathF.PI };
        var rotationStart = Math.Max(0, attempt - 1) % angleOffsets.Length;
        for (var index = 0; index < angleOffsets.Length; index++)
        {
            var angle = angleOffsets[(rotationStart + index) % angleOffsets.Length];
            var direction = new Vector2(
                away.X * MathF.Cos(angle) - away.Y * MathF.Sin(angle),
                away.X * MathF.Sin(angle) + away.Y * MathF.Cos(angle));
            var distance = IncidentalAggroEscapeDistance + (attempt % 3) * 10f;
            var proposed = new Vector3(start.X + direction.X * distance,
                fly ? start.Y + 15f : start.Y,
                start.Z + direction.Y * distance);
            var candidate = fly ? proposed : vnav.PointOnFloorSafe(proposed, 15f);
            if (candidate is null)
                continue;

            if (liveMark is not null)
            {
                var minimumClearance = Math.Max(ActiveDistanceProfile.WaitingDistance,
                    ActiveDistanceProfile.EmergencyDistance);
                if (ClearanceAtPoint(candidate.Value, liveMark) < minimumClearance ||
                    !ProtectedSegmentIsSafe(start, candidate.Value, liveMark.Position,
                        ProtectedCenterRadius(liveMark), true))
                {
                    log.Information("Incidental-aggro escape candidate rejected: crosses the active mark safety radius");
                    continue;
                }
            }

            if (!vnav.MoveToSafe(candidate.Value, fly))
                continue;
            destination = candidate.Value;
            return true;
        }

        return false;
    }

    private void ResetIncidentalAggroTracking()
    {
        incidentalAggroResumeState = SentinelState.Idle;
        incidentalAggroStartedUtc = DateTime.MinValue;
        incidentalAggroClearSinceUtc = DateTime.MinValue;
        incidentalAggroLastProgressUtc = DateTime.MinValue;
        incidentalAggroLastPosition = default;
        incidentalAggroEscapeAttempt = 0;
    }

    private void ResetSsStagingTracking()
    {
        activeSsStagingLocation = null;
        ssStagingAnchor = null;
        ssStagingDestination = null;
        selectedSsStagingCandidate = null;
        ssStagingPathTask = null;
        selectedSsStagingPath = null;
        ssStagingPathStartedUtc = DateTime.MinValue;
        nextSsStagingAttemptUtc = DateTime.MinValue;
        ssStagingArrived = false;
        ssStagingProjectionFailureLogged = false;
        ssStagingCandidates.Clear();
    }

    private void TickDeadReturnRecovery(DateTime now)
    {
        if (returnRecoveryStartedUtc == DateTime.MinValue)
        {
            returnRecoveryStartedUtc = now;
            returnExpectedWorld = travel.CurrentWorld;
        }

        if (returnInitiatedBySentinel)
        {
            if (returnConfirmed)
            {
                if ((now - returnConfirmedUtc).TotalSeconds < ReturnTransitionTimeoutSeconds)
                {
                    status = "Return confirmed; waiting for zone transition";
                    return;
                }

                ReportReturnRecoveryFailure(
                    "Return confirmation was accepted but the zone transition timed out; retrying Return");
                returnInitiatedBySentinel = false;
                returnConfirmed = false;
                nextActionUtc = now.AddSeconds(ReturnActionRetrySeconds);
                return;
            }

            if ((now - returnActionIssuedUtc).TotalSeconds < ReturnDialogTimeoutSeconds)
            {
                status = "Initiating Return to Ul'dah; waiting for its confirmation dialog";
                return;
            }

            ReportReturnRecoveryFailure("Return confirmation did not appear; retrying Return");
            returnInitiatedBySentinel = false;
            returnConfirmationObserved = false;
            nextActionUtc = now.AddSeconds(ReturnActionRetrySeconds);
            return;
        }

        if (now < nextActionUtc)
        {
            if (returnWatchdogWarning)
                status = $"Return recovery has not completed after repeated attempts; retrying in " +
                         $"{Math.Max(0, Math.Ceiling((nextActionUtc - now).TotalSeconds)):0}s";
            return;
        }

        var returnStatus = combat.GetReturnActionStatus();
        if (returnStatus != 0)
        {
            status = returnStatus == uint.MaxValue
                ? "Return is unavailable because the game action manager is not ready; retrying later"
                : $"Return is unavailable or on cooldown (game status {returnStatus}); retrying later";
            log.Warning("Could not initiate Return on {World}; action status {Status}", returnExpectedWorld, returnStatus);
            nextActionUtc = now.AddSeconds(ReturnActionRetrySeconds);
            return;
        }

        returnActionAttempts++;
        status = "Initiating Return to Ul'dah";
        log.Information("Initiating Return to Ul'dah on {World}; attempt {Attempt}",
            returnExpectedWorld, returnActionAttempts);

        if (!combat.UseReturn())
        {
            ReportReturnRecoveryFailure("The game rejected the Return action; retrying later");
            nextActionUtc = now.AddSeconds(ReturnActionRetrySeconds);
            return;
        }

        returnInitiatedBySentinel = true;
        returnConfirmationObserved = false;
        returnConfirmed = false;
        returnActionIssuedUtc = now;
        nextReturnConfirmationAttemptUtc = now;
        nextActionUtc = now.AddSeconds(ReturnActionRetrySeconds);
    }

    private void ReportReturnRecoveryFailure(string message)
    {
        status = $"Return recovery failure: {message}";
        log.Error("Return recovery failure: {Message}", message);
    }

    private void UpdateReturnRecoveryWatchdog(DateTime now)
    {
        if (returnRecoveryStartedUtc == DateTime.MinValue || returnWatchdogWarning ||
            (now - returnRecoveryStartedUtc).TotalSeconds < ReturnRecoveryWatchdogSeconds)
            return;

        returnWatchdogWarning = true;
        log.Error("Return recovery watchdog expired after {Seconds}s on {World}; retries will continue with backoff",
            ReturnRecoveryWatchdogSeconds, returnExpectedWorld);
    }

    private void ResetReturnRecoveryTracking()
    {
        returnRecoveryStartedUtc = DateTime.MinValue;
        returnActionIssuedUtc = DateTime.MinValue;
        returnConfirmedUtc = DateTime.MinValue;
        nextReturnConfirmationAttemptUtc = DateTime.MinValue;
        returnExpectedWorld = string.Empty;
        returnInitiatedBySentinel = false;
        returnConfirmationObserved = false;
        returnConfirmed = false;
        returnWatchdogWarning = false;
        returnActionAttempts = 0;
        returnConfirmationAttempts = 0;
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
        if (current is not null)
            log.Information("Travel state entered for {Mark}: arrived on {World}; next territory {Territory}, instance {Instance}",
                current.CreatureName, targetWorld, current.TerritoryId, current.Instance);
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
        if (NeedsIdyllshireGateway())
        {
            BeginHinterlandsGateway();
            return;
        }
        if (territoryAetheryteId == 0)
        {
            if (now < nextActionUtc)
                return;
            if (TryResolveTerritoryAetheryte())
            {
                log.Information("Recovered territory teleport destination {AetheryteId} for active hunt {Mark}",
                    territoryAetheryteId, current.CreatureName);
                nextActionUtc = now;
                return;
            }

            status = $"No attuned aetheryte is currently resolvable for territory {current.TerritoryId}; " +
                     $"retaining {current.CreatureName} and retrying";
            log.Warning("No attuned aetheryte currently resolved for {Mark} in territory {Territory}; active hunt retained",
                current.CreatureName, current.TerritoryId);
            nextActionUtc = now.AddSeconds(5);
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
        if (!travel.IsBusy && NeedsIdyllshireGateway())
        {
            BeginHinterlandsGateway();
            return;
        }
        if (TravelTimedOut(now))
            FailCurrent($"Teleport arrival in territory {current.TerritoryId} timed out");
    }

    private bool NeedsIdyllshireGateway() =>
        current?.TerritoryId == HuntCatalog.DravanianHinterlandsTerritoryId &&
        clientState.TerritoryType == HuntCatalog.IdyllshireTerritoryId;

    private void BeginHinterlandsGateway()
    {
        nextActionUtc = DateTime.UtcNow;
        SetState(SentinelState.OpenHinterlandsGateway,
            "Using Idyllshire's normal Prologue Gate aethernet route to the Dravanian Hinterlands");
    }

    private void TickOpenHinterlandsGateway(DateTime now)
    {
        if (current is null)
            return;
        if (clientState.TerritoryType == current.TerritoryId)
        {
            BeginInstanceCheck();
            return;
        }
        if (!NeedsIdyllshireGateway())
        {
            if (!travel.IsBusy)
                SetState(SentinelState.TeleportToTerritory, "Re-establishing the normal territory route");
            return;
        }
        if (TravelTimedOut(now))
        {
            FailCurrent("Could not open the Idyllshire aethernet for the western gate");
            return;
        }
        if (now < nextActionUtc || travel.IsBusy)
            return;
        if (condition[ConditionFlag.Mounted])
        {
            UseGeneralAction(23);
            nextActionUtc = now.AddSeconds(2);
            return;
        }
        if (travel.InteractWithNearbyAetheryte(35f))
        {
            SetState(SentinelState.SelectHinterlandsGateway,
                "Selecting Prologue Gate (Western Hinterlands)");
            return;
        }
        nextActionUtc = now.AddSeconds(2);
    }

    private void TickSelectHinterlandsGateway(DateTime now)
    {
        if (current is null)
            return;
        if (clientState.TerritoryType == current.TerritoryId)
        {
            BeginInstanceCheck();
            return;
        }
        if (TravelTimedOut(now))
        {
            FailCurrent("Prologue Gate was not available from the Idyllshire aethernet");
            return;
        }
        // Some client/menu states expose the destination list immediately; others first expose
        // the ordinary "Select aethernet destination" entry. Support both normal UI paths.
        if (travel.SelectIdyllshireWesternGate())
        {
            SetState(SentinelState.WaitForHinterlands,
                "Traveling normally through Prologue Gate to the Dravanian Hinterlands");
            return;
        }
        if (travel.SelectAethernetTravelMenu())
            SetState(SentinelState.SelectHinterlandsDestination,
                "Choosing Prologue Gate (Western Hinterlands) from the aethernet");
    }

    private void TickSelectHinterlandsDestination(DateTime now)
    {
        if (current is null)
            return;
        if (clientState.TerritoryType == current.TerritoryId)
        {
            BeginInstanceCheck();
            return;
        }
        if (TravelTimedOut(now))
        {
            FailCurrent("Prologue Gate was not available in the Idyllshire aethernet");
            return;
        }
        if (travel.SelectIdyllshireWesternGate())
            SetState(SentinelState.WaitForHinterlands,
                "Traveling normally through Prologue Gate to the Dravanian Hinterlands");
    }

    private void TickWaitForHinterlands(DateTime now)
    {
        if (current is null)
            return;
        if (!travel.IsBusy && clientState.TerritoryType == current.TerritoryId)
        {
            BeginInstanceCheck();
            return;
        }
        if (TravelTimedOut(now))
            FailCurrent("Arrival in the Dravanian Hinterlands timed out");
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

        if (markEverIdentified && alertPoint is null)
        {
            SetState(SentinelState.LocateMark,
                "vnavmesh mesh is fully ready; resuming entity resolution for the previously identified mark");
            return;
        }

        SetState(SentinelState.PrepareApproachDestination,
            "vnavmesh mesh is fully ready; resolving the active hunt's stored alert coordinates");
    }

    private void TickPrepareApproachDestination(DateTime now)
    {
        if (current is null)
            return;
        if (!vnav.IsReadySafe())
        {
            BeginMeshWait("vnavmesh mesh readiness was lost");
            return;
        }

        if (alertPoint is null)
        {
            status = $"Waiting for a usable mapped local destination for {current.CreatureName}; the hunt remains active";
            return;
        }

        if (approachPoint is null)
        {
            if (now < nextActionUtc)
                return;
            if (approachProjectionCandidates.Count == 0)
                PrepareApproachProjectionCandidates(alertPoint.Value);
            approachPoint = approachProjectionCandidates.Count > 0
                ? approachProjectionCandidates.Dequeue()
                : null;
            nextActionUtc = now.AddSeconds(2);
            if (approachPoint is null)
            {
                status = $"Could not project {current.CreatureName}'s mapped local destination onto vnavmesh yet; " +
                         "holding the active hunt and retrying";
                return;
            }
            status = $"Direct local destination ready for {current.CreatureName}; preparing normal flight";
        }

        if (now < nextActionUtc)
            return;
        if (!EnsureMounted(now))
            return;
        if (vnav.MoveCloseToSafe(approachPoint.Value, true, ActiveDistanceProfile.FlagApproachDistance))
        {
            nextActionUtc = now.AddSeconds(3);
            SetState(SentinelState.ApproachAlertCoordinates,
                $"Flying toward {current.CreatureName}'s reported coordinates; " +
                $"entity resolution waits until within about {ActiveDistanceProfile.FlagApproachDistance:0}y");
            return;
        }

        nextActionUtc = now.AddSeconds(3);
        if (HuntCatalog.IsAnySsName(current.CreatureName) && approachProjectionCandidates.Count > 0)
        {
            approachPoint = null;
            status = $"The first {current.CreatureName} projection was unreachable; trying a nearby candidate";
        }
        else
            status = $"No route to {current.CreatureName}'s alert coordinates is available yet; holding and retrying";
    }

    private void TickApproachAlertCoordinates(DateTime now)
    {
        if (current is null)
            return;
        if (!vnav.IsReadySafe())
        {
            vnav.StopSafe();
            BeginMeshWait("vnavmesh readiness was lost during the coordinate approach");
            return;
        }
        if (approachPoint is null)
        {
            SetState(SentinelState.PrepareApproachDestination,
                "Direct alert-coordinate projection was lost; rebuilding it without abandoning the active hunt");
            return;
        }

        var distance = HorizontalDistance(PlayerPosition(), approachPoint.Value);
        if (distance <= ActiveDistanceProfile.FlagApproachDistance + 8f)
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

        if (vnav.MoveCloseToSafe(approachPoint.Value, true, ActiveDistanceProfile.FlagApproachDistance))
            status = $"Coordinate route stopped early; retrying while keeping {current.CreatureName} active";
        else if (HuntCatalog.IsAnySsName(current.CreatureName) && approachProjectionCandidates.Count > 0)
        {
            approachPoint = null;
            SetState(SentinelState.PrepareApproachDestination,
                $"Mapped {current.CreatureName} destination was unreachable; trying another nearby projection");
        }
        else
            status = $"Coordinate route is currently unavailable; holding position and retrying {current.CreatureName}";
        nextActionUtc = now.AddSeconds(3);
    }

    private void PrepareApproachProjectionCandidates(Vector3 anchor)
    {
        approachProjectionCandidates.Clear();
        var projected = new List<Vector3>();
        var isSs = current is not null && HuntCatalog.IsAnySsName(current.CreatureName);
        var offsets = isSs
            ? new[] { 0f, 6f, 12f, 18f, 24f }
            : new[] { 0f };
        var angles = new[] { 0f, 45f, 90f, 135f, 180f, 225f, 270f, 315f };

        foreach (var radius in offsets)
        {
            foreach (var angle in radius == 0f ? new[] { 0f } : angles)
            {
                var radians = angle * MathF.PI / 180f;
                var sample = anchor + new Vector3(MathF.Cos(radians) * radius, 0f, MathF.Sin(radians) * radius);
                sample.Y = 1024f;
                var floor = vnav.PointOnFloorSafe(sample, isSs ? 18f : 20f);
                if (floor is null || projected.Any(point => HorizontalDistance(point, floor.Value) < 2f))
                    continue;
                projected.Add(floor.Value);
            }
        }

        foreach (var point in projected.OrderBy(point => HorizontalDistance(point, anchor)))
            approachProjectionCandidates.Enqueue(point);
        if (isSs && projected.Count > 0 && HorizontalDistance(projected[0], anchor) > 2f)
            log.Information(
                "Fixed SS coordinate projection failed; selected {Count} nearby projected candidate(s) for routing",
                projected.Count);
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
            selectedParkingCandidate = null;
            parkingPathTask = null;
            selectedParkingPath = null;
            parkingCandidates.Clear();
            nextActionUtc = now.AddSeconds(1);
            SetState(SentinelState.LocateMark,
                "The previously identified mark temporarily left object range while parking; holding and rescanning");
            return;
        }
        MarkWasIdentified(mark);
        if (parkingPathTask is not null)
        {
            PollCrowdParkingPath(mark, now);
            return;
        }
        if (safePoint is not null && HorizontalDistance(PlayerPosition(), safePoint.Value) <= 5f)
        {
            vnav.StopSafe();
            if (!RevalidateParkingForLanding(mark, out var reason))
            {
                RestartSafeParkingAfterRevalidation(mark, reason);
                return;
            }
            SetState(SentinelState.Landing, "At the safe parking point; landing normally");
            return;
        }
        if ((now - stateSinceUtc).TotalSeconds > 4 && !vnav.IsPathRunningSafe() && !vnav.IsPathfindInProgressSafe())
        {
            if (!TryStartNextParkingRoute(true, mark))
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
        mark = FindMark();
        if (mark is null)
        {
            vnav.StopSafe();
            safePoint = null;
            selectedParkingCandidate = null;
            selectedParkingPath = null;
            nextActionUtc = now.AddSeconds(1);
            SetState(SentinelState.LocateMark,
                "Mark temporarily left object range before landing; remaining mounted and rescanning");
            return;
        }
        MarkWasIdentified(mark);
        if (!RevalidateParkingForLanding(mark, out var reason))
        {
            RestartSafeParkingAfterRevalidation(mark, reason);
            return;
        }
        if (condition[ConditionFlag.InFlight] || condition[ConditionFlag.Mounted])
        {
            UseGeneralAction(23);
            nextActionUtc = now.AddSeconds(1);
            return;
        }
        SetState(SentinelState.SafeWait,
            $"Parked {ActiveDistanceProfile.WaitingDistance:0}y clear; " +
            $"emergency floor {ActiveDistanceProfile.EmergencyDistance:0}y");
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

        if (clearance < ActiveDistanceProfile.EmergencyDistance)
        {
            BeginGroundRetreat(mark);
            return;
        }

        if (!tagAttempted && inCombat && hp <= ActiveDistanceProfile.EngageHpPercent)
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
            {
                log.Information("Proper pull detected for {Mark}; attempting ranged tag for pull cycle {PullCycle}",
                    mark.Name.TextValue, pullCycle);
                SetState(SentinelState.TagApproach,
                    $"Proper pull detected; attempting ranged tag for pull cycle {pullCycle} at {hp:0.0}% HP");
            }
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
                BeginGroundRetreat(mark, postTag: true);
                status = $"Attack cutoff active; retreating to {ActiveDistanceProfile.WaitingDistance:0}y after the one tag attempt";
            }
            else
            {
                status = $"One tag attempt sent (action {activeTagActionId}); holding still briefly so casted tags are not cancelled";
            }
            return;
        }

        if (!CombatController.IsMarkInCombat(mark) ||
            CombatController.HpPercent(mark) > ActiveDistanceProfile.EngageHpPercent)
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
                    pullCycleTagged = true;
                    nextActionUtc = now.AddSeconds(3);
                    status = $"Tagged {mark.Name.TextValue} for pull cycle {pullCycle} (action {activeTagActionId}); client " +
                             (attempt.Accepted ? "accepted it" : "did not accept it") +
                             "; attack cutoff is active and no further attacks will be issued";
                    log.Information(
                        "Tagged {Mark} for pull cycle {PullCycle}; one action attempt sent, client accepted={Accepted}",
                        mark.Name.TextValue, pullCycle, attempt.Accepted);
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
        if (parkingPathTask is not null)
        {
            PollCrowdParkingPath(mark, now);
            return;
        }
        if (postTagRetreatActive && selectedParkingCandidate is not null && safePoint is not null)
        {
            var protectedRadius = ProtectedCenterRadius(mark);
            if (ClearanceAtPoint(safePoint.Value, mark) < ActiveDistanceProfile.WaitingDistance - 0.5f ||
                !ProtectedSegmentIsSafe(PlayerPosition(), safePoint.Value, mark.Position, protectedRadius, true))
            {
                vnav.StopSafe();
                log.Information("Post-tag retreat target invalidated by mark movement; selecting a new safe target");
                BeginGroundRetreat(mark, postTag: true);
                return;
            }
            if (HorizontalDistance(PlayerPosition(), safePoint.Value) <= 5f &&
                ClearanceFromMark(mark) >= ActiveDistanceProfile.WaitingDistance - 0.5f)
            {
                vnav.StopSafe();
                var finalClearance = ClearanceFromMark(mark);
                postTagRetreatActive = false;
                status = $"Retreat complete; holding position {finalClearance:0}y from mark";
                log.Information("Retreat complete; holding position {Clearance:0}y from mark", finalClearance);
                SetState(SentinelState.SafeWait, status);
                return;
            }
        }
        if (!postTagRetreatActive && ClearanceFromMark(mark) >= ActiveDistanceProfile.WaitingDistance - 2f)
        {
            vnav.StopSafe();
            SetState(SentinelState.SafeWait, tagAttempted ? "One tag attempt completed; safe radius restored" : "Safe radius restored");
            return;
        }
        if ((now - stateSinceUtc).TotalSeconds > 4 && !vnav.IsPathRunningSafe() && !vnav.IsPathfindInProgressSafe())
        {
            if (!TryStartNextParkingRoute(false, mark))
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

        if (!ssSpawnAnnounced && now >= ssWatchDeadlineUtc)
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

        TickSsStaging(now);
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
            log.Information("Actual SS detected; switching to entity tracking: {Ss}", activeSsProfile.SsName);
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

    private void TickSsStaging(DateTime now)
    {
        if (current is null || activeSsProfile is null)
            return;
        if (!TryInitializeSsStagingAnchor())
        {
            status = $"No fixed {activeSsProfile.SsName} staging coordinate is configured for territory " +
                     $"{current.TerritoryId}; holding the SS watch without abandoning it";
            return;
        }
        if (!vnav.IsReadySafe())
        {
            vnav.StopSafe();
            status = $"Waiting for vnavmesh readiness before staging for {activeSsProfile.SsName}; " +
                     "the SS watch remains active";
            return;
        }

        var anchor = ssStagingAnchor!.Value;
        if (ssStagingArrived)
        {
            if (condition[ConditionFlag.InFlight] || condition[ConditionFlag.Mounted])
            {
                if (now >= nextActionUtc)
                {
                    UseGeneralAction(23);
                    nextActionUtc = now.AddSeconds(1);
                }
                status = $"At the {activeSsProfile.SsName} staging point; landing normally";
                return;
            }

            status = ssSpawnAnnounced
                ? $"{activeSsProfile.SsName} announced; holding at its staging point until the entity is detectable"
                : $"Holding at SS staging point; waiting for precursors ({SsWatchRemaining(now)})";
            return;
        }

        if (ssStagingPathTask is not null)
        {
            PollSsStagingPath(now, anchor);
            return;
        }

        if (ssStagingDestination is not null)
        {
            var remaining = HorizontalDistance(PlayerPosition(), ssStagingDestination.Value);
            if (remaining <= SsStagingArrivalDistance)
            {
                vnav.StopSafe();
                if (!RevalidateSsStagingDestination(anchor, out var reason))
                {
                    log.Information("{Reason}", reason);
                    status = reason;
                    InvalidateSsStagingRoute(now);
                    return;
                }

                ssStagingArrived = true;
                nextActionUtc = now;
                log.Information(
                    "SS staging candidate selected and reached: {Ss} in {Territory}, {Distance:0.0}y from fixed spawn",
                    activeSsProfile.SsName, activeSsStagingLocation!.TerritoryName,
                    HorizontalDistance(PlayerPosition(), anchor));
                return;
            }

            if (vnav.IsPathRunningSafe() || vnav.IsPathfindInProgressSafe())
            {
                status = $"Navigating to SS staging area for {activeSsProfile.SsName} ({remaining:0}y remaining)";
                return;
            }

            if (now < nextSsStagingAttemptUtc)
                return;
            log.Information("SS staging route stopped before arrival; trying another nearby candidate");
            InvalidateSsStagingRoute(now);
        }

        if (now < nextSsStagingAttemptUtc)
            return;
        if (!EnsureMounted(now))
            return;

        if (ssStagingCandidates.Count == 0)
            PrepareSsStagingCandidates(anchor);
        if (!TryStartNextSsStagingRoute(now, anchor))
        {
            nextSsStagingAttemptUtc = now.AddSeconds(SsStagingRouteRetrySeconds);
            status = $"Fixed SS coordinate projection failed; trying nearby candidate for {activeSsProfile.SsName}";
            log.Information("Fixed SS coordinate projection failed; trying nearby candidate");
        }
    }

    private bool TryInitializeSsStagingAnchor()
    {
        if (ssStagingAnchor is not null && activeSsStagingLocation is not null)
            return true;
        if (current is null || activeSsProfile is null ||
            !HuntCatalog.TryGetSsStagingLocation(current.TerritoryId, out var location) ||
            !HuntCatalog.NamesMatch(location.SsName, activeSsProfile.SsName))
            return false;

        var map = data.GetExcelSheet<Map>()
            .FirstOrDefault(row => row.TerritoryType.RowId == current.TerritoryId);
        if (map.RowId == 0)
            return false;

        var mapped = new MapLinkPayload(current.TerritoryId, map.RowId, location.MapX, location.MapY);
        activeSsStagingLocation = location;
        ssStagingAnchor = new Vector3(mapped.RawX / 1000f, 1024f, mapped.RawY / 1000f);
        log.Information(
            "SS staging location: {Territory} X{MapX:0.0} Y{MapY:0.0} for {Ss}; local anchor ({LocalX:0.0}, {LocalZ:0.0})",
            location.TerritoryName, location.MapX, location.MapY, location.SsName,
            ssStagingAnchor.Value.X, ssStagingAnchor.Value.Z);
        return true;
    }

    private void PrepareSsStagingCandidates(Vector3 anchor)
    {
        ssStagingCandidates.Clear();
        selectedSsStagingCandidate = null;
        ssStagingPathTask = null;
        selectedSsStagingPath = null;
        ssStagingDestination = null;

        if (!ssStagingProjectionFailureLogged && vnav.PointOnFloorSafe(anchor, 10f) is null)
        {
            ssStagingProjectionFailureLogged = true;
            log.Information("Fixed SS coordinate projection failed; trying nearby candidate");
        }

        var player = PlayerPosition();
        var playerRadius = objects.LocalPlayer?.HitboxRadius ?? 0f;
        var safeRadius = ActiveDistanceProfile.WaitingDistance + playerRadius + 2f;
        var minimumRadius = ActiveDistanceProfile.EmergencyDistance + playerRadius + 3f;
        var accepted = new List<Vector3>();

        var crowdCandidates = new List<(SsStagingCandidate Candidate, float Score)>();
        foreach (var cluster in DetectPlayerClusters(anchor))
        {
            var towardCrowd = cluster.Center - anchor;
            towardCrowd.Y = 0f;
            if (towardCrowd.LengthSquared() < 0.01f)
                continue;
            towardCrowd = Vector3.Normalize(towardCrowd);
            var tangent = new Vector3(-towardCrowd.Z, 0f, towardCrowd.X);
            var clusterRadius = HorizontalDistance(cluster.Center, anchor);
            foreach (var lateral in new[] { 6f, -6f, 10f, -10f })
            {
                var candidate = anchor + towardCrowd * MathF.Max(safeRadius, clusterRadius + 2f) + tangent * lateral;
                candidate.Y = 1024f;
                var projected = vnav.PointOnFloorSafe(candidate, 18f);
                if (projected is null ||
                    HorizontalDistance(projected.Value, anchor) < safeRadius - 0.5f ||
                    HorizontalDistance(projected.Value, anchor) < minimumRadius ||
                    HorizontalDistance(projected.Value, cluster.Center) > CrowdRevalidationRadius ||
                    accepted.Any(point => HorizontalDistance(point, projected.Value) < 2f))
                    continue;

                var score = cluster.Population * 1000f - cluster.Tightness * 25f -
                            HorizontalDistance(projected.Value, cluster.Center) * 3f -
                            HorizontalDistance(player, projected.Value) * 0.25f;
                crowdCandidates.Add((
                    new SsStagingCandidate(projected.Value, true, cluster.Population, cluster.Center), score));
            }
        }

        foreach (var entry in crowdCandidates.OrderByDescending(entry => entry.Score))
        {
            accepted.Add(entry.Candidate.Position);
            ssStagingCandidates.Enqueue(entry.Candidate);
        }

        var away = player - anchor;
        away.Y = 0f;
        if (away.LengthSquared() < 0.01f)
            away = Vector3.UnitX;
        away = Vector3.Normalize(away);
        foreach (var extraRadius in new[] { 0f, 6f, 12f, 18f })
        {
            foreach (var angle in new[] { 0f, 25f, -25f, 50f, -50f, 80f, -80f, 110f, -110f, 145f, -145f, 180f })
            {
                var radians = angle * MathF.PI / 180f;
                var direction = new Vector3(
                    away.X * MathF.Cos(radians) - away.Z * MathF.Sin(radians),
                    0f,
                    away.X * MathF.Sin(radians) + away.Z * MathF.Cos(radians));
                var candidate = anchor + direction * (safeRadius + extraRadius);
                candidate.Y = 1024f;
                var projected = vnav.PointOnFloorSafe(candidate, 18f);
                if (projected is null ||
                    HorizontalDistance(projected.Value, anchor) < safeRadius - 0.5f ||
                    HorizontalDistance(projected.Value, anchor) < minimumRadius ||
                    accepted.Any(point => HorizontalDistance(point, projected.Value) < 2f))
                    continue;
                accepted.Add(projected.Value);
                ssStagingCandidates.Enqueue(new SsStagingCandidate(projected.Value, false, 0, Vector3.Zero));
            }
        }

        log.Information(
            "Prepared {Count} safely projected SS staging candidates near {Ss} ({CrowdCount} crowd-aware)",
            ssStagingCandidates.Count, activeSsProfile?.SsName ?? "SS", crowdCandidates.Count);
    }

    private bool TryStartNextSsStagingRoute(DateTime now, Vector3 anchor)
    {
        var protectedRadius = ActiveDistanceProfile.EmergencyDistance +
                              (objects.LocalPlayer?.HitboxRadius ?? 0f) + 3f;
        while (ssStagingCandidates.Count > 0)
        {
            var candidate = ssStagingCandidates.Dequeue();
            if (!ProtectedSegmentIsSafe(PlayerPosition(), candidate.Position, anchor, protectedRadius, true))
            {
                log.Information("SS staging candidate rejected: route crosses fixed spawn safety radius");
                continue;
            }

            var startDistance = HorizontalDistance(PlayerPosition(), anchor);
            var queryRadius = startDistance < protectedRadius
                ? MathF.Max(1f, startDistance - 1f)
                : protectedRadius;
            var pathTask = vnav.PathfindAvoidSafe(PlayerPosition(), candidate.Position, true, anchor, queryRadius);
            if (pathTask is null)
                continue;

            selectedSsStagingCandidate = candidate;
            ssStagingPathTask = pathTask;
            ssStagingPathStartedUtc = now;
            status = candidate.IsCrowd
                ? $"Validating an SS staging route near a {candidate.CrowdPopulation}-player crowd"
                : "Validating a protected route to the SS staging area";
            return true;
        }
        return false;
    }

    private void PollSsStagingPath(DateTime now, Vector3 anchor)
    {
        var task = ssStagingPathTask;
        var candidate = selectedSsStagingCandidate;
        if (task is null || candidate is null)
            return;
        if (!task.IsCompleted)
        {
            if ((now - ssStagingPathStartedUtc).TotalSeconds <= SsStagingPathQueryTimeoutSeconds)
            {
                status = "Validating a protected vnavmesh route to the fixed SS staging area";
                return;
            }
            log.Information("SS staging candidate rejected: vnavmesh path query timed out");
            ssStagingPathTask = null;
            selectedSsStagingCandidate = null;
            TryStartNextSsStagingRoute(now, anchor);
            return;
        }

        List<Vector3>? path = null;
        try
        {
            if (task.IsCompletedSuccessfully)
                path = task.Result;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "SS staging path query failed");
        }
        ssStagingPathTask = null;

        var protectedRadius = ActiveDistanceProfile.EmergencyDistance +
                              (objects.LocalPlayer?.HitboxRadius ?? 0f) + 3f;
        if (path is null || path.Count == 0 ||
            !ProtectedSegmentIsSafe(PlayerPosition(), candidate.Position, anchor, protectedRadius, true) ||
            !PathStaysOutsideProtectedRadius(PlayerPosition(), path, anchor, protectedRadius, true) ||
            !vnav.MovePathSafe(path, true))
        {
            log.Information("SS staging candidate rejected: no safely reachable vnavmesh route");
            selectedSsStagingCandidate = null;
            if (!TryStartNextSsStagingRoute(now, anchor))
                nextSsStagingAttemptUtc = now.AddSeconds(SsStagingRouteRetrySeconds);
            return;
        }

        ssStagingDestination = candidate.Position;
        selectedSsStagingPath = path;
        nextSsStagingAttemptUtc = now.AddSeconds(SsStagingRouteRetrySeconds);
        status = candidate.IsCrowd
            ? $"Navigating to crowd-aware SS staging ({candidate.CrowdPopulation} players)"
            : "Navigating to SS staging area";
        log.Information(
            "SS staging candidate selected: {Type}, {Distance:0.0}y from fixed spawn",
            candidate.IsCrowd ? $"crowd ({candidate.CrowdPopulation} players)" : "standard",
            HorizontalDistance(candidate.Position, anchor));
    }

    private bool RevalidateSsStagingDestination(Vector3 anchor, out string reason)
    {
        var candidate = selectedSsStagingCandidate;
        if (candidate is null || ssStagingDestination is null)
        {
            reason = "SS staging destination was lost; sampling another nearby point";
            return false;
        }

        var playerRadius = objects.LocalPlayer?.HitboxRadius ?? 0f;
        if (HorizontalDistance(candidate.Position, anchor) < ActiveDistanceProfile.WaitingDistance + playerRadius - 0.5f ||
            HorizontalDistance(candidate.Position, anchor) < ActiveDistanceProfile.EmergencyDistance + playerRadius)
        {
            reason = "SS staging destination no longer meets the active safety profile; resampling";
            return false;
        }

        var protectedRadius = ActiveDistanceProfile.EmergencyDistance + playerRadius + 3f;
        if (!ProtectedSegmentIsSafe(PlayerPosition(), candidate.Position, anchor, protectedRadius, true) ||
            !FinalApproachStaysOutsideProtectedRadius(selectedSsStagingPath, anchor, protectedRadius))
        {
            reason = "SS staging final approach crosses the fixed spawn safety radius; resampling";
            return false;
        }

        if (candidate.IsCrowd)
        {
            var liveCluster = DetectPlayerClusters(anchor)
                .OrderBy(cluster => HorizontalDistance(cluster.Center, candidate.CrowdCenter))
                .FirstOrDefault();
            if (liveCluster is null ||
                HorizontalDistance(liveCluster.Center, candidate.CrowdCenter) > CrowdMovementTolerance ||
                HorizontalDistance(liveCluster.Center, candidate.Position) > CrowdRevalidationRadius)
            {
                reason = "SS staging crowd moved before landing; resampling a safe point";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private void InvalidateSsStagingRoute(DateTime now)
    {
        vnav.StopSafe();
        ssStagingDestination = null;
        selectedSsStagingCandidate = null;
        ssStagingPathTask = null;
        selectedSsStagingPath = null;
        ssStagingArrived = false;
        nextSsStagingAttemptUtc = now.AddSeconds(SsStagingRouteRetrySeconds);
    }

    private string SsWatchRemaining(DateTime now) => ssSpawnAnnounced
        ? "spawn announced; timeout disabled"
        : $"{Math.Max(0, (int)Math.Ceiling((ssWatchDeadlineUtc - now).TotalSeconds))}s remaining";

    private void BeginSafeParking(IBattleChara target, bool fly)
    {
        if (!vnav.IsReadySafe())
        {
            BeginMeshWait("vnavmesh readiness was lost before dynamic safe parking");
            return;
        }
        if (fly && !EnsureMounted(DateTime.UtcNow))
            return;
        PrepareParkingCandidates(target, ActiveDistanceProfile.WaitingDistance, preferCrowd: true);
        if (!TryStartNextParkingRoute(fly, target))
        {
            vnav.StopSafe();
            nextActionUtc = DateTime.UtcNow.AddSeconds(3);
            SetState(SentinelState.LocateMark,
                "No sampled safe parking route is available yet; keeping the hunt active and retrying near the alert area");
            return;
        }
        SetState(SentinelState.MoveToSafePoint,
            $"Parking dynamically {ActiveDistanceProfile.WaitingDistance:0}y clear of the S rank " +
            $"({parkingCandidates.Count} alternatives ready)");
    }

    private void BeginGroundRetreat(IBattleChara target, bool postTag = false)
    {
        postTagRetreatActive = postTag;
        PrepareParkingCandidates(target, ActiveDistanceProfile.WaitingDistance,
            preferCrowd: postTag, randomizedRetreat: postTag);
        if (!TryStartNextParkingRoute(false, target))
        {
            vnav.StopSafe();
            postTagRetreatActive = false;
            SetState(SentinelState.SafeWait, postTag
                ? "No protected randomized retreat route was reachable; holding position safely"
                : "No sampled ground-retreat route was reachable; holding position");
            return;
        }
        SetState(SentinelState.GroundRetreat,
            (postTag ? "Post-tag randomized retreat" : $"Retreating to {ActiveDistanceProfile.WaitingDistance:0}y clear") + " " +
            $"({parkingCandidates.Count} alternatives ready)");
    }

    private void PrepareParkingCandidates(
        IBattleChara target,
        float clearance,
        bool preferCrowd,
        bool randomizedRetreat = false)
    {
        parkingCandidates.Clear();
        safePoint = null;
        selectedParkingCandidate = null;
        parkingPathTask = null;
        parkingPathStartedUtc = DateTime.MinValue;
        selectedParkingPath = null;
        crowdFallbackAnnounced = !preferCrowd;
        var player = PlayerPosition();
        var away = player - target.Position;
        away.Y = 0;
        if (away.LengthSquared() < 0.01f)
            away = Vector3.UnitX;
        away = Vector3.Normalize(away);

        var randomAngle = randomizedRetreat ? Random.Shared.NextSingle() * 50f - 25f : 0f;
        float[] angles = randomizedRetreat
            ? [randomAngle, randomAngle + 22f, randomAngle - 22f, randomAngle + 48f, randomAngle - 48f,
                randomAngle + 80f, randomAngle - 80f]
            : [0f, 30f, -30f, 60f, -60f, 90f, -90f, 120f, -120f, 150f, -150f, 180f];
        float[] extraRadii = randomizedRetreat
            ? [Random.Shared.NextSingle() * 4f + 2f, Random.Shared.NextSingle() * 4f + 5f]
            : [0f, 8f];
        var hitboxPadding = target.HitboxRadius + (objects.LocalPlayer?.HitboxRadius ?? 0f);
        var centerRadius = clearance + hitboxPadding;
        var minimumCenterDistance = ActiveDistanceProfile.EmergencyDistance + hitboxPadding + 3f;
        var accepted = new List<Vector3>();

        if (preferCrowd)
        {
            var clusters = DetectPlayerClusters(target);
            log.Information("Detected {ClusterCount} player clusters near {Mark}", clusters.Count, target.Name.TextValue);

            var crowdCandidates = new List<(ParkingCandidate Candidate, float Score)>();
            foreach (var cluster in clusters)
            {
                var towardCrowd = cluster.Center - target.Position;
                towardCrowd.Y = 0;
                if (towardCrowd.LengthSquared() < 0.01f)
                    continue;
                towardCrowd = Vector3.Normalize(towardCrowd);
                var tangent = new Vector3(-towardCrowd.Z, 0f, towardCrowd.X);
                var crowdCenterRadius = HorizontalDistance(cluster.Center, target.Position);

                // Park beside the crowd instead of on its centroid. Safe parking is a minimum,
                // so retain the crowd's natural radius whenever it is already farther out.
                float[] lateralOffsets = randomizedRetreat
                    ? Enumerable.Range(0, 4)
                        .Select(_ => (Random.Shared.NextSingle() * 6f + 4f) *
                                     (Random.Shared.Next(2) == 0 ? -1f : 1f))
                        .ToArray()
                    : [6f, -6f, 10f, -10f];
                float[] radialOffsets = randomizedRetreat
                    ? Enumerable.Range(0, 4).Select(_ => Random.Shared.NextSingle() * 6f - 1f).ToArray()
                    : [2f, 2f, 5f, 5f];
                for (var offsetIndex = 0; offsetIndex < lateralOffsets.Length; offsetIndex++)
                {
                    var radialDistance = MathF.Max(centerRadius + 1f,
                        crowdCenterRadius + radialOffsets[offsetIndex]);
                    var candidate = target.Position + towardCrowd * radialDistance +
                                    tangent * lateralOffsets[offsetIndex];
                    candidate.Y = 1024f;
                    var projected = vnav.PointOnFloorSafe(candidate, 12f);
                    if (projected is null ||
                        ClearanceAtPoint(projected.Value, target) < clearance - 0.5f ||
                        HorizontalDistance(projected.Value, cluster.Center) > CrowdRevalidationRadius ||
                        crowdCandidates.Any(existing => HorizontalDistance(existing.Candidate.Position, projected.Value) < 2f))
                        continue;

                    var score = cluster.Population * 1000f -
                                cluster.Tightness * 25f -
                                HorizontalDistance(projected.Value, cluster.Center) * 3f -
                                HorizontalDistance(player, projected.Value) * 0.25f -
                                MathF.Abs(lateralOffsets[offsetIndex]);
                    crowdCandidates.Add((
                        new ParkingCandidate(projected.Value, true, cluster.Population, cluster.Center,
                            true, randomizedRetreat), score));
                }
            }

            foreach (var entry in crowdCandidates.OrderByDescending(entry => entry.Score))
            {
                accepted.Add(entry.Candidate.Position);
                parkingCandidates.Enqueue(entry.Candidate);
            }

            if (crowdCandidates.Count == 0)
            {
                crowdFallbackAnnounced = true;
                log.Information("No safe crowd candidate; using standard parking");
            }
        }

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
                if (projected is null ||
                    HorizontalDistance(projected.Value, target.Position) < minimumCenterDistance ||
                    ClearanceAtPoint(projected.Value, target) < clearance - 0.5f)
                    continue;
                if (accepted.Any(point => HorizontalDistance(point, projected.Value) < 2f))
                    continue;
                accepted.Add(projected.Value);
                parkingCandidates.Enqueue(new ParkingCandidate(projected.Value, false, 0, Vector3.Zero,
                    randomizedRetreat, randomizedRetreat));
            }
        }
        if (randomizedRetreat)
            log.Information("Randomized offset: accepted {Count} protected post-tag retreat candidate(s)",
                parkingCandidates.Count);
    }

    private bool TryStartNextParkingRoute(bool fly, IBattleChara target)
    {
        while (parkingCandidates.Count > 0)
        {
            var candidate = parkingCandidates.Dequeue();
            if (!candidate.RequiresProtectedRoute)
            {
                if (!crowdFallbackAnnounced)
                {
                    crowdFallbackAnnounced = true;
                    log.Information("No safe crowd candidate; using standard parking");
                }
                if (!vnav.MoveToSafe(candidate.Position, fly))
                    continue;
                selectedParkingCandidate = candidate;
                safePoint = candidate.Position;
                selectedParkingPath = null;
                return true;
            }

            var protectedRadius = ProtectedCenterRadius(target);
            if (!ProtectedSegmentIsSafe(PlayerPosition(), candidate.Position, target.Position, protectedRadius,
                    candidate.IsRandomizedRetreat))
            {
                var rejection = candidate.IsRandomizedRetreat
                    ? "Retreat candidate rejected: crosses safety radius"
                    : "Rejected crowd candidate: route crosses mark safety radius";
                log.Information("{Rejection}", rejection);
                status = rejection;
                continue;
            }

            var startDistance = HorizontalDistance(PlayerPosition(), target.Position);
            var queryAvoidRadius = candidate.IsRandomizedRetreat && startDistance < protectedRadius
                ? MathF.Max(1f, startDistance - 1f)
                : protectedRadius;
            var pathTask = vnav.PathfindAvoidSafe(
                PlayerPosition(), candidate.Position, fly, target.Position, queryAvoidRadius);
            if (pathTask is null)
            {
                log.Information(candidate.IsRandomizedRetreat
                    ? "Retreat candidate rejected: vnavmesh could not start a protected path query"
                    : "Rejected crowd candidate: vnavmesh could not start a protected path query");
                continue;
            }

            selectedParkingCandidate = candidate;
            parkingPathTask = pathTask;
            parkingPathUsesFlight = fly;
            parkingPathStartedUtc = DateTime.UtcNow;
            safePoint = null;
            selectedParkingPath = null;
            status = candidate.IsRandomizedRetreat
                ? "Validating randomized post-tag retreat route"
                : $"Validating a protected route toward a {candidate.CrowdPopulation}-player crowd";
            return true;
        }
        safePoint = null;
        selectedParkingCandidate = null;
        parkingPathTask = null;
        selectedParkingPath = null;
        return false;
    }

    private void PollCrowdParkingPath(IBattleChara target, DateTime now)
    {
        var task = parkingPathTask;
        var candidate = selectedParkingCandidate;
        if (task is null || candidate is null || !candidate.RequiresProtectedRoute)
            return;

        if (!task.IsCompleted)
        {
            if ((now - parkingPathStartedUtc).TotalSeconds <= CrowdPathQueryTimeoutSeconds)
            {
                status = candidate.IsRandomizedRetreat
                    ? "Validating randomized post-tag retreat route"
                    : $"Validating a protected route toward a {candidate.CrowdPopulation}-player crowd";
                return;
            }

            parkingPathTask = null;
            selectedParkingCandidate = null;
            log.Information(candidate.IsRandomizedRetreat
                ? "Retreat candidate rejected: protected vnavmesh path query timed out"
                : "Rejected crowd candidate: protected vnavmesh path query timed out");
            ContinueParkingAfterCrowdRejection(target, now);
            return;
        }

        List<Vector3>? path = null;
        try
        {
            if (task.IsCompletedSuccessfully)
                path = task.Result;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Protected parking path query failed");
        }

        parkingPathTask = null;
        if (path is null || path.Count == 0)
        {
            selectedParkingCandidate = null;
            log.Information(candidate.IsRandomizedRetreat
                ? "Retreat candidate rejected: vnavmesh found no protected route"
                : "Rejected crowd candidate: vnavmesh found no protected route");
            ContinueParkingAfterCrowdRejection(target, now);
            return;
        }

        var protectedRadius = ProtectedCenterRadius(target);
        if (!ProtectedSegmentIsSafe(PlayerPosition(), candidate.Position, target.Position, protectedRadius,
                candidate.IsRandomizedRetreat) ||
            !PathStaysOutsideProtectedRadius(PlayerPosition(), path, target.Position, protectedRadius,
                candidate.IsRandomizedRetreat))
        {
            selectedParkingCandidate = null;
            var rejection = candidate.IsRandomizedRetreat
                ? "Retreat candidate rejected: crosses safety radius"
                : "Rejected crowd candidate: route crosses mark safety radius";
            log.Information("{Rejection}", rejection);
            status = rejection;
            ContinueParkingAfterCrowdRejection(target, now);
            return;
        }

        if (ClearanceAtPoint(candidate.Position, target) < ActiveDistanceProfile.WaitingDistance - 0.5f ||
            !vnav.MovePathSafe(path, parkingPathUsesFlight))
        {
            selectedParkingCandidate = null;
            log.Information(candidate.IsRandomizedRetreat
                ? "Retreat candidate rejected: destination or protected route became unavailable"
                : "Rejected crowd candidate: destination or protected route became unavailable");
            ContinueParkingAfterCrowdRejection(target, now);
            return;
        }

        safePoint = candidate.Position;
        selectedParkingPath = path;
        parkingPathStartedUtc = DateTime.MinValue;
        var clearance = ClearanceAtPoint(candidate.Position, target);
        if (candidate.IsRandomizedRetreat)
        {
            status = $"Post-tag retreat target: {clearance:0}y from mark";
            log.Information("Post-tag retreat: {CandidateType} candidate selected; target {Clearance:0}y from mark",
                candidate.IsCrowd ? "crowd" : "standard", clearance);
        }
        else
        {
            status = $"Selected crowd parking candidate: {candidate.CrowdPopulation} players, {clearance:0}y from mark";
            log.Information("Selected crowd parking candidate: {Players} players, {Clearance:0}y from mark",
                candidate.CrowdPopulation, clearance);
        }
    }

    private void ContinueParkingAfterCrowdRejection(IBattleChara target, DateTime now)
    {
        var fly = parkingPathUsesFlight;
        safePoint = null;
        selectedParkingPath = null;
        parkingPathStartedUtc = DateTime.MinValue;
        if (TryStartNextParkingRoute(fly, target))
        {
            stateSinceUtc = now;
            return;
        }

        vnav.StopSafe();
        if (postTagRetreatActive)
        {
            postTagRetreatActive = false;
            SetState(SentinelState.SafeWait,
                "No protected randomized retreat route is currently reachable; holding position safely");
            return;
        }
        nextActionUtc = now.AddSeconds(3);
        SetState(SentinelState.LocateMark,
            "No crowd or standard parking route is currently reachable; keeping the hunt active and retrying");
    }

    private bool RevalidateParkingForLanding(IBattleChara target, out string reason)
    {
        var candidate = selectedParkingCandidate;
        if (candidate is null || safePoint is null)
        {
            reason = "Parking destination was lost before landing; resampling safely";
            return false;
        }

        if (ClearanceAtPoint(candidate.Position, target) < ActiveDistanceProfile.WaitingDistance - 0.5f ||
            ClearanceFromMark(target) < ActiveDistanceProfile.EmergencyDistance)
        {
            reason = "Mark movement invalidated the configured parking clearance; resampling safely";
            return false;
        }

        if (!candidate.IsCrowd)
        {
            reason = string.Empty;
            return true;
        }

        var protectedRadius = ProtectedCenterRadius(target);
        if (HorizontalSegmentDistance(PlayerPosition(), candidate.Position, target.Position) < protectedRadius ||
            !FinalApproachStaysOutsideProtectedRadius(selectedParkingPath, target.Position, protectedRadius))
        {
            reason = "Crowd parking final approach now crosses the mark safety radius; resampling safely";
            return false;
        }

        var liveCluster = DetectPlayerClusters(target)
            .OrderBy(cluster => HorizontalDistance(cluster.Center, candidate.CrowdCenter))
            .FirstOrDefault();
        if (liveCluster is null ||
            HorizontalDistance(liveCluster.Center, candidate.CrowdCenter) > CrowdMovementTolerance ||
            HorizontalDistance(liveCluster.Center, candidate.Position) > CrowdRevalidationRadius)
        {
            reason = "Crowd positions changed before landing; resampling safely";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private void RestartSafeParkingAfterRevalidation(IBattleChara target, string reason)
    {
        vnav.StopSafe();
        log.Information("{Reason}", reason);
        status = reason;
        BeginSafeParking(target, true);
    }

    private List<PlayerCluster> DetectPlayerClusters(IBattleChara target) =>
        DetectPlayerClusters(target.Position);

    private List<PlayerCluster> DetectPlayerClusters(Vector3 centerPoint)
    {
        var localPlayer = objects.LocalPlayer;
        if (localPlayer is null)
            return [];

        var players = objects.OfType<IPlayerCharacter>()
            .Where(player => player.GameObjectId != localPlayer.GameObjectId &&
                             !player.IsDead &&
                             HorizontalDistance(player.Position, centerPoint) <= CrowdSearchRadius)
            .Select(player => player.Position)
            .ToArray();
        var visited = new bool[players.Length];
        var clusters = new List<PlayerCluster>();

        for (var start = 0; start < players.Length; start++)
        {
            if (visited[start])
                continue;

            var members = new List<Vector3>();
            var pending = new Queue<int>();
            pending.Enqueue(start);
            visited[start] = true;
            while (pending.Count > 0)
            {
                var index = pending.Dequeue();
                members.Add(players[index]);
                for (var other = 0; other < players.Length; other++)
                {
                    if (!visited[other] &&
                        HorizontalDistance(players[index], players[other]) <= CrowdClusterLinkDistance)
                    {
                        visited[other] = true;
                        pending.Enqueue(other);
                    }
                }
            }

            if (members.Count < CrowdMinimumPlayers)
                continue;
            var center = new Vector3(
                members.Average(point => point.X),
                members.Average(point => point.Y),
                members.Average(point => point.Z));
            var tightness = MathF.Sqrt(members.Average(point =>
            {
                var distance = HorizontalDistance(point, center);
                return distance * distance;
            }));
            clusters.Add(new PlayerCluster(center, members.Count, tightness));
        }

        return clusters
            .OrderByDescending(cluster => cluster.Population)
            .ThenBy(cluster => cluster.Tightness)
            .ToList();
    }

    private float ClearanceAtPoint(Vector3 point, IBattleChara target)
    {
        var playerRadius = objects.LocalPlayer?.HitboxRadius ?? 0f;
        return MathF.Max(0f,
            HorizontalDistance(point, target.Position) - target.HitboxRadius - playerRadius);
    }

    private float ProtectedCenterRadius(IBattleChara target)
    {
        var playerRadius = objects.LocalPlayer?.HitboxRadius ?? 0f;
        return ActiveDistanceProfile.EmergencyDistance + target.HitboxRadius + playerRadius + 3f;
    }

    private static bool PathStaysOutsideProtectedRadius(
        Vector3 start,
        IReadOnlyList<Vector3> path,
        Vector3 protectedCenter,
        float protectedRadius,
        bool allowOutwardEscape = false)
    {
        var previous = start;
        var previousDistance = HorizontalDistance(start, protectedCenter);
        var escaping = allowOutwardEscape && previousDistance < protectedRadius;
        foreach (var waypoint in path)
        {
            var requiredRadius = escaping ? MathF.Max(0f, previousDistance - 0.75f) : protectedRadius;
            if (HorizontalSegmentDistance(previous, waypoint, protectedCenter) < requiredRadius)
                return false;
            var waypointDistance = HorizontalDistance(waypoint, protectedCenter);
            if (escaping && waypointDistance + 0.75f < previousDistance)
                return false;
            if (escaping && waypointDistance >= protectedRadius)
                escaping = false;
            previousDistance = waypointDistance;
            previous = waypoint;
        }
        return true;
    }

    private static bool ProtectedSegmentIsSafe(
        Vector3 start,
        Vector3 end,
        Vector3 protectedCenter,
        float protectedRadius,
        bool allowOutwardEscape)
    {
        var startDistance = HorizontalDistance(start, protectedCenter);
        if (!allowOutwardEscape || startDistance >= protectedRadius)
            return HorizontalSegmentDistance(start, end, protectedCenter) >= protectedRadius;
        var endDistance = HorizontalDistance(end, protectedCenter);
        return endDistance > startDistance &&
               HorizontalSegmentDistance(start, end, protectedCenter) >= MathF.Max(0f, startDistance - 0.75f);
    }

    private static bool FinalApproachStaysOutsideProtectedRadius(
        IReadOnlyList<Vector3>? path,
        Vector3 protectedCenter,
        float protectedRadius)
    {
        if (path is null || path.Count == 0)
            return false;
        var start = path.Count >= 2 ? path[^2] : path[0];
        return HorizontalSegmentDistance(start, path[^1], protectedCenter) >= protectedRadius;
    }

    private static float HorizontalSegmentDistance(Vector3 start, Vector3 end, Vector3 point)
    {
        var segmentX = end.X - start.X;
        var segmentZ = end.Z - start.Z;
        var lengthSquared = segmentX * segmentX + segmentZ * segmentZ;
        if (lengthSquared < 0.0001f)
            return HorizontalDistance(start, point);
        var t = ((point.X - start.X) * segmentX + (point.Z - start.Z) * segmentZ) / lengthSquared;
        t = Math.Clamp(t, 0f, 1f);
        var closest = new Vector3(start.X + segmentX * t, point.Y, start.Z + segmentZ * t);
        return HorizontalDistance(closest, point);
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
            ResetSsStagingTracking();
            log.Information("SS precursor event detected: {Ss}; reason={Reason}", profile.SsName, reason);
        }
        if (state == SentinelState.PostKillSsGrace)
            SetState(SentinelState.SsWatch,
                $"{reason}; staging at the fixed {profile.SsName} spawn location");
        status = $"{reason}; navigating to {profile.SsName} staging without targeting {profile.PrecursorName}";
    }

    private void ConfirmKill(string reason)
    {
        if (current is null || killConfirmed)
            return;
        killConfirmed = true;
        pullResetCandidateSinceUtc = DateTime.MinValue;
        discardAtUldah = false;
        discardReason = string.Empty;
        vnav.StopSafe();
        var now = DateTime.UtcNow;
        MarkKilled(current, now);
        log.Information("Hunt cleared/completed with positive evidence: {Mark} on {World}; reason={Reason}",
            current.CreatureName, current.World, reason);
        if (HuntCatalog.IsSupportedNormalS(current.TerritoryId, current.CreatureName) &&
            clientState.TerritoryType == current.TerritoryId &&
            travel.CurrentWorld.Equals(current.World, StringComparison.OrdinalIgnoreCase))
        {
            activeSsProfile = HuntCatalog.GetSsProfileForTerritory(current.TerritoryId);
            ssChainObserved = false;
            ssSpawnAnnounced = false;
            postKillSsGraceDeadlineUtc = now.AddSeconds(config.PostKillSsGraceSeconds);
            ssWatchDeadlineUtc = DateTime.MinValue;
            ResetSsStagingTracking();
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
        discardReason = reason;
        nextActionUtc = DateTime.UtcNow;
        log.Warning("Abandoning active hunt {Mark} without confirmed kill only after explicit failure: {Reason}",
            current?.CreatureName ?? "(none)", reason);
        SetState(SentinelState.ResetToUldah, $"{reason}; discarding this alert after the Ul'dah reset");
    }

    private void ClearCurrent()
    {
        vnav.StopSafe();
        current = null;
        mark = null;
        alertPoint = null;
        approachPoint = null;
        safePoint = null;
        selectedParkingCandidate = null;
        parkingPathTask = null;
        parkingPathStartedUtc = DateTime.MinValue;
        selectedParkingPath = null;
        crowdFallbackAnnounced = false;
        territoryAetheryteId = 0;
        killConfirmed = false;
        tagAttempted = false;
        postTagRetreatActive = false;
        markEverIdentified = false;
        markCombatObserved = false;
        pullCycleCombatObserved = false;
        pullCycleTagged = false;
        identifiedMarkGameObjectId = 0;
        pullCycle = 1;
        pullResetCandidateSinceUtc = DateTime.MinValue;
        activeTagActionId = 0;
        discardAtUldah = false;
        discardReason = string.Empty;
        ssChainObserved = false;
        ssSpawnAnnounced = false;
        activeSsProfile = null;
        postKillSsGraceDeadlineUtc = DateTime.MinValue;
        ssWatchDeadlineUtc = DateTime.MinValue;
        ResetSsStagingTracking();
        playerReadySinceUtc = DateTime.MinValue;
        lastMarkSeenUtc = DateTime.MinValue;
        ResetReturnRecoveryTracking();
        ResetIncidentalAggroTracking();
        parkingCandidates.Clear();
    }

    private bool CanResolveMarkInState() => state is
        SentinelState.LocateMark or
        SentinelState.MoveToSafePoint or
        SentinelState.Landing or
        SentinelState.SafeWait or
        SentinelState.TagApproach or
        SentinelState.GroundRetreat or
        SentinelState.AvoidIncidentalAggro;

    private void MarkWasIdentified(IBattleChara target)
    {
        mark = target;
        var firstIdentification = !markEverIdentified;
        markEverIdentified = true;
        if (identifiedMarkGameObjectId == 0)
            identifiedMarkGameObjectId = target.GameObjectId;
        lastMarkSeenUtc = DateTime.UtcNow;
        if (firstIdentification)
            log.Information(
                "Entity first positively identified: {Mark}, object={ObjectId}, world={World}, territory={Territory}",
                target.Name.TextValue, target.GameObjectId, travel.CurrentWorld, clientState.TerritoryType);
        if (CombatController.IsMarkInCombat(target))
        {
            markCombatObserved = true;
            if (!pullCycleCombatObserved)
            {
                pullCycleCombatObserved = true;
                log.Information("Combat first observed for {Mark} in pull cycle {PullCycle}",
                    target.Name.TextValue, pullCycle);
            }
        }
    }

    private bool ObservePullCycleReset(IBattleChara target, DateTime now)
    {
        if (current is null || !markEverIdentified || !pullCycleCombatObserved || !pullCycleTagged ||
            !tagAttempted || killConfirmed || target.IsDead || target.CurrentHp == 0 ||
            clientState.TerritoryType != current.TerritoryId ||
            !travel.CurrentWorld.Equals(current.World, StringComparison.OrdinalIgnoreCase) ||
            identifiedMarkGameObjectId == 0 || target.GameObjectId != identifiedMarkGameObjectId)
        {
            pullResetCandidateSinceUtc = DateTime.MinValue;
            return false;
        }

        var outOfCombatAtFullHealth = !CombatController.IsMarkInCombat(target) &&
                                      CombatController.HpPercent(target) >= PullResetMinimumHpPercent;
        if (!outOfCombatAtFullHealth)
        {
            pullResetCandidateSinceUtc = DateTime.MinValue;
            return false;
        }

        if (pullResetCandidateSinceUtc == DateTime.MinValue)
        {
            pullResetCandidateSinceUtc = now;
            log.Information(
                "Reset candidate started: same {Mark} entity, previous combat={PreviousCombat}, previous tag={PreviousTag}, combat=false, HP={Hp:0.0}%, requiring {Seconds:0.0}s stable confirmation",
                target.Name.TextValue, pullCycleCombatObserved, pullCycleTagged,
                CombatController.HpPercent(target), PullResetConfirmationSeconds);
            return false;
        }

        if ((now - pullResetCandidateSinceUtc).TotalSeconds < PullResetConfirmationSeconds)
            return false;

        var completedCycle = pullCycle;
        var stableSeconds = (now - pullResetCandidateSinceUtc).TotalSeconds;
        pullCycle++;
        tagAttempted = false;
        pullCycleCombatObserved = false;
        pullCycleTagged = false;
        activeTagActionId = 0;
        postTagRetreatActive = false;
        pullResetCandidateSinceUtc = DateTime.MinValue;
        vnav.StopSafe();
        log.Information(
            "Reset confirmed: same {Mark} entity {ObjectId}, previous combat=true, previous tag=true, combat=false, HP={Hp:0.0}%, stable={Stable:0.0}s; completed pull cycle {PullCycle}",
            target.Name.TextValue, target.GameObjectId, CombatController.HpPercent(target),
            stableSeconds, completedCycle);
        log.Information("Tag gate re-armed for pull cycle {PullCycle}", pullCycle);

        if (ClearanceFromMark(target) < ActiveDistanceProfile.WaitingDistance || state != SentinelState.SafeWait)
        {
            BeginSafeParking(target, fly: false);
            status = $"Reset detected: HP restored and combat ended; tag gate re-armed for pull cycle {pullCycle} and returning to safe parking";
        }
        else
        {
            SetState(SentinelState.SafeWait,
                $"Reset detected: HP restored and combat ended; tag gate re-armed for pull cycle {pullCycle}");
        }
        return true;
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
        var unresolvedRemoved = unresolvedFaloopAlerts
            .Where(pair => HuntCatalog.TextMentionsMark(sonarText, pair.Value.Alert.CreatureName) &&
                           (string.IsNullOrWhiteSpace(killedWorld)
                               ? pair.Value.Alert.World.Equals(travel.CurrentWorld, StringComparison.OrdinalIgnoreCase)
                               : pair.Value.Alert.World.Equals(killedWorld, StringComparison.OrdinalIgnoreCase)))
            .Select(pair => pair.Key).ToArray();
        if (removed.Length == 0 && unresolvedRemoved.Length == 0)
            return;

        if (removed.Length > 0)
        {
            var removedKeys = removed.Select(alert => alert.Key).ToHashSet(StringComparer.Ordinal);
            var survivors = pendingAlerts.Where(alert => !removedKeys.Contains(alert.Key)).ToArray();
            pendingAlerts.Clear();
            foreach (var alert in survivors)
                pendingAlerts.Enqueue(alert);
        }
        var now = DateTime.UtcNow;
        foreach (var alert in removed)
            killedAlerts[alert.Key] = now;
        foreach (var key in unresolvedRemoved)
            unresolvedFaloopAlerts.Remove(key);
        PersistQueue();
        status = $"Removed {removed.Length + unresolvedRemoved.Length} queued/pending hunt(s) already reported killed";
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
        var unresolvedRemoved = unresolvedFaloopAlerts.Where(pair => Matches(pair.Value.Alert)).Select(pair => pair.Key).ToArray();
        foreach (var key in unresolvedRemoved)
            unresolvedFaloopAlerts.Remove(key);
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

        if (removed.Length > 0 || unresolvedRemoved.Length > 0)
            status = $"{source} removed {removed.Length + unresolvedRemoved.Length} killed queued/pending hunt(s)";
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

        ReorderPendingQueue();
        PersistQueue();
    }

    private void EnqueuePersistent(HuntAlertSnapshot alert)
    {
        pendingAlerts.Enqueue(alert);
        ReorderPendingQueue();
        PersistQueue();
        log.Information("Queued {Mark} ({Expansion}); pending order is {Order}",
            alert.CreatureName,
            HuntCatalog.ExpansionName(HuntCatalog.GetExpansion(alert.TerritoryId)),
            string.Join(" -> ", pendingAlerts.Select(candidate => candidate.CreatureName)));
    }

    private bool TryDequeueNextValid(out HuntAlertSnapshot alert)
    {
        var now = DateTime.UtcNow;
        var queued = pendingAlerts.ToArray();
        var valid = queued.Where(candidate => IsAlertFresh(candidate, now))
            .OrderByDescending(candidate => ExpansionQueuePriority(HuntCatalog.GetExpansion(candidate.TerritoryId)))
            .ThenBy(candidate => candidate.ReceivedAtUtc)
            .ToArray();
        var skipped = queued.Length - valid.Length;
        pendingAlerts.Clear();
        alert = valid.FirstOrDefault()!;
        foreach (var remaining in valid.Skip(1))
            pendingAlerts.Enqueue(remaining);
        PersistQueue();
        if (skipped > 0)
            log.Information("Skipped {Count} killed, stale, or no-longer-eligible queued alerts", skipped);
        if (alert is not null)
        {
            log.Information("Selected queued {Mark} by expansion priority ({Expansion}); {Remaining} remain",
                alert.CreatureName,
                HuntCatalog.ExpansionName(HuntCatalog.GetExpansion(alert.TerritoryId)),
                pendingAlerts.Count);
            return true;
        }
        if (skipped > 0)
            status = $"Skipped {skipped} killed, stale, or no-longer-eligible queued alert(s)";
        return false;
    }

    private void ReorderPendingQueue()
    {
        var ordered = pendingAlerts
            .OrderByDescending(alert => ExpansionQueuePriority(HuntCatalog.GetExpansion(alert.TerritoryId)))
            .ThenBy(alert => alert.ReceivedAtUtc)
            .ToArray();
        pendingAlerts.Clear();
        foreach (var alert in ordered)
            pendingAlerts.Enqueue(alert);
    }

    private static int ExpansionQueuePriority(SupportedExpansion expansion) => expansion switch
    {
        SupportedExpansion.Evercold => 5,
        SupportedExpansion.Dawntrail => 4,
        SupportedExpansion.Endwalker => 3,
        SupportedExpansion.Shadowbringers => 2,
        SupportedExpansion.Centurio => 1,
        _ => 0,
    };

    private bool IsAlertFresh(HuntAlertSnapshot alert, DateTime now) =>
        IsWithinFreshnessWindow(alert.ReceivedAtUtc, now) &&
        !killedAlerts.ContainsKey(alert.Key) &&
        travel.IsSameDataCenter(alert.World) &&
        HuntCatalog.IsSupportedTerritory(alert.TerritoryId) &&
        config.IsExpansionEnabled(HuntCatalog.GetExpansion(alert.TerritoryId)) &&
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

    private void PruneUnresolvedFaloopAlerts()
    {
        var now = DateTime.UtcNow;
        foreach (var key in unresolvedFaloopAlerts
                     .Where(pair => !IsWithinFreshnessWindow(pair.Value.Alert.ReceivedAtUtc, now) ||
                                    !config.IsExpansionEnabled(HuntCatalog.GetExpansion(pair.Value.Alert.TerritoryId)))
                     .Select(pair => pair.Key).ToArray())
            unresolvedFaloopAlerts.Remove(key);
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

    private void RemoveDisabledQueuedAlerts()
    {
        var survivors = pendingAlerts
            .Where(alert => config.IsExpansionEnabled(HuntCatalog.GetExpansion(alert.TerritoryId)))
            .ToArray();
        if (survivors.Length == pendingAlerts.Count)
            return;

        var removed = pendingAlerts.Count - survivors.Length;
        pendingAlerts.Clear();
        foreach (var alert in survivors)
            pendingAlerts.Enqueue(alert);
        PersistQueue();
        status = $"Removed {removed} queued hunt(s) from disabled expansions";
    }

    private HuntDistanceProfile ActiveDistanceProfile => config.GetDistanceProfile(
        current is null ? SupportedExpansion.Shadowbringers : HuntCatalog.GetExpansion(current.TerritoryId));

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
        ImGui.SetNextWindowSize(new Vector2(680, 880), ImGuiCond.FirstUseEver);
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
                StartFaloopWithSavedAuthentication();
            }
            config.Save();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("STANDALONE S-RANK ORCHESTRATOR");
        ImGui.TextWrapped("Faloop, HuntAlerts, and Sonar supply alerts only. Sentinel owns Ul'dah reset, World Visit, teleport, instance selection, safe vnavmesh movement, one gated ranged tag, expansion-specific SS watch, and post-kill recovery.");
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
                StartFaloopWithSavedAuthentication();
            else
                faloop.Stop("Direct Faloop feed disabled");
            config.Save();
        }
        ImGui.TextWrapped($"Faloop: {faloop.Status}");
        if (faloop.LastMessageUtc != DateTime.MinValue)
            ImGui.TextWrapped($"Last socket packet: {(DateTime.UtcNow - faloop.LastMessageUtc).TotalSeconds:0}s ago");
        if (faloop.LastRawFeedMessageUtc != DateTime.MinValue)
            ImGui.TextWrapped($"Last raw Faloop message: {(DateTime.UtcNow - faloop.LastRawFeedMessageUtc).TotalSeconds:0}s ago");
        if (faloop.LastEventUtc != DateTime.MinValue)
            ImGui.TextWrapped($"Last recognized hunt event: {(DateTime.UtcNow - faloop.LastEventUtc).TotalSeconds:0}s ago");
        ImGui.TextWrapped($"Last raw event: {faloop.LastRawEventSummary}");
        if (!string.Equals(faloop.LastRejectedEventReason, "None", StringComparison.Ordinal))
            ImGui.TextWrapped($"Last raw hunt rejection: {faloop.LastRejectedEventReason}");
        ImGui.TextWrapped(lastFaloopDecisionUtc == DateTime.MinValue
            ? $"Pipeline: {lastFaloopDecision}"
            : $"Pipeline ({(DateTime.UtcNow - lastFaloopDecisionUtc).TotalSeconds:0}s ago): {lastFaloopDecision}");
        ImGui.SetNextItemWidth(250f);
        ImGui.InputText("Faloop username", ref faloopUsername, 128);
        ImGui.SetNextItemWidth(250f);
        ImGui.InputText("Faloop password (never stored as plaintext)", ref faloopPassword, 256,
            ImGuiInputTextFlags.Password);
        var rememberFaloopLogin = config.RememberFaloopLogin;
        if (ImGui.Checkbox("Remember Faloop login on this PC", ref rememberFaloopLogin))
        {
            config.RememberFaloopLogin = rememberFaloopLogin;
            if (!rememberFaloopLogin)
                config.FaloopProtectedPassword = string.Empty;
            config.Save();
        }
        if (config.RememberFaloopLogin)
        {
            ImGui.TextWrapped(string.IsNullOrWhiteSpace(config.FaloopProtectedPassword)
                ? "Authenticate once to save a Windows-protected login."
                : "Remembered login is protected by Windows for this user on this PC.");
        }
        if (ImGui.Button(faloopLoginTask is { IsCompleted: false } ? "Authenticating..." : "Authenticate / refresh session") &&
            faloopLoginTask is not { IsCompleted: false })
            BeginFaloopLogin();
        if (!string.IsNullOrWhiteSpace(faloopLoginStatus))
            ImGui.TextWrapped(faloopLoginStatus);
        if (!string.IsNullOrWhiteSpace(config.FaloopSessionId) ||
            !string.IsNullOrWhiteSpace(config.FaloopUsername) ||
            !string.IsNullOrWhiteSpace(config.FaloopProtectedPassword))
        {
            ImGui.SameLine();
            if (ImGui.Button("Forget saved session/login"))
                ForgetFaloopLogin();
        }
        ImGui.TextWrapped("The session and username remain in the normal plugin configuration. Optional remembered passwords are stored only as Windows CurrentUser DPAPI ciphertext and are never logged or serialized as plaintext. Expansion eligibility is enforced locally before queueing or travel, regardless of website filters.");

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
        ImGui.TextUnformatted("EXPANSION HUNTING");
        var expansionChanged = false;
        var centurio = config.EnableCenturio;
        if (ImGui.Checkbox("Centurio (ARR / HW / SB)", ref centurio))
        {
            config.EnableCenturio = centurio;
            expansionChanged = true;
        }
        var shadowbringers = config.EnableShadowbringers;
        if (ImGui.Checkbox("Shadowbringers", ref shadowbringers))
        {
            config.EnableShadowbringers = shadowbringers;
            expansionChanged = true;
        }
        var endwalker = config.EnableEndwalker;
        if (ImGui.Checkbox("Endwalker", ref endwalker))
        {
            config.EnableEndwalker = endwalker;
            expansionChanged = true;
        }
        var dawntrail = config.EnableDawntrail;
        if (ImGui.Checkbox("Dawntrail", ref dawntrail))
        {
            config.EnableDawntrail = dawntrail;
            expansionChanged = true;
        }
        var evercold = false;
        ImGui.BeginDisabled();
        ImGui.Checkbox("Evercold", ref evercold);
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextUnformatted("Future placeholder — hunt data not available");
        if (expansionChanged)
        {
            RemoveDisabledQueuedAlerts();
            config.Save();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("DISTANCE PROFILES");
        ImGui.TextWrapped("Behavior profiles are independent from the expansion hunting checkboxes.");
        DrawDistanceProfile(
            "Close-safe profile — Centurio + Shadowbringers",
            "close-safe",
            config.CloseSafeProfile,
            25f,
            5f,
            5f);
        DrawDistanceProfile(
            "Proximity-sensitive profile — Endwalker + Dawntrail (Evercold future)",
            "proximity-sensitive",
            config.ProximitySensitiveProfile,
            35f,
            20f,
            15f);
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
        ImGui.TextWrapped("Safety gates: the active S/SS mark itself must already be in combat and at/below the configured HP threshold. Sentinel targets it, attempts one job-appropriate ranged action, closes the attack gate for that pull cycle, and never runs a rotation. The gate re-arms only after the same living mark remains out of combat at restored health long enough to prove a genuine reset. A missing entity or failed route never means cleared; only a positive death event/message or a visibly dead identified mark can complete the hunt. Forgiven Gossip, Ker Shroud, and Crystal Incarnation precursors are observation-only and are never targeted, approached, or attacked.");
        ImGui.End();
    }

    private static float DrawFloat(string label, float value, float min, float max, string format = "%.0f y")
    {
        return ImGui.SliderFloat(label, ref value, min, max, format)
            ? MathF.Round(value)
            : value;
    }

    private static void DrawDistanceProfile(
        string heading,
        string id,
        HuntDistanceProfile profile,
        float flagMinimum,
        float safeMinimum,
        float emergencyMinimum)
    {
        ImGui.Spacing();
        ImGui.TextUnformatted(heading);
        profile.FlagApproachDistance = DrawFloat($"Initial coordinate stop##{id}",
            profile.FlagApproachDistance, flagMinimum, 90f);
        profile.WaitingDistance = DrawFloat($"Safe parking clearance##{id}",
            profile.WaitingDistance, safeMinimum, 70f);
        var emergencyMaximum = Math.Min(50f, profile.WaitingDistance);
        profile.EmergencyDistance = DrawFloat($"Emergency clearance##{id}",
            Math.Min(profile.EmergencyDistance, emergencyMaximum), emergencyMinimum, emergencyMaximum);
        profile.EngageHpPercent = DrawFloat($"Engage only at/below HP %##{id}",
            profile.EngageHpPercent, 1f, 99f, "%.0f%%");
        profile.EnforceClearanceInvariant();
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
        OpenHinterlandsGateway,
        SelectHinterlandsGateway,
        SelectHinterlandsDestination,
        WaitForHinterlands,
        ChangeInstance,
        SelectInstance,
        WaitForInstance,
        WaitForPlayerReady,
        WaitForMesh,
        PrepareApproachDestination,
        ApproachAlertCoordinates,
        LocateMark,
        MoveToSafePoint,
        Landing,
        SafeWait,
        TagApproach,
        GroundRetreat,
        AvoidIncidentalAggro,
        PostKillSsGrace,
        SsWatch,
    }
}

internal sealed record ParkingCandidate(
    Vector3 Position,
    bool IsCrowd,
    int CrowdPopulation,
    Vector3 CrowdCenter,
    bool RequiresProtectedRoute,
    bool IsRandomizedRetreat);

internal sealed record PlayerCluster(
    Vector3 Center,
    int Population,
    float Tightness);

internal sealed record SsStagingCandidate(
    Vector3 Position,
    bool IsCrowd,
    int CrowdPopulation,
    Vector3 CrowdCenter);

internal sealed record TerritoryAetheryteOverride(
    uint AetheryteId,
    string TerritoryName,
    string AetheryteName);

internal sealed class PendingFaloopLocation(
    HuntAlertSnapshot alert,
    FaloopFeedEvent feedEvent,
    string dataCenterSlug,
    DateTime firstObservedUtc)
{
    public HuntAlertSnapshot Alert { get; } = alert;
    public FaloopFeedEvent FeedEvent { get; set; } = feedEvent;
    public string DataCenterSlug { get; } = dataCenterSlug;
    public DateTime FirstObservedUtc { get; } = firstObservedUtc;
    public DateTime NextAttemptUtc { get; set; } = firstObservedUtc;
    public int Attempts { get; set; }
    public string LastDetail { get; set; } = "not attempted yet";
    public Task<FaloopLocationEnrichmentResult>? ActiveTask { get; set; }
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
