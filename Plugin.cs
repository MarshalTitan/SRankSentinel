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
    private const double ReturnTeleportRetrySeconds = 3;
    private const double ReturnTransitionTimeoutSeconds = 45;
    private const double ReturnRecoveryWatchdogSeconds = 120;
    private const double DeadPostKillRewardGraceSeconds = 2.0;
    private const float CrowdSearchRadius = 90f;
    private const float CrowdClusterLinkDistance = 14f;
    private const float CrowdRevalidationRadius = 18f;
    private const float CrowdMovementTolerance = 10f;
    private const int CrowdMinimumPlayers = 3;
    private const double CrowdPathQueryTimeoutSeconds = 20;
    private const float ParkingMaximumVerticalSeparation = 6f;
    private const double ParkingRouteStallSeconds = 12;
    private const float ParkingMeaningfulProgressDistance = 1f;
    private const double LandingAttemptTimeoutSeconds = 10;
    private const int MaximumParkingRecoveryFailures = 6;
    private const double ParkingRecoveryBudgetSeconds = 90;
    private const float ParkingPreferredClearanceTolerance = 3f;
    private const float TagApproachClearance = 8f;
    private const double TagDispatchConfirmationTimeoutSeconds = 5;
    private const double TagRecoveryBudgetSeconds = 120;
    private const double TagRouteStallSeconds = 8;
    private const double TagEntityReacquireSeconds = 5;
    private const float MaximumParkingClearance = 35f;
    private const float MaximumGroundPathDetourRatio = 2.5f;
    private const float MaximumGroundPathDetourAllowance = 30f;
    private const float MaximumGroundPathEndpointGap = 12f;
    private const double ReturnLandingDirectAttemptSeconds = 4;
    private const double ReturnLandingRouteStallSeconds = 10;
    private const int ReturnTeleportAttemptsBeforeLandingFallback = 2;
    private const double FaloopLocationEnrichmentTimeoutSeconds = 300;
    private const double FaloopLocationEnrichmentInitialRetrySeconds = 5;
    private const double FaloopLocationEnrichmentMaximumRetrySeconds = 30;
    private const float PullResetMinimumHpPercent = 99f;
    private const double PullResetConfirmationSeconds = 4;
    private const double IncidentalAggroClearConfirmationSeconds = 2;
    private const double IncidentalAggroRouteRetrySeconds = 6;
    private const double IncidentalAggroEscapeBudgetSeconds = 24;
    private const int IncidentalAggroEscapeAttemptLimit = 4;
    private const double IncidentalCombatActionRetrySeconds = 1;
    private const float IncidentalAggroThreatRadius = 60f;
    private const float IncidentalAggroEscapeDistance = 55f;
    private const double SsStagingPathQueryTimeoutSeconds = 20;
    private const double SsStagingRouteRetrySeconds = 3;
    private const float SsStagingArrivalDistance = 5f;
    private const float SsStagingTargetRadius = 25f;
    private const float SsStagingRadiusTolerance = 4f;
    private const float SsStagingProtectedRadius = 18f;
    private const double SsChainKillTransitionLatchSeconds = 30;
    private const string ReservedSsWatchHuntType = "sswatch";
    private const double ApproachRouteRetrySeconds = 3;
    private const double ApproachRouteStallSeconds = 15;
    private const double ApproachMovementStartGraceSeconds = 8;
    private const double ApproachSlowPathfindNoticeSeconds = 20;
    private const double ApproachPathQueryTimeoutSeconds = 45;
    private const float ApproachMeaningfulProgressDistance = 3f;
    private const int ApproachEarlyStopLimit = 3;
    private const float ApproachScanTolerance = 3f;
    private const int ProjectionFailuresBeforeApproximateRoute = 2;
    private const double LocalApproachRecoveryBudgetSeconds = 90;
    private const double FinalLocateScanSeconds = 20;
    private const double RaiseAcceptanceRetrySeconds = 2;
    private const double CompletedRaiseResolutionBudgetSeconds = 6;
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
    private readonly Dictionary<string, string> faloopReportIdsByAlertKey = new(StringComparer.Ordinal);
    private readonly Queue<ParkingCandidate> parkingCandidates = new();
    private readonly Queue<Vector3> returnLandingCandidates = new();
    private readonly Queue<SsStagingCandidate> ssStagingCandidates = new();
    private readonly Queue<Vector3> approachProjectionCandidates = new();
    private readonly Queue<Vector3> approachRouteCandidates = new();

    private bool configOpen;
    private HuntAlertSnapshot? current;
    private IBattleChara? mark;
    private Vector3? alertPoint;
    private Vector3? approachPoint;
    private Vector3? approachRouteTarget;
    private Task<List<Vector3>>? approachPathTask;
    private Vector3 approachRouteStartPosition;
    private Vector3 approachLastProgressPosition;
    private float approachRouteStartRemaining;
    private float approachBestRemaining;
    private DateTime approachRouteStartedUtc = DateTime.MinValue;
    private DateTime approachLastProgressUtc = DateTime.MinValue;
    private DateTime approachMovementSubmittedUtc = DateTime.MinValue;
    private int approachProjectionCandidateIndex;
    private int approachProjectionCandidateCount;
    private int approachEarlyStops;
    private bool approachRouteIsSegment;
    private bool approachStartingEgressActive;
    private bool approachPathfindingObserved;
    private bool approachMovementObserved;
    private bool approachSlowPathfindNoticeLogged;
    private int approachSubmittedWaypointCount;
    private int approachLastObservedWaypointCount = -1;
    private int approachEmptyProjectionPasses;
    private bool approachPointIsApproximate;
    private bool approachApproximateRecoveryUsed;
    private DateTime approachRecoveryStartedUtc = DateTime.MinValue;
    private Vector3? safePoint;
    private ParkingCandidate? selectedParkingCandidate;
    private Task<List<Vector3>>? parkingPathTask;
    private Task<List<Vector3>>? parkingGroundPathTask;
    private DateTime parkingPathStartedUtc = DateTime.MinValue;
    private List<Vector3>? pendingParkingFlightPath;
    private Vector3 parkingGroundPathGoal;
    private List<Vector3>? selectedParkingPath;
    private bool crowdFallbackAnnounced;
    private bool postTagRetreatActive;
    private bool parkingPathUsesFlight;
    private Vector3 parkingLastProgressPosition;
    private DateTime parkingLastProgressUtc = DateTime.MinValue;
    private DateTime parkingRecoveryStartedUtc = DateTime.MinValue;
    private int parkingRecoveryFailures;
    private bool parkingDeviationLogged;
    private Vector3? returnLandingPoint;
    private Vector3 returnLandingLastProgressPosition;
    private DateTime returnLandingStartedUtc = DateTime.MinValue;
    private DateTime returnLandingRouteStartedUtc = DateTime.MinValue;
    private DateTime returnLandingLastProgressUtc = DateTime.MinValue;
    private int returnLandingAttempt;
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
    private bool tagRequired;
    private DateTime tagRequiredSinceUtc = DateTime.MinValue;
    private DateTime tagEntityMissingSinceUtc = DateTime.MinValue;
    private DateTime tagLastProgressUtc = DateTime.MinValue;
    private Vector3 tagLastProgressPosition;
    private float tagBestClearance = float.MaxValue;
    private Vector3? tagLandingPoint;
    private DateTime tagLandingStartedUtc = DateTime.MinValue;
    private DateTime tagLandingLastProgressUtc = DateTime.MinValue;
    private Vector3 tagLandingLastPosition;
    private int tagRecoveryFailures;
    private int tagHpEscalationMask;
    private ulong identifiedMarkGameObjectId;
    private int pullCycle = 1;
    private DateTime pullResetCandidateSinceUtc = DateTime.MinValue;
    private uint activeTagActionId;
    private TagDispatch? pendingTagDispatch;
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
    private Task<List<Vector3>>? ssStagingGroundPathTask;
    private List<Vector3>? selectedSsStagingPath;
    private List<Vector3>? pendingSsStagingFlightPath;
    private Vector3 ssStagingGroundPathGoal;
    private DateTime ssStagingPathStartedUtc = DateTime.MinValue;
    private DateTime nextSsStagingAttemptUtc = DateTime.MinValue;
    private bool ssStagingArrived;
    private DateTime ssStagingLandingStartedUtc = DateTime.MinValue;
    private bool ssStagingProjectionFailureLogged;
    private DateTime postKillSsGraceDeadlineUtc = DateTime.MinValue;
    private DateTime ssWatchDeadlineUtc = DateTime.MinValue;
    private DateTime pendingSsChainEvidenceUtc = DateTime.MinValue;
    private string pendingSsChainAlertKey = string.Empty;
    private string pendingSsChainReason = string.Empty;
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
    private int returnTeleportAttempts;
    private bool resetToUldahAllowsLiveEntityExit;
    private HuntExitRequestSource resetToUldahRequestSource = HuntExitRequestSource.AutomaticStateTransition;
    private SentinelState resetToUldahRequestedFromState = SentinelState.Idle;
    private string resetToUldahRequestReason = string.Empty;
    private DateTime deadPostKillRewardGraceDeadlineUtc = DateTime.MinValue;
    private DateTime raiseDialogFirstSeenUtc = DateTime.MinValue;
    private DateTime raiseAcceptanceSubmittedUtc = DateTime.MinValue;
    private DateTime nextRaiseAttemptUtc = DateTime.MinValue;
    private DateTime lastRaiseDiagnosticUtc = DateTime.MinValue;
    private bool raiseDialogCloseLogged;
    private int raiseAcceptanceAttempts;
    private string lastRaisePrompt = string.Empty;
    private SentinelState incidentalAggroResumeState = SentinelState.Idle;
    private DateTime incidentalAggroStartedUtc = DateTime.MinValue;
    private DateTime incidentalAggroClearSinceUtc = DateTime.MinValue;
    private DateTime incidentalAggroLastProgressUtc = DateTime.MinValue;
    private Vector3 incidentalAggroLastPosition;
    private int incidentalAggroEscapeAttempt;
    private bool incidentalCombatFallbackActive;
    private ulong incidentalCombatTargetId;
    private DateTime nextIncidentalCombatActionUtc = DateTime.MinValue;
    private DateTime huntSearchStartedUtc = DateTime.MinValue;
    private DateTime locateWindowStartedUtc = DateTime.MinValue;
    private DateTime finalLocateScanStartedUtc = DateTime.MinValue;
    private int locateWindowCount;
    private bool finalLocateReturnStarted;
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
        vnav = new VNavmeshIpc(pi, pluginLog);
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
        var world = ResolveFaloopWorld(feedEvent.WorldSlug);
        if (!travel.TryGetDataCenterRelationship(
                world, out var currentDataCenter, out var eventDataCenter, out var isSameDataCenter))
        {
            log.Debug(
                "Ignored Faloop event before classification: data-center relationship for world {World} is not available yet",
                world);
            return;
        }
        if (!isSameDataCenter)
        {
            // Cross-DC events are irrelevant to normal World Visit. Keep this below the normal
            // UI/log level and return before territory, expansion, POI, queue, or travel work.
            log.Debug(
                "Ignored off-DC event: {World} / {EventDataCenter}; current DC is {CurrentDataCenter}",
                world, eventDataCenter, currentDataCenter);
            return;
        }

        var creature = FaloopCatalog.DisplayName(feedEvent.MobSlug);
        var hasTerritory = FaloopCatalog.TryResolveTerritory(feedEvent.ZoneSlug, out var territory);
        if (!hasTerritory && HuntCatalog.ResolveUniqueName(creature) is { } uniqueDefinition)
        {
            territory = uniqueDefinition.TerritoryId;
            hasTerritory = true;
            // Lightweight sighting_set reports may omit zoneId even though the mark identity is
            // unambiguous. Preserve the inferred territory through POI resolution/enrichment.
            feedEvent = feedEvent with { ZoneSlug = territory.ToString() };
            log.Debug(
                "Inferred territory {Territory} for {Mark} from its unique supported mark identity",
                territory, uniqueDefinition.Name);
        }

        if (feedEvent.Action == FaloopEventAction.Death)
        {
            if (!TryMatchTrackedFaloopDeath(feedEvent, world, creature,
                    hasTerritory ? territory : 0, out var matchDetail))
            {
                log.Debug("Ignored unrelated mob death: {Mark} / {World}", creature, world);
                return;
            }
            if (!IsWithinFreshnessWindow(feedEvent.OccurredAtUtc, DateTime.UtcNow))
            {
                log.Debug("Ignored stale matching death event: {Mark} / {World}", creature, world);
                return;
            }

            faloop.RecordRelevantHuntEvent(feedEvent);
            SetFaloopDecision($"Accepted death evidence: {creature} on {world} ({matchDetail})");
            InvalidateExternalDeath(world, creature, hasTerritory ? territory : 0,
                feedEvent.Instance, feedEvent.OccurredAtUtc, "Faloop");
            return;
        }

        if (feedEvent.Action == FaloopEventAction.FutureTiming)
        {
            log.Debug("Ignored future spawn timing: {Mark} / {World}; action={Action}",
                creature, world, feedEvent.RawAction);
            return;
        }

        if (feedEvent.Action == FaloopEventAction.LocationUpdate)
        {
            if (!hasTerritory ||
                !IsTrackedFaloopAlertAwaitingLocation(feedEvent, world, creature, territory))
            {
                // A Faloop sighting is a map-location update, not proof of a current spawn.
                // In particular, users can set one while the website still shows "In HH:MM".
                log.Debug(
                    "Ignored untracked Faloop sighting without live spawn evidence: {Mark} / {World}; action={Action}",
                    creature, world, feedEvent.RawAction);
                return;
            }

            log.Information(
                "Using Faloop {Action} only to enrich the missing location of an already tracked live {Mark} alert on {World}",
                feedEvent.RawAction, creature, world);
            // Promotion is deliberately local to this already-tracked alert. The raw action is
            // retained in diagnostics, while the normal spawn pipeline performs coordinate,
            // freshness, expansion, duplicate, and killed-alert validation again.
            feedEvent = feedEvent with { Action = FaloopEventAction.Spawn };
        }

        if (!IsPositiveCurrentSpawnEvidence(feedEvent))
        {
            log.Debug("Ignored non-spawn Faloop event: {Mark} / {World}; type={Type}, action={Action}",
                creature, world, feedEvent.EventType, feedEvent.RawAction);
            return;
        }

        var precursorProfile = HuntCatalog.GetSsProfileForPrecursorName(creature);
        if (precursorProfile is not null)
        {
            if (hasTerritory && SsAlertMatchesCurrent(world, territory, feedEvent.Instance))
            {
                log.Debug("Accepted current-DC SS precursor evidence: {Mark} / {World}", creature, world);
                ObserveOrLatchSsChain(precursorProfile,
                    $"Faloop reported a {precursorProfile.PrecursorName} precursor");
            }
            else
            {
                log.Debug("Ignored unrelated SS precursor: {Mark} / {World}", creature, world);
            }
            return;
        }

        if (!hasTerritory)
        {
            log.Debug("Ignored Faloop spawn without a supported territory: {Mark} / {World}", creature, world);
            return;
        }
        var definition = HuntCatalog.ResolveStrict(territory, creature);
        if (definition is null)
        {
            log.Debug("Ignored current-DC non-S/SS event: {Mark} / {World} / territory {Territory}",
                creature, world, territory);
            return; // Faloop reports many ranks; only the strict configured S/SS catalog is eligible.
        }
        var expansion = HuntCatalog.GetExpansion(territory);
        if (!config.IsExpansionEnabled(expansion))
        {
            log.Debug("Ignored supported S/SS event: {Mark} / {World}; {Expansion} hunting is disabled",
                definition.Name, world, HuntCatalog.ExpansionName(expansion));
            return;
        }
        if (!IsWithinFreshnessWindow(feedEvent.OccurredAtUtc, DateTime.UtcNow))
        {
            log.Debug("Ignored stale active-spawn event: {Mark} / {World}", definition.Name, world);
            return;
        }

        var faloopAlertIdentity = new HuntAlertSnapshot(
            HuntCatalog.IsAnySsName(definition.Name) ? "ssrank" : "srank",
            world, definition.Name, territory, definition.DataId, definition.PreferredAetheryteId,
            Math.Max(1, feedEvent.Instance), 0f, 0f, feedEvent.OccurredAtUtc);
        PruneKilledAlerts();
        if (killedAlerts.ContainsKey(faloopAlertIdentity.Key))
        {
            log.Debug("Ignored replayed active-spawn event for already killed hunt: {Mark} / {World}",
                definition.Name, world);
            return;
        }

        PruneUnresolvedFaloopAlerts();
        faloop.RecordRelevantHuntEvent(feedEvent);
        RememberFaloopReportId(faloopAlertIdentity.Key, feedEvent.EventId);
        SetFaloopDecision($"Accepted active spawn: {definition.Name} / {world}; resolving destination");
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
            RememberFaloopReportId(unresolved.Key, feedEvent.EventId);
            var trackedAlertHasCoordinates =
                current is not null && current.Key == unresolved.Key && HasUsableMapCoordinates(current) ||
                pendingAlerts.Any(alert => alert.Key == unresolved.Key && HasUsableMapCoordinates(alert));
            if (!trackedAlertHasCoordinates)
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

        var resolvedAlert = new HuntAlertSnapshot(
            HuntCatalog.IsAnySsName(definition.Name) ? "ssrank" : "srank",
            world, definition.Name, territory, definition.DataId, definition.PreferredAetheryteId,
            Math.Max(1, feedEvent.Instance), mapX, mapY, feedEvent.OccurredAtUtc);
        unresolvedFaloopAlerts.Remove(resolvedAlert.Key);
        RememberFaloopReportId(resolvedAlert.Key, feedEvent.EventId);
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

    private static bool IsPositiveCurrentSpawnEvidence(FaloopFeedEvent feedEvent)
    {
        if (feedEvent.Action != FaloopEventAction.Spawn)
            return false;
        if (feedEvent.EventType.Equals("mobworldspawn", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!feedEvent.EventType.Equals("mob", StringComparison.OrdinalIgnoreCase))
            return false;
        return feedEvent.RawAction is "spawn" or "sighting_set" or "spawn_location" or "sighting";
    }

    private bool IsTrackedFaloopAlertAwaitingLocation(
        FaloopFeedEvent feedEvent,
        string world,
        string creature,
        uint territory)
    {
        var definition = HuntCatalog.ResolveStrict(territory, creature);
        if (definition is null)
            return false;

        var identity = new HuntAlertSnapshot(
            HuntCatalog.IsAnySsName(definition.Name) ? "ssrank" : "srank",
            world,
            definition.Name,
            territory,
            definition.DataId,
            definition.PreferredAetheryteId,
            Math.Max(1, feedEvent.Instance),
            0f,
            0f,
            feedEvent.OccurredAtUtc);

        if (unresolvedFaloopAlerts.ContainsKey(identity.Key))
            return true;
        if (current is not null && current.Key == identity.Key && !HasUsableMapCoordinates(current))
            return true;
        return pendingAlerts.Any(alert => alert.Key == identity.Key && !HasUsableMapCoordinates(alert));
    }

    private bool TryMatchTrackedFaloopDeath(
        FaloopFeedEvent feedEvent,
        string world,
        string creature,
        uint territory,
        out string detail)
    {
        detail = string.Empty;
        bool Matches(HuntAlertSnapshot alert)
        {
            if (!alert.World.Equals(world, StringComparison.OrdinalIgnoreCase) ||
                !HuntCatalog.NamesMatch(alert.CreatureName, creature) ||
                territory != 0 && alert.TerritoryId != territory ||
                feedEvent.Instance > 0 && alert.Instance != feedEvent.Instance)
                return false;

            if (!string.IsNullOrWhiteSpace(feedEvent.EventId) &&
                faloopReportIdsByAlertKey.TryGetValue(alert.Key, out var trackedReportId) &&
                !string.IsNullOrWhiteSpace(trackedReportId) &&
                !trackedReportId.Equals(feedEvent.EventId, StringComparison.Ordinal))
                return false;
            return true;
        }

        if (current is not null && Matches(current))
        {
            detail = "matched active S/SS identity";
            return true;
        }
        if (pendingAlerts.Any(Matches))
        {
            detail = "matched queued S/SS identity";
            return true;
        }
        if (unresolvedFaloopAlerts.Values.Any(pending => Matches(pending.Alert)))
        {
            detail = "matched location-pending S/SS identity";
            return true;
        }
        return false;
    }

    private void RememberFaloopReportId(string alertKey, string reportId)
    {
        if (!string.IsNullOrWhiteSpace(alertKey) && !string.IsNullOrWhiteSpace(reportId))
            faloopReportIdsByAlertKey[alertKey] = reportId;
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
                faloopReportIdsByAlertKey.Remove(key);
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
                ObserveOrLatchSsChain(precursorProfile,
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
            var isSonar = chatMessage.Sender.TextValue.Equals("Sonar", StringComparison.OrdinalIgnoreCase);
            var mapLink = chatMessage.Message.Payloads.OfType<MapLinkPayload>().FirstOrDefault();

            if (isSonar && !config.EnableSonarFallback)
                return;

            if (!isSonar && IsPositiveGameKillMessage(text))
            {
                ConfirmKill($"Game hunt message confirmed {current!.CreatureName} was killed");
                return;
            }

            if (HuntCatalog.IsSsChainStartMessage(text))
            {
                var currentSsProfile = current is null
                    ? null
                    : HuntCatalog.GetSsProfileForTerritory(current.TerritoryId);
                if (currentSsProfile is not null)
                    ObserveOrLatchSsChain(currentSsProfile,
                        $"The {currentSsProfile.ExpansionName} SS precursor chain started");
            }

            if (current is not null && IsReservedSsWatch(current))
            {
                var reservedProfile = HuntCatalog.GetSsProfileForSsName(current.CreatureName);
                if (reservedProfile is not null && HuntCatalog.IsSsChainWithdrawnMessage(text))
                {
                    FailCurrent($"The {reservedProfile.PrecursorName} chain withdrew before {reservedProfile.SsName} spawned");
                    return;
                }
                if (reservedProfile is not null && HuntCatalog.IsSsSpawnMessage(text))
                {
                    current = current with { HuntType = "ssrank", ReceivedAtUtc = DateTime.UtcNow };
                    ssSpawnAnnounced = true;
                    status = $"{reservedProfile.SsName} spawn confirmed; continuing the reserved SS hunt";
                    log.Information(
                        "Promoted active SS reservation from the game spawn message: {Ss}",
                        reservedProfile.SsName);
                }
            }

            if (state == SentinelState.ResetToUldah && killConfirmed && activeSsProfile is not null)
            {
                if (HuntCatalog.IsSsChainWithdrawnMessage(text))
                    RemoveQueuedSsReservation(activeSsProfile);
                if (HuntCatalog.IsSsSpawnMessage(text))
                {
                    ssSpawnAnnounced = true;
                    QueueSsAfterCompletedHunt(null, activeSsProfile, DateTime.UtcNow,
                        $"{activeSsProfile.SsName} spawn message arrived during post-kill Return");
                }
            }

            if ((state is SentinelState.PostKillSsGrace or SentinelState.SsWatch) && activeSsProfile is not null)
            {
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
            if (!IsSonarKillNotice(text) &&
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

            if (IsSonarKillNotice(text))
            {
                var killedWorld = ParseSonarWorld(text);
                var killedTerritory = mapLink?.TerritoryType.RowId ?? 0;
                var killedInstance = ParseSonarInstance(text);
                log.Information("Sonar kill notice parsed for world {World}: {Text}",
                    string.IsNullOrWhiteSpace(killedWorld) ? "(unresolved)" : killedWorld, text);
                if (current is not null &&
                    HuntCatalog.TextMentionsMark(text, current.CreatureName) &&
                    KillNoticeMatchesAlert(killedWorld, killedTerritory, killedInstance, current))
                    ConfirmKill($"Sonar confirmed {current.CreatureName} was killed");
                RemoveKilledQueuedAlerts(text, killedWorld, killedTerritory, killedInstance);
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
        if (HuntCatalog.IsChernobog(territory, creature))
        {
            status = $"Ignored {source} alert for Chernobog: U'Ghamaro Mines navigation is intentionally unsupported";
            log.Warning(
                "Ignored {Source} Chernobog alert on {World}; U'Ghamaro Mines navigation is intentionally unsupported and the hunt was not queued",
                source, string.IsNullOrWhiteSpace(world) ? travel.CurrentWorld : world.Trim());
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
        if (isSs && HuntCatalog.TryGetSsStagingLocation(territory, out var ssLocation))
        {
            if (Math.Abs(incoming.MapX - ssLocation.MapX) >= 0.1f ||
                Math.Abs(incoming.MapY - ssLocation.MapY) >= 0.1f)
            {
                log.Information(
                    "Normalized {Source} {Ss} coordinates ({SourceX:0.0}, {SourceY:0.0}) to documented {Territory} spawn ({MapX:0.0}, {MapY:0.0})",
                    source, incoming.CreatureName, incoming.MapX, incoming.MapY,
                    ssLocation.TerritoryName, ssLocation.MapX, ssLocation.MapY);
            }
            incoming = incoming with { MapX = ssLocation.MapX, MapY = ssLocation.MapY };
        }
        if (!HasUsableMapCoordinates(incoming))
        {
            status = $"Ignored coordinate-less {source} report for {incoming.CreatureName}; waiting for a location-bearing report";
            log.Warning(
                "Ignored coordinate-less {Source} report for {Mark} on {World}; it cannot activate or enter the travel queue until coordinates arrive",
                source, incoming.CreatureName, incoming.World);
            return;
        }
        if (mapX > 0f && mapY > 0f && unresolvedFaloopAlerts.Remove(incoming.Key))
            log.Information("{Source} enriched unresolved Faloop alert for {Mark} on {World} with coordinates ({MapX:0.0}, {MapY:0.0})",
                source, incoming.CreatureName, incoming.World, mapX, mapY);
        if (!IsWithinFreshnessWindow(incoming.ReceivedAtUtc, DateTime.UtcNow))
        {
            status = $"Ignored stale {source} alert for {incoming.CreatureName} on {incoming.World}";
            return;
        }
        PruneKilledAlerts();
        if (killedAlerts.ContainsKey(incoming.Key))
            return;

        if (current is not null && current.Key == incoming.Key)
        {
            if (isSs && IsReservedSsWatch(current))
            {
                var reserved = current;
                current = reserved with
                {
                    HuntType = "ssrank",
                    ReceivedAtUtc = incoming.ReceivedAtUtc,
                };
                ssSpawnAnnounced = true;
                log.Information(
                    "Promoted reserved SS opportunity while preserving its documented spawn destination: {Ss} on {World}; source={Source}, ignored provider point=({MapX:0.0}, {MapY:0.0})",
                    incoming.CreatureName, incoming.World, source, incoming.MapX, incoming.MapY);
                status = $"{source} confirmed {incoming.CreatureName}; continuing toward its documented spawn location";
            }
            if (HasUsableMapCoordinates(incoming) && !HasUsableMapCoordinates(current))
            {
                current = current with { MapX = incoming.MapX, MapY = incoming.MapY };
                PrepareCurrentTravel();
                status = $"{source} supplied the missing destination for {current.CreatureName}; resuming the active hunt";
                log.Information(
                    "Enriched active {Mark} from {Source} with destination ({MapX:0.0}, {MapY:0.0}); rebuilding local travel coordinates",
                    current.CreatureName, source, current.MapX, current.MapY);
            }
            return;
        }

        var queuedDuplicate = pendingAlerts.FirstOrDefault(alert => alert.Key == incoming.Key);
        if (queuedDuplicate is not null)
        {
            if (isSs && IsReservedSsWatch(queuedDuplicate))
            {
                var updatedQueue = pendingAlerts
                    .Select(alert => alert.Key == incoming.Key
                        ? alert with { HuntType = "ssrank", ReceivedAtUtc = incoming.ReceivedAtUtc }
                        : alert)
                    .ToArray();
                pendingAlerts.Clear();
                foreach (var alert in updatedQueue)
                    pendingAlerts.Enqueue(alert);
                ReorderPendingQueue();
                PersistQueue();
                log.Information(
                    "Promoted queued SS reservation while preserving its documented spawn destination: {Ss} on {World}; source={Source}, ignored provider point=({MapX:0.0}, {MapY:0.0})",
                    incoming.CreatureName, incoming.World, source, incoming.MapX, incoming.MapY);
            }
            else if (HasUsableMapCoordinates(incoming) && !HasUsableMapCoordinates(queuedDuplicate))
            {
                var updatedQueue = pendingAlerts
                    .Select(alert => alert.Key == incoming.Key
                        ? alert with { MapX = incoming.MapX, MapY = incoming.MapY }
                        : alert)
                    .ToArray();
                pendingAlerts.Clear();
                foreach (var alert in updatedQueue)
                    pendingAlerts.Enqueue(alert);
                PersistQueue();
                log.Information(
                    "Enriched queued {Mark} from {Source} with destination ({MapX:0.0}, {MapY:0.0})",
                    incoming.CreatureName, source, incoming.MapX, incoming.MapY);
            }
            return;
        }

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
            if (combat.IsPlayerDead && killConfirmed)
            {
                QueueSsAfterCompletedHunt(incoming, ssProfile, DateTime.UtcNow,
                    $"{source} confirmed {incoming.CreatureName} while the completed S-rank player is dead");
                return;
            }
            EnqueuePersistent(incoming);
            ssSpawnAnnounced = true;
            ObserveSsChain(ssProfile,
                $"{source} reported {incoming.CreatureName}; staging at its documented spawn until the entity is detectable");
            ssWatchDeadlineUtc = DateTime.MaxValue;
            status = $"{incoming.CreatureName} reported; navigating to its documented spawn location before entity tracking";
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
        if (HuntCatalog.IsAnySsName(alert.CreatureName) &&
            HuntCatalog.TryGetSsStagingLocation(alert.TerritoryId, out var ssLocation))
            alert = alert with { MapX = ssLocation.MapX, MapY = ssLocation.MapY };
        current = alert;
        mark = null;
        killConfirmed = false;
        tagAttempted = false;
        postTagRetreatActive = false;
        markEverIdentified = false;
        markCombatObserved = false;
        pullCycleCombatObserved = false;
        pullCycleTagged = false;
        ResetTagRecoveryTracking();
        identifiedMarkGameObjectId = 0;
        pullCycle = 1;
        pullResetCandidateSinceUtc = DateTime.MinValue;
        activeTagActionId = 0;
        ResetPendingTagDispatch();
        discardAtUldah = false;
        discardReason = string.Empty;
        activeSsProfile = HuntCatalog.GetSsProfileForSsName(alert.CreatureName);
        ssChainObserved = activeSsProfile is not null;
        ssSpawnAnnounced = activeSsProfile is not null && !IsReservedSsWatch(alert);
        postKillSsGraceDeadlineUtc = DateTime.MinValue;
        ssWatchDeadlineUtc = DateTime.MinValue;
        ClearPendingSsChainEvidence();
        ResetSsStagingTracking();
        playerReadySinceUtc = DateTime.MinValue;
        lastMarkSeenUtc = DateTime.MinValue;
        ResetReturnRecoveryTracking();
        resetToUldahAllowsLiveEntityExit = false;
        resetToUldahRequestSource = HuntExitRequestSource.AutomaticStateTransition;
        resetToUldahRequestedFromState = SentinelState.Idle;
        resetToUldahRequestReason = string.Empty;
        ResetRaiseTracking();
        ResetIncidentalAggroTracking();
        ResetParkingRecoveryTracking();
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
        ResetTagRecoveryTracking();
        identifiedMarkGameObjectId = 0;
        pullCycle = 1;
        pullResetCandidateSinceUtc = DateTime.MinValue;
        activeTagActionId = 0;
        ResetPendingTagDispatch();
        discardAtUldah = false;
        discardReason = string.Empty;
        ssChainObserved = true;
        ssSpawnAnnounced = true;
        activeSsProfile = HuntCatalog.GetSsProfileForSsName(alert.CreatureName) ??
                          HuntCatalog.GetSsProfileForTerritory(alert.TerritoryId);
        postKillSsGraceDeadlineUtc = DateTime.MinValue;
        ssWatchDeadlineUtc = DateTime.MinValue;
        ClearPendingSsChainEvidence();
        ResetSsStagingTracking();
        playerReadySinceUtc = DateTime.MinValue;
        lastMarkSeenUtc = DateTime.MinValue;
        ResetReturnRecoveryTracking();
        ResetRaiseTracking();
        ResetIncidentalAggroTracking();
        ResetParkingRecoveryTracking();
        PrepareCurrentTravel();

        // A real loaded SS entity always supersedes static/provider coordinates. Provider links
        // can arrive during the prey phase and must not pull staging away from the documented
        // territory spawn location.
        var visibleSs = FindMark();
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
        parkingGroundPathTask = null;
        parkingPathStartedUtc = DateTime.MinValue;
        pendingParkingFlightPath = null;
        parkingGroundPathGoal = default;
        selectedParkingPath = null;
        crowdFallbackAnnounced = false;
        parkingCandidates.Clear();
        ResetApproachRouteTracking(clearProjectionCandidates: true);
        ResetLocalApproachRecovery();
        ResetLocateSearchTracking();
        nextActionUtc = DateTime.MinValue;

        if (current is null)
            return;

        TryPrepareAlertPoint();

        // Aetheryte data can contain sparse/invalid linked Level rows. Resolution is isolated
        // so one bad game-data reference can never unwind alert acceptance or discard the hunt.
        TryResolveTerritoryAetheryte();
    }

    private bool TryPrepareAlertPoint()
    {
        if (current is null || !HasUsableMapCoordinates(current))
            return false;

        try
        {
            var map = data.GetExcelSheet<Map>()
                .FirstOrDefault(row => row.TerritoryType.RowId == current.TerritoryId);
            if (map.RowId == 0)
            {
                log.Debug(
                    "No current Map row was available for {Mark} in territory {Territory}; local destination conversion will retry",
                    current.CreatureName, current.TerritoryId);
                return false;
            }

            // MapLinkPayload performs Dalamud's canonical map-coordinate conversion. Its RawX/RawY
            // values are local game-world X/Z positions scaled by 1000. Preserve that destination
            // independently of the game's global map flag so direct Faloop navigation survives
            // World Visit, teleport, zoning, and instance transitions without a fallback plugin.
            var mapped = new MapLinkPayload(current.TerritoryId, map.RowId, current.MapX, current.MapY);
            alertPoint = new Vector3(mapped.RawX / 1000f, 1024f, mapped.RawY / 1000f);
            log.Information(
                "Preserved alert destination for {Mark}: map ({MapX:0.0}, {MapY:0.0}) -> local ({LocalX:0.0}, {LocalZ:0.0})",
                current.CreatureName, current.MapX, current.MapY, alertPoint.Value.X, alertPoint.Value.Z);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex,
                "Could not convert {Mark}'s map destination in territory {Territory}; retaining the hunt and retrying",
                current.CreatureName, current.TerritoryId);
            return false;
        }
    }

    private static bool HasUsableMapCoordinates(HuntAlertSnapshot alert) =>
        alert.MapX > 0f && alert.MapY > 0f;

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
            travel.RefreshCurrentDataCenter();
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
                $"Internal error while handling {current.CreatureName}; active hunt retained and retrying through Ul'dah",
                HuntExitRequestSource.FrameworkException);
        }
    }

    private void Tick(DateTime now)
    {
        if ((state is SentinelState.PostKillSsGrace or SentinelState.SsWatch) &&
            TryBlockAutomaticHuntExit(
                HuntExitRequestSource.ConfirmedDeath,
                state,
                "A post-kill/SS state was active while the exact current hunt entity was visibly alive"))
            return;

        if (current is not null && IsReservedSsWatch(current) && !killConfirmed &&
            !markEverIdentified &&
            now >= current.ReceivedAtUtc.AddSeconds(config.SsChainTimeoutSeconds))
        {
            FailCurrent($"The reserved {current.CreatureName} opportunity expired before the SS was confirmed");
            return;
        }

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
                    LatchTagRequirement(visibleMark, now);
                }
                else
                    pullResetCandidateSinceUtc = DateTime.MinValue;
            }

            if (combat.IsPlayerDead)
            {
                TickRaiseAcceptance(now, visibleMark is null
                    ? "Dead before the mark's death was confirmed; waiting for Raise (Return is locked)"
                    : $"Dead while {visibleMark.Name.TextValue} is alive; waiting for Raise (Return is locked)");
                return;
            }

            ObserveRaiseRecoveryCompletion(now);
            if (tagRequired && state != SentinelState.TagApproach &&
                state != SentinelState.AvoidIncidentalAggro && IsLocalHuntState(state))
            {
                BeginTagRequiredRecovery(visibleMark, now,
                    $"{visibleMark?.Name.TextValue ?? current.CreatureName} crossed the engage threshold before a tag was confirmed");
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
        if (!resetToUldahAllowsLiveEntityExit &&
            TryBlockAutomaticHuntExit(
                resetToUldahRequestSource,
                resetToUldahRequestedFromState,
                string.IsNullOrWhiteSpace(resetToUldahRequestReason)
                    ? "ResetToUldah preflight requested automatic departure"
                    : resetToUldahRequestReason))
            return;

        if (TickDeadPostKillRewardGrace(now))
            return;

        if (killConfirmed && combat.IsPlayerDead && TickCompletedRaiseResolution(now))
            return;

        UpdateReturnRecoveryWatchdog(now);

        // The game can open its normal death Return prompt before positive kill evidence moves
        // Sentinel into recovery. Adopt only that exact Ul'dah prompt, only while dead, and only
        // after the active hunt is positively confirmed dead. This preserves the live-hunt Return
        // lock without leaving a pre-existing valid prompt unowned forever.
        if (!returnInitiatedBySentinel && killConfirmed && combat.IsPlayerDead &&
            travel.ReturnConfirmationIsOpen(out var existingReturnPrompt))
        {
            if (returnRecoveryStartedUtc == DateTime.MinValue)
            {
                returnRecoveryStartedUtc = now;
                returnExpectedWorld = travel.CurrentWorld;
            }
            returnInitiatedBySentinel = true;
            returnActionIssuedUtc = now;
            nextReturnConfirmationAttemptUtc = now;
            returnConfirmationObserved = false;
            returnConfirmed = false;
            status = "Adopting the existing post-kill Return confirmation";
            log.Information(
                "Adopting existing post-death Return confirmation after positive kill evidence: {Prompt}",
                existingReturnPrompt);
        }

        // Return uses SelectYesno, but only touch a prompt Sentinel requested or safely adopted
        // while this recovery state is active. This must never become a generic dialog accepter.
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

        if (returnRecoveryStartedUtc == DateTime.MinValue)
        {
            returnRecoveryStartedUtc = now;
            returnExpectedWorld = travel.CurrentWorld;
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
            returnTeleportAttempts++;
            vnav.StopSafe("attempting direct Ul'dah teleport recovery");
            if (travel.Teleport(NativeTravel.UldahAetheryteId))
            {
                ResetReturnLandingRecovery();
                status = "Teleporting normally to Ul'dah for the mandatory reset";
                log.Information(
                    "Direct Ul'dah teleport accepted on attempt {Attempt}; mounted={Mounted}, inFlight={InFlight}",
                    returnTeleportAttempts, condition[ConditionFlag.Mounted], condition[ConditionFlag.InFlight]);
                nextActionUtc = now.AddSeconds(8);
                return;
            }

            log.Information(
                "Direct Ul'dah teleport was not accepted on attempt {Attempt}; mounted={Mounted}, inFlight={InFlight}, inCombat={InCombat}",
                returnTeleportAttempts, condition[ConditionFlag.Mounted], condition[ConditionFlag.InFlight],
                condition[ConditionFlag.InCombat]);
            nextActionUtc = now.AddSeconds(ReturnTeleportRetrySeconds);
        }

        // Landing is a fallback only after concrete Teleport rejections while mounted/flying.
        // It is never the prerequisite for the first normal Ul'dah teleport attempt.
        if ((condition[ConditionFlag.InFlight] || condition[ConditionFlag.Mounted]) &&
            returnTeleportAttempts >= ReturnTeleportAttemptsBeforeLandingFallback)
        {
            TickReturnLandingRecovery(now);
            return;
        }

        status = returnTeleportAttempts == 0
            ? "Preparing a direct normal Teleport to Ul'dah"
            : "Direct Ul'dah teleport is not available yet; retrying before any landing fallback";
    }

    private void TickReturnLandingRecovery(DateTime now)
    {
        if (!condition[ConditionFlag.InFlight])
        {
            vnav.StopSafe("dismounting before Ul'dah recovery");
            if (now >= nextActionUtc)
            {
                UseGeneralAction(23);
                nextActionUtc = now.AddSeconds(1);
            }
            status = "Dismounting before returning to Ul'dah";
            return;
        }

        if (returnLandingStartedUtc == DateTime.MinValue)
        {
            vnav.StopSafe("starting bounded post-kill landing recovery");
            returnLandingStartedUtc = now;
            returnLandingLastProgressPosition = PlayerPosition();
            returnLandingLastProgressUtc = now;
            returnLandingAttempt = 0;
            nextActionUtc = now;
            log.Warning("Ul'dah recovery is blocked by flight; starting bounded landing recovery");
        }

        if (returnLandingPoint is null &&
            (now - returnLandingStartedUtc).TotalSeconds < ReturnLandingDirectAttemptSeconds)
        {
            if (now >= nextActionUtc)
            {
                UseGeneralAction(23);
                nextActionUtc = now.AddSeconds(1);
            }
            status = "Landing before returning to Ul'dah";
            return;
        }

        if (returnLandingPoint is null)
        {
            if (returnLandingCandidates.Count == 0)
                PrepareReturnLandingCandidates();

            while (returnLandingCandidates.Count > 0)
            {
                var candidate = returnLandingCandidates.Dequeue();
                if (!vnav.MoveToSafe(candidate, true))
                    continue;

                returnLandingPoint = candidate;
                returnLandingRouteStartedUtc = now;
                returnLandingLastProgressPosition = PlayerPosition();
                returnLandingLastProgressUtc = now;
                returnLandingAttempt++;
                status = $"Flight obstructed; moving to alternate landing point {returnLandingAttempt} before Ul'dah";
                log.Warning("Post-kill landing attempt {Attempt}: relocating {Distance:0}y to projected floor at Y={Y:0.0}",
                    returnLandingAttempt, Vector3.Distance(PlayerPosition(), candidate), candidate.Y);
                return;
            }

            // Projection can be temporarily unavailable while the mesh is changing. Continue
            // ordinary landing attempts and resample rather than abandoning the confirmed kill.
            if (now >= nextActionUtc)
            {
                UseGeneralAction(23);
                nextActionUtc = now.AddSeconds(2);
                returnLandingStartedUtc = now.AddSeconds(-ReturnLandingDirectAttemptSeconds);
            }
            status = "No alternate landing point is projected yet; retrying before Ul'dah";
            return;
        }

        var player = PlayerPosition();
        if (Vector3.Distance(player, returnLandingLastProgressPosition) >= ParkingMeaningfulProgressDistance)
        {
            returnLandingLastProgressPosition = player;
            returnLandingLastProgressUtc = now;
        }

        if (Vector3.Distance(player, returnLandingPoint.Value) <= 5f)
        {
            vnav.StopSafe("alternate post-kill landing point reached");
            returnLandingPoint = null;
            returnLandingStartedUtc = now;
            nextActionUtc = now;
            status = "Alternate landing point reached; landing before Ul'dah";
            return;
        }

        var routeStopped = (now - returnLandingRouteStartedUtc).TotalSeconds >= 4 &&
                           !vnav.IsPathRunningSafe() && !vnav.IsPathfindInProgressSafe();
        var routeStalled = (now - returnLandingLastProgressUtc).TotalSeconds >= ReturnLandingRouteStallSeconds;
        if (routeStopped || routeStalled)
        {
            vnav.StopSafe(routeStalled
                ? "post-kill alternate landing route stalled"
                : "post-kill alternate landing route stopped");
            log.Warning("Post-kill landing attempt {Attempt} {Reason}; trying another projected floor point",
                returnLandingAttempt, routeStalled ? "stalled" : "stopped");
            returnLandingPoint = null;
            returnLandingRouteStartedUtc = DateTime.MinValue;
            returnLandingLastProgressUtc = now;
            status = "Alternate landing route was obstructed; trying another before Ul'dah";
            return;
        }

        status = $"Moving to alternate landing point {returnLandingAttempt} before returning to Ul'dah";
    }

    private void PrepareReturnLandingCandidates()
    {
        var player = PlayerPosition();
        var candidates = new List<Vector3>();
        float[] radii = [18f, 30f, 45f];
        for (var angle = 0; angle < 360; angle += 45)
        {
            var radians = angle * MathF.PI / 180f;
            var direction = new Vector3(MathF.Cos(radians), 0f, MathF.Sin(radians));
            foreach (var radius in radii)
            {
                var query = player + direction * radius;
                query.Y = 1024f;
                var projected = vnav.PointOnFloorSafe(query, 10f);
                if (projected is null || HorizontalDistance(player, projected.Value) < 12f ||
                    candidates.Any(existing => HorizontalDistance(existing, projected.Value) < 5f))
                    continue;
                candidates.Add(projected.Value);
            }
        }

        foreach (var candidate in candidates
                     .OrderBy(point => MathF.Abs(point.Y - player.Y))
                     .ThenBy(point => HorizontalDistance(point, player)))
            returnLandingCandidates.Enqueue(candidate);

        log.Information("Prepared {Count} alternate floor point(s) for post-kill landing recovery",
            returnLandingCandidates.Count);
    }

    private void ResetReturnLandingRecovery()
    {
        returnLandingCandidates.Clear();
        returnLandingPoint = null;
        returnLandingStartedUtc = DateTime.MinValue;
        returnLandingRouteStartedUtc = DateTime.MinValue;
        returnLandingLastProgressUtc = DateTime.MinValue;
        returnLandingAttempt = 0;
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
                            !IsCurrentMarkActor(actor) &&
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
        parkingGroundPathTask = null;
        pendingParkingFlightPath = null;
        parkingGroundPathGoal = default;
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
        if (incidentalCombatFallbackActive)
        {
            TickIncidentalCombatFallback(threats, now);
            return;
        }

        if (HuntProgressPolicy.ShouldUseIncidentalCombatFallback(
                threats.Length > 0,
                incidentalAggroEscapeAttempt,
                IncidentalAggroEscapeAttemptLimit,
                (now - incidentalAggroStartedUtc).TotalSeconds,
                IncidentalAggroEscapeBudgetSeconds))
        {
            incidentalCombatFallbackActive = true;
            incidentalCombatTargetId = threats[0].GameObjectId;
            nextIncidentalCombatActionUtc = now;
            vnav.StopSafe("bounded incidental escape exhausted; starting controlled WAR/MRD fallback");
            log.Warning(
                "Incidental escape exhausted after {Attempts} attempt(s) and {Seconds:0.0}s; switching to the positively identified non-mark attacker {Threat} ({ObjectId})",
                incidentalAggroEscapeAttempt, (now - incidentalAggroStartedUtc).TotalSeconds,
                threats[0].Name.TextValue, threats[0].GameObjectId);
            TickIncidentalCombatFallback(threats, now);
            return;
        }

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

    private void TickIncidentalCombatFallback(IReadOnlyCollection<IBattleChara> threats, DateTime now)
    {
        if (current is null)
            return;

        var threat = threats.FirstOrDefault(actor => actor.GameObjectId == incidentalCombatTargetId) ??
                     threats.FirstOrDefault();
        var playerId = objects.LocalPlayer?.GameObjectId ?? 0;
        if (threat is null || IsCurrentMarkActor(threat) || threat.IsDead || threat.CurrentHp == 0 ||
            !threat.IsTargetable || playerId == 0 || threat.TargetObjectId != playerId)
        {
            incidentalCombatFallbackActive = false;
            incidentalCombatTargetId = 0;
            nextIncidentalCombatActionUtc = DateTime.MinValue;
            status = "Incidental attacker is no longer positively identified; confirming combat has cleared";
            return;
        }

        incidentalCombatTargetId = threat.GameObjectId;
        if (condition[ConditionFlag.InFlight] || condition[ConditionFlag.Mounted])
        {
            vnav.StopSafe("dismounting for bounded incidental attacker fallback");
            if (now >= nextIncidentalCombatActionUtc)
            {
                UseGeneralAction(23);
                nextIncidentalCombatActionUtc = now.AddSeconds(1);
            }
            status = $"Incidental escape exhausted; dismounting to clear only {threat.Name.TextValue}";
            return;
        }

        var liveMark = FindMark();
        var player = PlayerPosition();
        var clearance = MathF.Max(0f,
            HorizontalDistance(player, threat.Position) - threat.HitboxRadius -
            (objects.LocalPlayer?.HitboxRadius ?? 0f));
        if (clearance > 18f)
        {
            if (liveMark is not null &&
                !ProtectedSegmentIsSafe(player, threat.Position, liveMark.Position,
                    ProtectedCenterRadius(liveMark), true))
            {
                status = $"Incidental attacker {threat.Name.TextValue} is across the S-rank safety radius; holding and retrying ranged clearance only";
            }
            else if (!vnav.IsPathRunningSafe() && !vnav.IsPathfindInProgressSafe())
            {
                var desiredCenterRange = threat.HitboxRadius +
                                         (objects.LocalPlayer?.HitboxRadius ?? 0f) + 15f;
                vnav.MoveCloseToSafe(threat.Position, false, desiredCenterRange);
            }
        }
        else
        {
            vnav.StopSafe("using controlled single-target action on incidental attacker");
        }

        if (now < nextIncidentalCombatActionUtc)
            return;

        var action = combat.TryBasicWarTrashAction(threat);
        nextIncidentalCombatActionUtc = now.AddSeconds(IncidentalCombatActionRetrySeconds);
        if (!action.SupportedJob)
        {
            status = "Incidental escape exhausted, but controlled combat fallback is limited to WAR/MRD; retaining safe recovery diagnostics";
            log.Error(
                "Could not clear incidental attacker {Threat}: controlled fallback requires WAR/MRD; active hunt remains {Mark}",
                threat.Name.TextValue, current.CreatureName);
            return;
        }

        if (action.Submitted)
            log.Information(
                "Submitted controlled incidental-attacker action {ActionId} to {Threat}; the S/SS target remains excluded",
                action.ActionId, threat.Name.TextValue);
        status = action.Submitted
            ? $"Clearing incidental attacker {threat.Name.TextValue} with WAR/MRD action {action.ActionId}; hunt state is preserved"
            : $"Incidental attacker {threat.Name.TextValue} remains selected; waiting for range/GCD before the next basic action";
    }

    private bool IsCurrentMarkActor(IBattleChara actor) =>
        actor.GameObjectId == identifiedMarkGameObjectId ||
        (current is not null &&
         ((current.MarkDataId != 0 && actor.BaseId == current.MarkDataId) ||
          actor.Name.TextValue.Equals(current.CreatureName, StringComparison.OrdinalIgnoreCase)));

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
        incidentalCombatFallbackActive = false;
        incidentalCombatTargetId = 0;
        nextIncidentalCombatActionUtc = DateTime.MinValue;
    }

    private void ResetSsStagingTracking()
    {
        activeSsStagingLocation = null;
        ssStagingAnchor = null;
        ssStagingDestination = null;
        selectedSsStagingCandidate = null;
        ssStagingPathTask = null;
        ssStagingGroundPathTask = null;
        selectedSsStagingPath = null;
        pendingSsStagingFlightPath = null;
        ssStagingGroundPathGoal = default;
        ssStagingPathStartedUtc = DateTime.MinValue;
        nextSsStagingAttemptUtc = DateTime.MinValue;
        ssStagingArrived = false;
        ssStagingLandingStartedUtc = DateTime.MinValue;
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

    private bool TickDeadPostKillRewardGrace(DateTime now)
    {
        if (deadPostKillRewardGraceDeadlineUtc == DateTime.MinValue)
            return false;

        if (!killConfirmed || !tagAttempted || !pullCycleTagged)
        {
            ResetDeadPostKillRewardGrace();
            return false;
        }

        // A successful Raise immediately restores the normal living recovery path. Until then,
        // retain the dead character in the hunt territory so reward/credit processing has a full
        // two seconds after the positively confirmed death before Return can be adopted or used.
        if (!combat.IsPlayerDead)
        {
            ResetDeadPostKillRewardGrace();
            return false;
        }

        if (now < deadPostKillRewardGraceDeadlineUtc)
        {
            var remaining = Math.Max(0, (deadPostKillRewardGraceDeadlineUtc - now).TotalSeconds);
            TickRaiseAcceptance(now,
                $"Current hunt confirmed dead — waiting {remaining:0.0}s for reward/credit processing before Return");
            return true;
        }

        log.Information("Post-kill reward grace complete — Returning to Ul'dah.");
        status = "Post-kill reward grace complete — Returning to Ul'dah";
        ResetDeadPostKillRewardGrace();
        return false;
    }

    private void BeginDeadPostKillRewardGrace(DateTime now)
    {
        ResetDeadPostKillRewardGrace();
        if (!combat.IsPlayerDead || !tagAttempted || !pullCycleTagged)
            return;

        deadPostKillRewardGraceDeadlineUtc = now.AddSeconds(DeadPostKillRewardGraceSeconds);
        log.Information(
            "Current hunt confirmed dead — waiting {Seconds:0.0}s for reward/credit processing before Return.",
            DeadPostKillRewardGraceSeconds);
    }

    private void ResetDeadPostKillRewardGrace() =>
        deadPostKillRewardGraceDeadlineUtc = DateTime.MinValue;

    private bool TickRaiseAcceptance(DateTime now, string fallbackStatus)
    {
        if (!combat.IsPlayerDead)
        {
            ObserveRaiseRecoveryCompletion(now);
            status = fallbackStatus;
            return false;
        }

        if (now < nextRaiseAttemptUtc)
        {
            status = raiseAcceptanceSubmittedUtc == DateTime.MinValue
                ? fallbackStatus
                : "Raise acceptance submitted; waiting for the dialog to close or revival to begin";
            return raiseAcceptanceSubmittedUtc != DateTime.MinValue;
        }

        var interaction = combat.TryAcceptRaise();
        switch (interaction.State)
        {
            case RaiseInteractionState.Submitted:
                if (raiseDialogFirstSeenUtc == DateTime.MinValue)
                {
                    raiseDialogFirstSeenUtc = now;
                    log.Information("Raise dialog detected; prompt text recognized: {Prompt}", interaction.Prompt);
                }
                raiseAcceptanceAttempts++;
                raiseAcceptanceSubmittedUtc = now;
                nextRaiseAttemptUtc = now.AddSeconds(RaiseAcceptanceRetrySeconds);
                raiseDialogCloseLogged = false;
                lastRaisePrompt = interaction.Prompt;
                log.Information(
                    "Raise affirmative control available; acceptance callback submitted (attempt {Attempt})",
                    raiseAcceptanceAttempts);
                status = "Raise acceptance callback submitted; waiting for revival";
                return true;

            case RaiseInteractionState.AffirmativeUnavailable:
                if (raiseDialogFirstSeenUtc == DateTime.MinValue)
                    raiseDialogFirstSeenUtc = now;
                if ((now - lastRaiseDiagnosticUtc).TotalSeconds >= RaiseAcceptanceRetrySeconds)
                {
                    log.Warning(
                        "Raise dialog detected and prompt recognized, but the affirmative control is unavailable: {Prompt}",
                        interaction.Prompt);
                    lastRaiseDiagnosticUtc = now;
                }
                nextRaiseAttemptUtc = now.AddSeconds(0.5);
                status = "Raise dialog recognized; waiting for the affirmative control to become available";
                return true;

            case RaiseInteractionState.PromptUnrecognized:
                if ((now - lastRaiseDiagnosticUtc).TotalSeconds >= 5)
                {
                    log.Information("SelectYesno is open but is not a Raise prompt: {Prompt}", interaction.Prompt);
                    lastRaiseDiagnosticUtc = now;
                }
                status = fallbackStatus;
                return false;

            default:
                if (raiseAcceptanceSubmittedUtc != DateTime.MinValue)
                {
                    if (!raiseDialogCloseLogged)
                    {
                        log.Information(
                            "Raise dialog closed after acceptance callback; waiting for revival state to begin");
                        raiseDialogCloseLogged = true;
                    }
                    status = "Raise dialog closed; waiting for revival to begin";
                    return true;
                }
                status = fallbackStatus;
                return false;
        }
    }

    private bool TickCompletedRaiseResolution(DateTime now)
    {
        var handled = TickRaiseAcceptance(now,
            "Hunt complete; checking for a remaining Raise dialog before Return");
        if (!handled)
            return false;

        var resolutionStart = raiseDialogFirstSeenUtc != DateTime.MinValue
            ? raiseDialogFirstSeenUtc
            : raiseAcceptanceSubmittedUtc;
        if (resolutionStart == DateTime.MinValue ||
            !HuntProgressPolicy.MayDeclineStuckRaiseAfterKill(
                combat.IsPlayerDead,
                false,
                killConfirmed,
                (now - resolutionStart).TotalSeconds,
                CompletedRaiseResolutionBudgetSeconds))
            return true;

        var decline = combat.TryDeclineRaise();
        if (decline.State == RaiseInteractionState.Declined)
        {
            log.Warning(
                "Raise remained unresolved for {Seconds:0.0}s after the confirmed kill; submitted the negative callback so Return recovery cannot remain blocked",
                (now - resolutionStart).TotalSeconds);
            status = "Completed-hunt Raise dialog remained stuck; closing it before Return";
            ResetRaiseTracking();
            nextActionUtc = now.AddSeconds(1);
            return true;
        }

        // The callback may already have closed the dialog between inspection and this frame.
        ResetRaiseTracking();
        return false;
    }

    private void ObserveRaiseRecoveryCompletion(DateTime now)
    {
        if (raiseDialogFirstSeenUtc == DateTime.MinValue &&
            raiseAcceptanceSubmittedUtc == DateTime.MinValue)
            return;
        log.Information(
            "Raise recovery observed: player is conscious; prompt={Prompt}, attempts={Attempts}, elapsed={Elapsed:0.0}s",
            lastRaisePrompt, raiseAcceptanceAttempts,
            raiseDialogFirstSeenUtc == DateTime.MinValue ? 0 : (now - raiseDialogFirstSeenUtc).TotalSeconds);
        ResetRaiseTracking();
    }

    private void ResetRaiseTracking()
    {
        raiseDialogFirstSeenUtc = DateTime.MinValue;
        raiseAcceptanceSubmittedUtc = DateTime.MinValue;
        nextRaiseAttemptUtc = DateTime.MinValue;
        lastRaiseDiagnosticUtc = DateTime.MinValue;
        raiseDialogCloseLogged = false;
        raiseAcceptanceAttempts = 0;
        lastRaisePrompt = string.Empty;
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
        returnTeleportAttempts = 0;
        ResetDeadPostKillRewardGrace();
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

        if (HuntCatalog.IsAnySsName(current.CreatureName))
        {
            activeSsProfile = HuntCatalog.GetSsProfileForSsName(current.CreatureName) ??
                              HuntCatalog.GetSsProfileForTerritory(current.TerritoryId);
            if (activeSsProfile is null)
            {
                status = $"Could not resolve the SS profile for {current.CreatureName}; retaining the hunt at the aetheryte";
                return;
            }

            ssChainObserved = true;
            ssWatchDeadlineUtc = ssSpawnAnnounced
                ? DateTime.MaxValue
                : current.ReceivedAtUtc.AddSeconds(config.SsChainTimeoutSeconds);
            ResetSsStagingTracking();
            SetState(SentinelState.SsWatch,
                $"vnavmesh is ready; staging about {SsStagingTargetRadius:0}y from the documented {activeSsProfile.SsName} spawn until the entity is detectable");
            return;
        }

        SetState(SentinelState.PrepareApproachDestination,
            "vnavmesh mesh is fully ready; resolving the active hunt's stored alert coordinates");
    }

    private void TickPrepareApproachDestination(DateTime now)
    {
        if (current is null)
            return;
        if (TrySwitchToVisibleMarkDuringApproach(now))
            return;
        if (!vnav.IsReadySafe())
        {
            BeginMeshWait("vnavmesh mesh readiness was lost");
            return;
        }

        if (approachRecoveryStartedUtc != DateTime.MinValue &&
            (now - approachRecoveryStartedUtc).TotalSeconds >= LocalApproachRecoveryBudgetSeconds)
        {
            FailCurrent(
                $"local coordinate projection/path recovery was exhausted after {LocalApproachRecoveryBudgetSeconds:0}s; the hunt is unresolved, not dead",
                HuntExitRequestSource.RecoveryBudget);
            return;
        }

        if (alertPoint is null)
        {
            if (now < nextActionUtc)
                return;
            if (!TryPrepareAlertPoint())
            {
                if (HasUsableMapCoordinates(current) && approachRecoveryStartedUtc == DateTime.MinValue)
                    approachRecoveryStartedUtc = now;
                nextActionUtc = now.AddSeconds(ApproachRouteRetrySeconds);
                status = HasUsableMapCoordinates(current)
                    ? $"Waiting for current game map data to convert {current.CreatureName}'s X{current.MapX:0.0} Y{current.MapY:0.0} destination; retrying"
                    : $"Waiting for a usable mapped destination for {current.CreatureName}; the hunt remains active while alert sources enrich it";
                return;
            }
        }

        if (approachPoint is null)
        {
            if (now < nextActionUtc)
                return;
            if (approachProjectionCandidates.Count == 0)
            {
                var resolvedAlertPoint = alertPoint;
                if (resolvedAlertPoint is null)
                    return;
                PrepareApproachProjectionCandidates(resolvedAlertPoint.Value);
            }
            approachPoint = approachProjectionCandidates.Count > 0
                ? approachProjectionCandidates.Dequeue()
                : null;
            if (approachPoint is not null)
                approachPointIsApproximate = false;
            if (approachPoint is null)
            {
                if (approachRecoveryStartedUtc == DateTime.MinValue)
                    approachRecoveryStartedUtc = now;
                approachEmptyProjectionPasses++;
                var decision = HuntProgressPolicy.DecideProjectionRecovery(
                    approachEmptyProjectionPasses,
                    ProjectionFailuresBeforeApproximateRoute,
                    approachApproximateRecoveryUsed,
                    (now - approachRecoveryStartedUtc).TotalSeconds,
                    LocalApproachRecoveryBudgetSeconds);
                log.Warning(
                    "Destination projection failed for {Mark}; pass {Pass}, local recovery={Recovery}, elapsed={Elapsed:0.0}s/{Budget:0}s",
                    current.CreatureName, approachEmptyProjectionPasses, decision,
                    (now - approachRecoveryStartedUtc).TotalSeconds, LocalApproachRecoveryBudgetSeconds);
                if (decision == ProjectionRecoveryAction.BeginApproximateFlight &&
                    TryPrepareApproximateAlertFlight())
                {
                    approachApproximateRecoveryUsed = true;
                    ResetApproachRouteTracking(clearProjectionCandidates: false);
                    nextActionUtc = now;
                    log.Warning(
                        "Destination projection failed for {Mark}; attempting bounded approximate X/Z flight while continuing actual-entity detection",
                        current.CreatureName);
                }
                else if (decision == ProjectionRecoveryAction.AbandonUnresolved)
                {
                    FailCurrent(
                        $"mapped destination remained unprojectable after {approachEmptyProjectionPasses} bounded passes and approximate recovery",
                        HuntExitRequestSource.RecoveryBudget);
                    return;
                }

                if (approachPoint is not null)
                {
                    status = $"Projection recovery: beginning approximate flight toward {current.CreatureName}'s reported X/Z";
                }
                else
                {
                    nextActionUtc = now.AddSeconds(ApproachRouteRetrySeconds);
                    status = $"Could not project {current.CreatureName}'s mapped destination; bounded pass " +
                             $"{approachEmptyProjectionPasses} will retry, then use approximate flight";
                    return;
                }
            }

            if (!approachPointIsApproximate)
            {
                approachEmptyProjectionPasses = 0;
                approachProjectionCandidateIndex++;
                ResetApproachRouteTracking(clearProjectionCandidates: false);
                log.Information(
                    "Trying approach candidate {Index}/{Count} for {Mark}: local ({X:0.0}, {Z:0.0}), {Distance:0}y from player",
                    approachProjectionCandidateIndex, approachProjectionCandidateCount, current.CreatureName,
                    approachPoint.Value.X, approachPoint.Value.Z,
                    HorizontalDistance(PlayerPosition(), approachPoint.Value));
                status = $"Trying approach candidate {approachProjectionCandidateIndex}/{approachProjectionCandidateCount} " +
                         $"for {current.CreatureName}";
            }
        }

        if (now < nextActionUtc)
            return;
        if (!EnsureMounted(now))
            return;
        if (TryStartApproachRoute(now))
        {
            var scanRange = Math.Max(ActiveDistanceProfile.FlagApproachDistance,
                ActiveDistanceProfile.WaitingDistance);
            SetState(SentinelState.ApproachAlertCoordinates,
                $"Flying toward {current.CreatureName}'s reported coordinates; " +
                $"entity resolution waits until within about {scanRange:0}y");
            return;
        }

        nextActionUtc = now.AddSeconds(ApproachRouteRetrySeconds);
        if (approachRouteCandidates.Count == 0)
        {
            log.Information(
                "Approach candidate {Index}/{Count} could not start a route; trying the next projected candidate",
                approachProjectionCandidateIndex, approachProjectionCandidateCount);
            approachPoint = null;
            ResetApproachRouteTracking(clearProjectionCandidates: false);
            status = $"Mapped {current.CreatureName} destination was unreachable; trying another nearby projection";
        }
        else
            status = $"No route to {current.CreatureName}'s next approach waypoint is available yet; " +
                     "holding the active hunt and retrying after a short backoff";
    }

    private void TickApproachAlertCoordinates(DateTime now)
    {
        if (current is null)
            return;
        if (TrySwitchToVisibleMarkDuringApproach(now))
            return;
        if (!vnav.IsReadySafe())
        {
            vnav.StopSafe("mesh readiness was lost during explicit approach path lifecycle");
            ResetApproachRouteTracking(clearProjectionCandidates: false);
            BeginMeshWait("vnavmesh readiness was lost during the coordinate approach");
            return;
        }
        if (approachRecoveryStartedUtc != DateTime.MinValue &&
            (now - approachRecoveryStartedUtc).TotalSeconds >= LocalApproachRecoveryBudgetSeconds)
        {
            vnav.StopSafe("bounded local approach budget exhausted");
            ResetApproachRouteTracking(clearProjectionCandidates: true);
            FailCurrent(
                $"local approach made no usable progress within {LocalApproachRecoveryBudgetSeconds:0}s; the hunt is unresolved, not dead",
                HuntExitRequestSource.RecoveryBudget);
            return;
        }
        if (approachPoint is null)
        {
            SetState(SentinelState.PrepareApproachDestination,
                "Direct alert-coordinate projection was lost; rebuilding it without abandoning the active hunt");
            return;
        }

        var playerPosition = PlayerPosition();
        var distance = HorizontalDistance(playerPosition, approachPoint.Value);
        var scanRange = Math.Max(ActiveDistanceProfile.FlagApproachDistance,
            ActiveDistanceProfile.WaitingDistance);
        if (distance <= scanRange + ApproachScanTolerance)
        {
            vnav.StopSafe("reported-coordinate scan range reached");
            log.Information(
                "Within entity scan range for {Mark}: {Distance:0.0}y from the projected report; preferred={Preferred:0.0}y, tolerance={Tolerance:0.0}y; switching to mark detection",
                current.CreatureName, distance, scanRange, ApproachScanTolerance);
            ResetApproachRouteTracking(clearProjectionCandidates: false);
            SetState(SentinelState.LocateMark,
                $"Reached {current.CreatureName}'s reported area; beginning positive entity resolution");
            return;
        }

        if (approachPathTask is not null)
        {
            var pathfindElapsed = (now - approachRouteStartedUtc).TotalSeconds;
            var navPathfinding = vnav.IsNavPathfindInProgressSafe();
            if (navPathfinding && !approachPathfindingObserved)
            {
                approachPathfindingObserved = true;
                log.Information(
                    "vnavmesh entered pathfinding for {Mark}; mounted={Mounted}, flying={Flying}",
                    current.CreatureName, condition[ConditionFlag.Mounted], condition[ConditionFlag.InFlight]);
            }

            if (!approachPathTask.IsCompleted)
            {
                if (pathfindElapsed >= ApproachPathQueryTimeoutSeconds)
                {
                    approachPathTask = null;
                    vnav.StopSafe("bounded local path query timed out");
                    HandleStoppedApproachRoute(now,
                        $"pathfinding exceeded {ApproachPathQueryTimeoutSeconds:0}s without returning a route");
                    return;
                }
                if (pathfindElapsed >= ApproachSlowPathfindNoticeSeconds && !approachSlowPathfindNoticeLogged)
                {
                    approachSlowPathfindNoticeLogged = true;
                    log.Warning(
                        "Pathfinding for {Mark} is still pending after {Elapsed:0.0}s; retaining the request without Stop/reissue",
                        current.CreatureName, pathfindElapsed);
                }
                status = $"Pathfinding to {current.CreatureName}'s approach waypoint " +
                         $"({distance:0}y remaining); waiting for vnavmesh without cancelling the request";
                return;
            }

            var completedTask = approachPathTask;
            approachPathTask = null;
            List<Vector3> path;
            try
            {
                path = completedTask.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                log.Warning(ex, "vnavmesh pathfinding failed for {Mark}", current.CreatureName);
                HandleStoppedApproachRoute(now, "pathfinding task failed before movement could begin");
                return;
            }

            if (path.Count == 0)
            {
                log.Information("Pathfinding returned no waypoints for {Mark}", current.CreatureName);
                HandleStoppedApproachRoute(now, "pathfinding completed with zero waypoints");
                return;
            }

            var firstGap = HorizontalDistance(playerPosition, path[0]);
            var lastGap = HorizontalDistance(path[^1], approachRouteTarget ?? approachPoint.Value);
            log.Information(
                "Pathfinding succeeded for {Mark}: {Count} waypoint(s), first-point gap={FirstGap:0.0}y, endpoint gap={LastGap:0.0}y, movementAllowed={MovementAllowed}",
                current.CreatureName, path.Count, firstGap, lastGap, vnav.IsMovementAllowedSafe());
            if (firstGap > 40f)
                log.Warning(
                    "First path point for {Mark} is {Gap:0.0}y from the current navmesh position; route will be observed during the movement-start grace period",
                    current.CreatureName, firstGap);

            if (!vnav.MovePathSafe(path, true))
            {
                HandleStoppedApproachRoute(now, "Path.MoveTo rejected the computed waypoint list");
                return;
            }

            approachSubmittedWaypointCount = path.Count;
            approachLastObservedWaypointCount = -1;
            approachMovementSubmittedUtc = now;
            approachMovementObserved = false;
            approachLastProgressUtc = now;
            status = $"Route submitted for {current.CreatureName} ({path.Count} waypoints); " +
                     "waiting for vnavmesh movement to begin";
            return;
        }

        var pathRunning = vnav.IsPathRunningSafe();
        var waypointCount = vnav.PathWaypointCountSafe();
        var simplePathfinding = vnav.IsPathfindInProgressSafe();
        var navPathfindingActive = vnav.IsNavPathfindInProgressSafe();
        if (pathRunning || waypointCount > 0)
        {
            if (!approachMovementObserved)
            {
                approachMovementObserved = true;
                var activeWaypoints = vnav.PathWaypointsSafe();
                var activeFirstGap = activeWaypoints.Count > 0
                    ? HorizontalDistance(playerPosition, activeWaypoints[0])
                    : -1f;
                log.Information(
                    "vnavmesh entered movement/following for {Mark}: running={Running}, waypoints={Waypoints}/{Submitted}, first-active-point={FirstGap:0.0}y, movementAllowed={MovementAllowed}, mounted={Mounted}, flying={Flying}",
                    current.CreatureName, pathRunning, waypointCount, approachSubmittedWaypointCount,
                    activeFirstGap, vnav.IsMovementAllowedSafe(), condition[ConditionFlag.Mounted], condition[ConditionFlag.InFlight]);
            }
            if (waypointCount != approachLastObservedWaypointCount)
            {
                log.Debug(
                    "Approach waypoint state for {Mark}: {Previous} -> {Current}",
                    current.CreatureName, approachLastObservedWaypointCount, waypointCount);
                approachLastObservedWaypointCount = waypointCount;
            }
            UpdateApproachProgress(now, playerPosition, distance);
            var movementAge = approachMovementSubmittedUtc == DateTime.MinValue
                ? double.MaxValue
                : (now - approachMovementSubmittedUtc).TotalSeconds;
            if (movementAge >= ApproachMovementStartGraceSeconds &&
                (now - approachLastProgressUtc).TotalSeconds >= ApproachRouteStallSeconds)
            {
                vnav.StopSafe("approach route remained active without measurable movement after the startup grace period");
                HandleStoppedApproachRoute(now,
                    $"movement stalled; running={pathRunning}, waypoints={waypointCount}, movementAllowed={vnav.IsMovementAllowedSafe()}");
                return;
            }

            var waypointText = approachRouteIsSegment && approachRouteTarget is not null
                ? $" via a recovery waypoint {HorizontalDistance(playerPosition, approachRouteTarget.Value):0}y away"
                : string.Empty;
            status = $"Approaching {current.CreatureName}'s reported coordinates ({distance:0}y remaining); " +
                     $"not scanning for the entity until nearby{waypointText}";
            return;
        }

        if (approachMovementSubmittedUtc != DateTime.MinValue)
        {
            var movementStartAge = (now - approachMovementSubmittedUtc).TotalSeconds;
            if (movementStartAge < ApproachMovementStartGraceSeconds || simplePathfinding || navPathfindingActive)
            {
                status = $"Route submitted for {current.CreatureName}; waiting for movement to start " +
                         $"({movementStartAge:0.0}/{ApproachMovementStartGraceSeconds:0}s grace, " +
                         $"pathfinding={simplePathfinding || navPathfindingActive})";
                return;
            }

            HandleStoppedApproachRoute(now,
                $"movement state ended after submission; movementStarted={approachMovementObserved}, " +
                $"running={pathRunning}, waypoints={waypointCount}, movementAllowed={vnav.IsMovementAllowedSafe()}, " +
                $"mounted={condition[ConditionFlag.Mounted]}, flying={condition[ConditionFlag.InFlight]}");
            return;
        }

        if (now < nextActionUtc)
            return;
        if (!EnsureMounted(now))
            return;

        if (TryStartApproachRoute(now))
        {
            status = $"Approach resumed for {current.CreatureName}; {distance:0}y remain to the reported area";
            log.Information("Approach resumed for {Mark}; {Distance:0}y remain", current.CreatureName, distance);
        }
        else
        {
            nextActionUtc = now.AddSeconds(ApproachRouteRetrySeconds);
            if (approachRouteCandidates.Count == 0)
                RejectCurrentApproachCandidate(now,
                    "all projected waypoint routes were unavailable from the current position");
            else
                status = $"Approach waypoint was unavailable; trying another after a short backoff while keeping " +
                         $"{current.CreatureName} active";
        }
    }

    private bool TrySwitchToVisibleMarkDuringApproach(DateTime now)
    {
        if (current is null)
            return false;
        var visible = FindMark();
        if (visible is null)
            return false;

        mark = visible;
        MarkWasIdentified(visible);
        if (visible.IsDead || visible.CurrentHp == 0)
        {
            ConfirmKill($"{visible.Name.TextValue} became visibly dead during local approach recovery");
            return true;
        }

        vnav.StopSafe("actual hunt entity became visible during local coordinate approach");
        ResetApproachRouteTracking(clearProjectionCandidates: true);
        approachPoint = null;
        LatchTagRequirement(visible, now);
        log.Information(
            "Actual entity detected during local approach: {Mark}; switching immediately from approximate coordinates to dynamic tracking",
            visible.Name.TextValue);
        if (tagRequired)
            BeginTagRequiredRecovery(visible, now,
                $"{visible.Name.TextValue} became visible with the tag gate already open");
        else
            SetState(SentinelState.LocateMark,
                $"{visible.Name.TextValue} is now detectable; switching to dynamic entity tracking");
        return true;
    }

    private bool TryPrepareApproximateAlertFlight()
    {
        if (current is null || alertPoint is null)
            return false;
        var player = PlayerPosition();
        approachPoint = new Vector3(alertPoint.Value.X, player.Y, alertPoint.Value.Z);
        approachPointIsApproximate = true;
        approachProjectionCandidateIndex = 1;
        approachProjectionCandidateCount = 1;
        log.Warning(
            "Using bounded approximate local flight for {Mark}: X={X:0.0}, Z={Z:0.0}, flight Y={Y:0.0}; actual entity detection remains authoritative",
            current.CreatureName, approachPoint.Value.X, approachPoint.Value.Z, approachPoint.Value.Y);
        return true;
    }

    private void PrepareApproachProjectionCandidates(Vector3 anchor)
    {
        approachProjectionCandidates.Clear();
        approachProjectionCandidateIndex = 0;
        approachProjectionCandidateCount = 0;
        var projected = new List<Vector3>();
        var playerY = PlayerPosition().Y;
        var verticalOrigins = new[] { anchor.Y, playerY + 120f, playerY + 60f, playerY + 20f, playerY - 40f, 1024f }
            .Distinct()
            .ToArray();
        var offsets = new[] { 0f, 6f, 12f, 18f, 24f, 36f, 54f, 72f };
        var angles = new[] { 0f, 45f, 90f, 135f, 180f, 225f, 270f, 315f };

        foreach (var radius in offsets)
        {
            foreach (var angle in radius == 0f ? new[] { 0f } : angles)
            {
                var radians = angle * MathF.PI / 180f;
                var sample = anchor + new Vector3(MathF.Cos(radians) * radius, 0f, MathF.Sin(radians) * radius);
                foreach (var originY in verticalOrigins)
                {
                    sample.Y = originY;
                    var floor = vnav.PointOnFloorSafe(sample, radius == 0f ? 8f : 18f);
                    if (floor is null || projected.Any(point => Vector3.Distance(point, floor.Value) < 2f))
                        continue;
                    projected.Add(floor.Value);
                }
            }
        }

        var ordered = projected
            .OrderBy(point => HorizontalDistance(point, anchor))
            .ThenBy(point => MathF.Abs(point.Y - playerY))
            .Take(24)
            .ToArray();
        foreach (var point in ordered)
            approachProjectionCandidates.Enqueue(point);
        approachProjectionCandidateCount = ordered.Length;
        log.Information(
            "Reported destination resolved for {Mark}: local anchor ({X:0.0}, {Z:0.0}); prepared {Count} elevation-aware candidate(s) from {VerticalCount} vertical origin(s)",
            current?.CreatureName ?? "active hunt", anchor.X, anchor.Z, ordered.Length,
            verticalOrigins.Length);
    }

    private bool TryStartApproachRoute(DateTime now)
    {
        if (approachPoint is null)
            return false;

        if (approachRouteCandidates.Count == 0)
            PrepareApproachRouteCandidates(approachPoint.Value);
        if (approachRouteCandidates.Count == 0)
            return false;

        var player = PlayerPosition();
        var target = approachRouteCandidates.Dequeue();
        var remaining = HorizontalDistance(player, approachPoint.Value);
        approachRouteIsSegment = HorizontalDistance(target, approachPoint.Value) > 2f;
        var pathTask = vnav.PathfindSafe(player, target, true);
        if (pathTask is null)
            return false;

        approachRouteTarget = target;
        approachPathTask = pathTask;
        approachRouteStartPosition = player;
        approachLastProgressPosition = player;
        approachRouteStartRemaining = remaining;
        approachBestRemaining = remaining;
        approachRouteStartedUtc = now;
        approachLastProgressUtc = now;
        approachMovementSubmittedUtc = DateTime.MinValue;
        approachPathfindingObserved = false;
        approachMovementObserved = false;
        approachSlowPathfindNoticeLogged = false;
        approachSubmittedWaypointCount = 0;
        approachLastObservedWaypointCount = -1;
        nextActionUtc = DateTime.MinValue;

        log.Information(
            "Submitted explicit vnavmesh pathfind for {Mark}: segment={Segment}, targetDistance={TargetDistance:0.0}y, totalRemaining={Remaining:0.0}y, mounted={Mounted}, flying={Flying}",
            current?.CreatureName ?? "active hunt", approachRouteIsSegment,
            HorizontalDistance(player, target), remaining,
            condition[ConditionFlag.Mounted], condition[ConditionFlag.InFlight]);

        if (approachStartingEgressActive)
            log.Information(
                "Using starting-area egress waypoint: {Distance:0}y toward a reachable launch point",
                HorizontalDistance(player, target));
        else if (approachRouteIsSegment)
            log.Information(
                "Using segmented approach waypoint: {Distance:0}y toward destination ({Remaining:0}y total remaining)",
                HorizontalDistance(player, target), remaining);
        return true;
    }

    private void PrepareApproachRouteCandidates(Vector3 destination)
    {
        approachRouteCandidates.Clear();
        var player = PlayerPosition();
        var delta = destination - player;
        delta.Y = 0f;
        var remaining = delta.Length();
        if (remaining < 0.01f)
            return;

        var direction = Vector3.Normalize(delta);
        var tangent = new Vector3(-direction.Z, 0f, direction.X);
        var accepted = new List<Vector3>();

        if (approachStartingEgressActive)
        {
            foreach (var distance in new[] { 28f, 42f, 56f })
            foreach (var lateral in new[] { 0f, 10f, -10f, 18f, -18f })
            {
                var intended = player + direction * distance + tangent * lateral;
                intended.Y = 1024f;
                var projected = vnav.PointOnFloorSafe(intended, 14f);
                if (projected is null ||
                    HorizontalDistance(projected.Value, player) < 12f ||
                    HorizontalDistance(projected.Value, destination) >= remaining ||
                    accepted.Any(point => HorizontalDistance(point, projected.Value) < 3f))
                    continue;
                accepted.Add(projected.Value);
            }

            foreach (var point in accepted
                         .OrderBy(point => HorizontalDistance(point, destination))
                         .Take(8))
                approachRouteCandidates.Enqueue(point);
            log.Information(
                "Starting-area route recovery prepared {Count} nearby reachable egress candidate(s)",
                approachRouteCandidates.Count);
            return;
        }

        // The destination was already projected onto the current territory mesh. Submit one
        // continuous flight path to it instead of inventing floor-projected 180y midpoints.
        // Midpoint projection caused visible pauses at every handoff and could reject otherwise
        // valid flights across water, cliffs, or sparse ground mesh (notably Western/Outer La Noscea).
        approachRouteCandidates.Enqueue(destination);
    }

    private void UpdateApproachProgress(DateTime now, Vector3 player, float remaining)
    {
        var movement = HorizontalDistance(player, approachLastProgressPosition);
        var improvement = approachBestRemaining - remaining;
        if (movement < ApproachMeaningfulProgressDistance &&
            improvement < ApproachMeaningfulProgressDistance)
            return;

        approachBestRemaining = Math.Min(approachBestRemaining, remaining);
        approachLastProgressPosition = player;
        approachLastProgressUtc = now;
        approachRecoveryStartedUtc = DateTime.MinValue;
    }

    private void HandleStoppedApproachRoute(DateTime now, string reason)
    {
        if (current is null || approachPoint is null)
            return;

        var player = PlayerPosition();
        var remaining = HorizontalDistance(player, approachPoint.Value);
        var displacement = HorizontalDistance(player, approachRouteStartPosition);
        var destinationProgress = Math.Max(0f, approachRouteStartRemaining - remaining);
        log.Information(
            "Route stopped after {Progress:0.0}y destination progress ({Movement:0.0}y actual movement) for {Mark}: {Reason}",
            destinationProgress, displacement, current.CreatureName, reason);
        approachRouteTarget = null;
        approachPathTask = null;
        approachRouteStartedUtc = DateTime.MinValue;
        approachLastProgressUtc = DateTime.MinValue;
        approachMovementSubmittedUtc = DateTime.MinValue;
        approachPathfindingObserved = false;
        approachMovementObserved = false;
        approachSlowPathfindNoticeLogged = false;
        approachSubmittedWaypointCount = 0;
        approachLastObservedWaypointCount = -1;
        nextActionUtc = now.AddSeconds(ApproachRouteRetrySeconds);

        if (destinationProgress >= ApproachMeaningfulProgressDistance)
        {
            approachRecoveryStartedUtc = DateTime.MinValue;
            approachEarlyStops = 0;
            approachStartingEgressActive = false;
            approachRouteCandidates.Clear();
            status = $"Route stopped after {destinationProgress:0.0}y progress; resuming {current.CreatureName}'s continuous approach " +
                     "after a short backoff";
            return;
        }

        if (approachRecoveryStartedUtc == DateTime.MinValue)
            approachRecoveryStartedUtc = now;
        approachEarlyStops++;
        if (!approachStartingEgressActive)
        {
            approachStartingEgressActive = true;
            approachRouteCandidates.Clear();
            log.Information(
                "Coordinate route stopped without progress; trying a nearby starting-area egress point before the long approach");
            status = "Coordinate route made no progress; sampling a reachable point away from the starting area";
            return;
        }

        if (approachRouteCandidates.Count > 0 && approachEarlyStops < ApproachEarlyStopLimit)
        {
            status = $"Route stopped after {destinationProgress:0.0}y progress; trying another starting-area candidate " +
                     "after a short backoff";
            return;
        }

        RejectCurrentApproachCandidate(now,
            $"repeated early stops made less than {ApproachMeaningfulProgressDistance:0}y progress");
    }

    private void RejectCurrentApproachCandidate(DateTime now, string reason)
    {
        if (current is null)
            return;
        log.Information(
            "Approach candidate {Index}/{Count} rejected for {Mark}: {Reason}",
            approachProjectionCandidateIndex, approachProjectionCandidateCount, current.CreatureName, reason);
        approachPoint = null;
        approachPointIsApproximate = false;
        ResetApproachRouteTracking(clearProjectionCandidates: false);
        nextActionUtc = now.AddSeconds(ApproachRouteRetrySeconds);
        SetState(SentinelState.PrepareApproachDestination,
            approachProjectionCandidates.Count > 0
                ? $"Candidate rejected after repeated early stops; trying another projection for {current.CreatureName}"
                : $"All current projections stopped early; resampling near {current.CreatureName}'s reported coordinates");
    }

    private void ResetApproachRouteTracking(bool clearProjectionCandidates)
    {
        approachRouteCandidates.Clear();
        approachRouteTarget = null;
        approachPathTask = null;
        approachRouteStartPosition = Vector3.Zero;
        approachLastProgressPosition = Vector3.Zero;
        approachRouteStartRemaining = 0f;
        approachBestRemaining = 0f;
        approachRouteStartedUtc = DateTime.MinValue;
        approachLastProgressUtc = DateTime.MinValue;
        approachMovementSubmittedUtc = DateTime.MinValue;
        approachEarlyStops = 0;
        approachRouteIsSegment = false;
        approachStartingEgressActive = false;
        approachPathfindingObserved = false;
        approachMovementObserved = false;
        approachSlowPathfindNoticeLogged = false;
        approachSubmittedWaypointCount = 0;
        approachLastObservedWaypointCount = -1;
        if (!clearProjectionCandidates)
            return;
        approachProjectionCandidates.Clear();
        approachProjectionCandidateIndex = 0;
        approachProjectionCandidateCount = 0;
        approachPointIsApproximate = false;
    }

    private void ResetLocalApproachRecovery()
    {
        approachEmptyProjectionPasses = 0;
        approachPointIsApproximate = false;
        approachApproximateRecoveryUsed = false;
        approachRecoveryStartedUtc = DateTime.MinValue;
    }

    private double CurrentLocateSearchBudgetSeconds()
    {
        var configuredWindow = Math.Max(30, config.LocateTimeoutSeconds);
        return pendingAlerts.Count > 0
            ? Math.Max(90, configuredWindow + 30)
            : Math.Max(120, configuredWindow * 2);
    }

    private void ResetLocateSearchTracking()
    {
        huntSearchStartedUtc = DateTime.MinValue;
        locateWindowStartedUtc = DateTime.MinValue;
        finalLocateScanStartedUtc = DateTime.MinValue;
        locateWindowCount = 0;
        finalLocateReturnStarted = false;
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
            LatchTagRequirement(mark, now);
            if (tagRequired)
            {
                BeginTagRequiredRecovery(mark, now,
                    "Reacquired the live mark; resuming the latched tag operation before parking");
                return;
            }
            BeginSafeParking(mark, true);
            return;
        }

        vnav.StopSafe();
        if (huntSearchStartedUtc == DateTime.MinValue)
        {
            huntSearchStartedUtc = now;
            locateWindowStartedUtc = now;
            locateWindowCount = 1;
            log.Information(
                "LocateMark cumulative search started for {Mark}; window={Window}s, total budget={Budget:0}s, queued={Queued}",
                current.CreatureName, config.LocateTimeoutSeconds, CurrentLocateSearchBudgetSeconds(),
                pendingAlerts.Count);
        }
        if (locateWindowStartedUtc == DateTime.MinValue)
            locateWindowStartedUtc = now;

        var cumulative = (now - huntSearchStartedUtc).TotalSeconds;
        if (finalLocateReturnStarted && finalLocateScanStartedUtc == DateTime.MinValue)
        {
            finalLocateScanStartedUtc = now;
            locateWindowCount++;
            log.Warning(
                "Final positive entity scan started for {Mark} at the reported area after {Elapsed:0}s cumulative search",
                current.CreatureName, cumulative);
        }
        var finalElapsed = finalLocateScanStartedUtc == DateTime.MinValue
            ? 0
            : (now - finalLocateScanStartedUtc).TotalSeconds;
        var recovery = HuntProgressPolicy.DecideLocateRecovery(
            cumulative,
            CurrentLocateSearchBudgetSeconds(),
            finalLocateReturnStarted,
            finalElapsed,
            FinalLocateScanSeconds);
        if (recovery == LocateRecoveryAction.BeginFinalReportedAreaScan)
        {
            finalLocateReturnStarted = true;
            var scanRange = Math.Max(ActiveDistanceProfile.FlagApproachDistance,
                ActiveDistanceProfile.WaitingDistance);
            if (alertPoint is not null &&
                HorizontalDistance(PlayerPosition(), alertPoint.Value) > scanRange + ApproachScanTolerance)
            {
                approachPoint = null;
                ResetApproachRouteTracking(clearProjectionCandidates: true);
                approachRecoveryStartedUtc = now;
                nextActionUtc = now;
                log.Warning(
                    "LocateMark search budget reached for {Mark}; returning once to the original reported coordinates for a final positive scan",
                    current.CreatureName);
                SetState(SentinelState.PrepareApproachDestination,
                    $"Search budget reached after {cumulative:0}s; returning once to the reported area for a final scan");
                return;
            }

            finalLocateScanStartedUtc = now;
            locateWindowCount++;
            log.Warning(
                "LocateMark search budget reached for {Mark}; already at the reported area, beginning the final {Seconds:0}s positive scan",
                current.CreatureName, FinalLocateScanSeconds);
        }
        else if (recovery == LocateRecoveryAction.AbandonUnresolved)
        {
            FailCurrent(
                $"{current.CreatureName} remained unresolved—not confirmed dead—after {cumulative:0}s, {locateWindowCount} scan window(s), and the final reported-area scan",
                HuntExitRequestSource.RecoveryBudget);
            return;
        }

        var windowElapsed = (now - locateWindowStartedUtc).TotalSeconds;
        if (!finalLocateReturnStarted && windowElapsed >= config.LocateTimeoutSeconds)
        {
            locateWindowStartedUtc = now;
            locateWindowCount++;
            log.Warning(
                "LocateMark scan window {Window} completed for {Mark}; cumulative={Cumulative:0}s/{Budget:0}s. Missing remains neutral evidence",
                locateWindowCount - 1, current.CreatureName, cumulative, CurrentLocateSearchBudgetSeconds());
            status = $"{current.CreatureName} was not visible in scan window {locateWindowCount - 1}; " +
                     $"continuing within the cumulative {CurrentLocateSearchBudgetSeconds():0}s search budget—no kill is inferred";
            return;
        }

        if (finalLocateReturnStarted)
        {
            var finalRemaining = Math.Max(0, FinalLocateScanSeconds - finalElapsed);
            status = $"Final positive scan for {current.CreatureName} at the original reported area " +
                     $"({finalRemaining:0}s remaining). Failure will be unresolved, not dead";
            return;
        }

        var remaining = Math.Max(0, config.LocateTimeoutSeconds - windowElapsed);
        status = $"Near {current.CreatureName}'s alert coordinates; scan window {locateWindowCount}, " +
                 $"{remaining:0}s remaining ({cumulative:0}s cumulative). Missing does not mean dead";
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
            parkingGroundPathTask = null;
            pendingParkingFlightPath = null;
            parkingGroundPathGoal = default;
            selectedParkingPath = null;
            parkingCandidates.Clear();
            nextActionUtc = now.AddSeconds(1);
            SetState(SentinelState.LocateMark,
                "The previously identified mark temporarily left object range while parking; holding and rescanning");
            return;
        }
        MarkWasIdentified(mark);
        if (CheckParkingRecoveryBudget(mark, now))
            return;
        if (parkingPathTask is not null || parkingGroundPathTask is not null)
        {
            PollCrowdParkingPath(mark, now);
            return;
        }
        if (safePoint is not null && Vector3.Distance(PlayerPosition(), safePoint.Value) <= 5f)
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
        if (safePoint is not null)
        {
            var player = PlayerPosition();
            if (Vector3.Distance(player, parkingLastProgressPosition) >= ParkingMeaningfulProgressDistance)
            {
                parkingLastProgressPosition = player;
                parkingLastProgressUtc = now;
            }
            else if (parkingLastProgressUtc != DateTime.MinValue &&
                     (now - parkingLastProgressUtc).TotalSeconds >= ParkingRouteStallSeconds)
            {
                vnav.StopSafe("safe parking route stalled without movement");
                log.Warning("Safe parking route stalled for {Seconds}s; trying another candidate",
                    ParkingRouteStallSeconds);
                if (!TryStartNextParkingRoute(true, mark))
                {
                    if (RegisterParkingRecoveryFailure(mark,
                            "the active parking route stalled and no alternate remained", now))
                        return;
                    nextActionUtc = now.AddSeconds(3);
                    SetState(SentinelState.LocateMark,
                        "Safe parking was obstructed; keeping the hunt active and resampling another route");
                    return;
                }
                SetState(SentinelState.MoveToSafePoint,
                    $"Safe parking route was obstructed; trying another ({parkingCandidates.Count} alternatives remain)");
                return;
            }
        }
        if ((now - stateSinceUtc).TotalSeconds > 4 && !vnav.IsPathRunningSafe() && !vnav.IsPathfindInProgressSafe())
        {
            if (!TryStartNextParkingRoute(true, mark))
            {
                vnav.StopSafe();
                if (RegisterParkingRecoveryFailure(mark,
                        "all sampled parking routes stopped before arrival", now))
                    return;
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
        if (CheckParkingRecoveryBudget(mark, now))
            return;
        if (!RevalidateParkingForLanding(mark, out var reason))
        {
            RestartSafeParkingAfterRevalidation(mark, reason);
            return;
        }
        if (condition[ConditionFlag.InFlight] || condition[ConditionFlag.Mounted])
        {
            if ((now - stateSinceUtc).TotalSeconds >= LandingAttemptTimeoutSeconds)
            {
                vnav.StopSafe("safe parking landing attempt timed out");
                log.Warning("Landing at the selected parking point did not complete within {Seconds}s; trying another candidate",
                    LandingAttemptTimeoutSeconds);
                if (TryStartNextParkingRoute(true, mark))
                {
                    SetState(SentinelState.MoveToSafePoint,
                        $"Landing was obstructed; trying another safe point ({parkingCandidates.Count} alternatives remain)");
                    return;
                }
                if (RegisterParkingRecoveryFailure(mark,
                        "landing timed out and no alternate parking candidate remained", now))
                    return;
                nextActionUtc = now.AddSeconds(3);
                SetState(SentinelState.LocateMark,
                    "Landing was obstructed and no alternate remains; keeping the hunt active and resampling");
                return;
            }
            UseGeneralAction(23);
            nextActionUtc = now.AddSeconds(1);
            return;
        }
        ResetParkingRecoveryTracking();
        var landedClearance = ClearanceFromMark(mark);
        SetState(SentinelState.SafeWait,
            $"Parked {landedClearance:0.0}y clear (preferred {ActiveDistanceProfile.WaitingDistance:0}y); " +
            $"emergency floor {ActiveDistanceProfile.EmergencyDistance:0}y");
    }

    private void TickSafeWait(DateTime now)
    {
        mark = FindMark();
        if (mark is null)
        {
            vnav.StopSafe();
            SetState(SentinelState.LocateMark,
                "Mark temporarily not visible from safe wait; beginning bounded reacquisition without inferring death");
            return;
        }

        MarkWasIdentified(mark);
        LatchTagRequirement(mark, now);

        var clearance = ClearanceFromMark(mark);
        var hp = CombatController.HpPercent(mark);
        var inCombat = CombatController.IsMarkInCombat(mark);
        status = $"Safe wait: {mark.Name.TextValue} {clearance:0.0}y clear, {hp:0.0}% HP, " +
                 (inCombat ? "in combat" : "not in combat");

        if (tagRequired)
        {
            BeginTagRequiredRecovery(mark, now,
                $"Tag required for pull cycle {pullCycle}; parking is suspended until one ranged tag is confirmed");
            return;
        }

        if (clearance < ActiveDistanceProfile.EmergencyDistance)
        {
            BeginGroundRetreat(mark);
            return;
        }

        var preferredError = HuntProgressPolicy.ParkingClearanceError(
            clearance, ActiveDistanceProfile.WaitingDistance);
        if (clearance > MaximumParkingClearance + 0.5f ||
            (preferredError > ParkingPreferredClearanceTolerance + 1f &&
             (selectedParkingCandidate is null ||
              HuntProgressPolicy.IsPreferredParkingClearance(
                  ClearanceAtPoint(selectedParkingCandidate.Position, mark),
                  ActiveDistanceProfile.WaitingDistance,
                  ParkingPreferredClearanceTolerance))))
        {
            BeginSafeParking(mark, fly: false);
            status = $"Mark moved to {clearance:0.0}y clearance; returning toward the configured " +
                     $"{ActiveDistanceProfile.WaitingDistance:0}y preference";
        }
    }

    private void TickTagApproach(DateTime now)
    {
        mark = FindMark();
        if (mark is null)
        {
            vnav.StopSafe();
            if (tagEntityMissingSinceUtc == DateTime.MinValue)
                tagEntityMissingSinceUtc = now;
            if ((now - tagEntityMissingSinceUtc).TotalSeconds >= TagEntityReacquireSeconds)
            {
                SetState(SentinelState.LocateMark,
                    "Tag is still required, but the entity left object range; beginning bounded reacquisition without clearing the tag latch");
                return;
            }
            status = "Tag remains required; briefly reacquiring the current entity without returning to ordinary waiting";
            return;
        }
        MarkWasIdentified(mark);
        tagEntityMissingSinceUtc = DateTime.MinValue;
        if (mark.IsDead || mark.CurrentHp == 0)
        {
            ConfirmKill($"Positively identified {mark.Name.TextValue} is visibly dead during tag approach");
            return;
        }

        LatchTagRequirement(mark, now);
        if (!tagRequired)
        {
            if (tagAttempted)
            {
                if (now >= nextActionUtc)
                    BeginGroundRetreat(mark, postTag: true);
                else
                    status = $"Tag confirmed (action {activeTagActionId}); holding briefly before retreat";
            }
            else
                SetState(SentinelState.SafeWait, "The pull reset before a tag was required; returning to safe wait");
            return;
        }

        if (tagRequiredSinceUtc != DateTime.MinValue &&
            (now - tagRequiredSinceUtc).TotalSeconds >= TagRecoveryBudgetSeconds)
        {
            FailCurrent(
                $"tag preparation remained unresolved for {TagRecoveryBudgetSeconds:0}s while the mark stayed alive",
                HuntExitRequestSource.RecoveryBudget);
            return;
        }

        ReportTagRecoveryEscalation(mark);

        if (pendingTagDispatch is not null)
        {
            var pending = pendingTagDispatch;
            var actionState = combat.ReadTagActionState(pending.ActionId, pending.TargetId);
            if (pending.Sequence is null && pending.DeferredForTarget &&
                actionState.LastUsedSequence != pending.BaselineSequence)
            {
                pending = pending with { Sequence = actionState.LastUsedSequence };
                pendingTagDispatch = pending;
                log.Information(
                    "Tag action sequence emitted: action={ActionId}, target={TargetId}, sequence={Sequence}",
                    pending.ActionId, pending.TargetId, pending.Sequence.Value);
            }

            if (pending.Sequence is ushort sequence &&
                ActionSequenceWasHandled(pending.BaselineSequence, sequence, actionState.LastHandledSequence))
            {
                tagAttempted = true;
                pullCycleTagged = true;
                tagRequired = false;
                pendingTagDispatch = null;
                ResetTagRecoveryTracking(clearRequirement: false);
                nextActionUtc = now.AddSeconds(3);
                status = $"Tag confirmed for {mark.Name.TextValue}, pull cycle {pullCycle} " +
                         $"(action {activeTagActionId}, sequence {sequence}); attack cutoff is active";
                log.Information(
                    "Tag confirmed by handled action sequence: {Mark}, pull cycle={PullCycle}, action={ActionId}, target={TargetId}, sequence={Sequence}",
                    mark.Name.TextValue, pullCycle, activeTagActionId, mark.GameObjectId, sequence);
                return;
            }

            if ((now - pending.SubmittedAtUtc).TotalSeconds < TagDispatchConfirmationTimeoutSeconds)
            {
                status = actionState.QueuedForTarget
                    ? $"Ranged tag is queued for {mark.Name.TextValue}; waiting for server handling"
                    : $"Ranged tag was submitted to {mark.Name.TextValue}; waiting for server handling";
                return;
            }

            log.Warning(
                "Tag submission was not confirmed within {Seconds}s; retaining the live hunt and retrying: action={ActionId}, target={TargetId}, baseline={Baseline}, observedUsed={Used}, observedHandled={Handled}",
                TagDispatchConfirmationTimeoutSeconds, pending.ActionId, pending.TargetId,
                pending.BaselineSequence, actionState.LastUsedSequence, actionState.LastHandledSequence);
            pendingTagDispatch = null;
            nextActionUtc = now.AddSeconds(1);
        }

        if (condition[ConditionFlag.InFlight] || condition[ConditionFlag.Mounted])
        {
            TickTagLandingAndDismount(mark, now);
            return;
        }

        if (!CombatController.IsMarkInCombat(mark) ||
            CombatController.HpPercent(mark) > ActiveDistanceProfile.EngageHpPercent)
        {
            vnav.StopSafe();
            status = $"Tag requirement remains latched for pull cycle {pullCycle}; waiting for the full-HP/out-of-combat reset confirmation";
            return;
        }

        if (activeTagActionId == 0)
            activeTagActionId = combat.ResolveTagActionId();
        if (activeTagActionId == 0)
        {
            status = "Tag is required, but the current job has no supported ranged tag; bounded recovery remains active";
            return;
        }

        if (!combat.TargetMark(mark))
        {
            status = $"Acquiring {mark.Name.TextValue} as the confirmed tag target";
            return;
        }

        var clearance = ClearanceFromMark(mark);
        UpdateTagRecoveryProgress(mark, clearance, now);
        if (clearance <= TagApproachClearance + 1f)
        {
            vnav.StopSafe();
            if (now >= nextActionUtc)
            {
                var attempt = combat.TrySingleTag(activeTagActionId, mark);
                if (attempt.Submitted && attempt.ClientAccepted)
                {
                    pendingTagDispatch = new TagDispatch(
                        activeTagActionId,
                        mark.GameObjectId,
                        attempt.SequenceBefore,
                        attempt.SequenceAfter == attempt.SequenceBefore ? null : attempt.SequenceAfter,
                        attempt.DeferredForTarget,
                        now);
                    nextActionUtc = now.AddSeconds(1);
                    status = $"Ranged tag submitted to {mark.Name.TextValue}; waiting for server handling before closing the tag gate";
                    log.Information(
                        "Tag action submitted: {Mark}, pull cycle={PullCycle}, action={ActionId}, target={TargetId}, sequenceBefore={Before}, sequenceAfter={After}, deferredForTarget={Deferred}; awaiting handled sequence",
                        mark.Name.TextValue, pullCycle, activeTagActionId, mark.GameObjectId,
                        attempt.SequenceBefore, attempt.SequenceAfter, attempt.DeferredForTarget);
                    return;
                }

                if (attempt.Submitted)
                    log.Information(
                        "Tag action was rejected before dispatch; retaining the live hunt and retrying: {Mark}, action={ActionId}",
                        mark.Name.TextValue, activeTagActionId);
                nextActionUtc = now.AddSeconds(1);
            }
            status = $"Tag remains required for pull cycle {pullCycle}; retrying the same ranged action until server handling is confirmed";
            return;
        }

        var routeRunning = vnav.IsPathRunningSafe() || vnav.IsPathfindInProgressSafe();
        if (routeRunning && tagLastProgressUtc != DateTime.MinValue &&
            (now - tagLastProgressUtc).TotalSeconds >= TagRouteStallSeconds)
        {
            vnav.StopSafe("tag approach stalled without meaningful progress");
            tagRecoveryFailures++;
            tagLastProgressUtc = now;
            nextActionUtc = now;
            log.Warning(
                "Tag-required ground approach stalled; retrying without clearing the latch (failure {Failure}, HP={Hp:0.0}%)",
                tagRecoveryFailures, CombatController.HpPercent(mark));
            routeRunning = false;
        }

        if (!routeRunning && now >= nextActionUtc)
        {
            var desiredCenterRange = mark.HitboxRadius +
                                     (objects.LocalPlayer?.HitboxRadius ?? 0f) +
                                     TagApproachClearance;
            if (!vnav.MoveCloseToSafe(mark.Position, false, desiredCenterRange))
            {
                tagRecoveryFailures++;
                nextActionUtc = now.AddSeconds(1);
                status = $"Tag-required approach could not start; retry {tagRecoveryFailures} remains latched while the mark is alive";
                return;
            }

            tagLastProgressPosition = PlayerPosition();
            tagBestClearance = clearance;
            tagLastProgressUtc = now;
        }

        status = $"Tag required: approaching {mark.Name.TextValue} for one confirmed ranged action ({clearance:0.0}y clear)";
    }

    private void TickGroundRetreat(DateTime now)
    {
        mark = FindMark();
        if (mark is null)
        {
            vnav.StopSafe();
            SetState(SentinelState.LocateMark,
                "Mark left object range during retreat; stopped safely and beginning bounded reacquisition");
            return;
        }
        if (CheckParkingRecoveryBudget(mark, now))
            return;
        if (parkingPathTask is not null || parkingGroundPathTask is not null)
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
            SetState(SentinelState.SafeWait, tagAttempted ? "Confirmed tag completed; safe radius restored" : "Safe radius restored");
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

        if (combat.IsPlayerDead)
        {
            if (TryFindQueuedSsForActiveContext(out var queuedSs))
            {
                QueueSsAfterCompletedHunt(queuedSs, activeSsProfile!, now,
                    $"{queuedSs.CreatureName} is queued after the completed S rank");
                return;
            }

            var visibleSs = FindBattleNpc(activeSsProfile!.SsDataId, activeSsProfile.SsName);
            if (visibleSs is not null)
            {
                var detectedSs = BuildReservedSsWatch(current!, activeSsProfile, now);
                if (detectedSs is not null)
                    detectedSs = detectedSs with { HuntType = "ssrank" };
                QueueSsAfterCompletedHunt(detectedSs, activeSsProfile, now,
                    $"{activeSsProfile.SsName} became visible after the completed S rank");
                return;
            }

            if (now >= postKillSsGraceDeadlineUtc)
            {
                nextActionUtc = now;
                SetState(SentinelState.ResetToUldah,
                    $"No {activeSsProfile.ExpansionName} SS evidence within {config.PostKillSsGraceSeconds}s; " +
                    "returning to Ul'dah on the current world");
                return;
            }

            TickRaiseAcceptance(now,
                $"Dead during the {activeSsProfile.ExpansionName} SS grace; waiting briefly for SS evidence");
            return;
        }

        ScanForSsEvidence(now);
        if (state != SentinelState.PostKillSsGrace)
            return;

        if (now >= postKillSsGraceDeadlineUtc)
        {
            nextActionUtc = now;
            SetState(SentinelState.ResetToUldah,
                $"No {activeSsProfile!.ExpansionName} SS evidence within {config.PostKillSsGraceSeconds}s; " +
                "returning to Ul'dah on the current world");
            return;
        }

        var remaining = Math.Max(0, (postKillSsGraceDeadlineUtc - now).TotalSeconds);
        status = $"Post-kill SS check: {activeSsProfile!.ExpansionName}, {remaining:0.0}s remaining";
    }

    private void TickSsWatch(DateTime now)
    {
        if (!ValidateSsWatchContext("SS watch"))
            return;

        if (combat.IsPlayerDead)
        {
            var reason = ssSpawnAnnounced
                ? $"{activeSsProfile!.SsName} is spawning after the completed S rank"
                : $"{activeSsProfile!.PrecursorName} prey is active after the completed S rank";
            QueueSsAfterCompletedHunt(null, activeSsProfile, now, reason);
            return;
        }

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

        var visibleSs = FindBattleNpc(activeSsProfile.SsDataId, activeSsProfile.SsName);
        if (visibleSs is not null)
        {
            HuntAlertSnapshot ss;
            if (TryFindQueuedSsForActiveContext(out var queuedSs))
            {
                var survivors = pendingAlerts.Where(alert => alert.Key != queuedSs.Key).ToArray();
                pendingAlerts.Clear();
                foreach (var survivor in survivors)
                    pendingAlerts.Enqueue(survivor);
                PersistQueue();
                ss = queuedSs;
            }
            else
            {
                ss = new HuntAlertSnapshot(
                    "ssrank", current.World, activeSsProfile.SsName, current.TerritoryId,
                    activeSsProfile.SsDataId, current.PreferredAetheryteId,
                    travel.CurrentInstance > 0 ? travel.CurrentInstance : current.Instance,
                    0f, 0f, now);
            }
            log.Information("Actual SS detected; switching to entity tracking: {Ss}", activeSsProfile.SsName);
            StartSsAlertDirect(ss, "game object scan");
            return;
        }

        if (TryFindQueuedSsForActiveContext(out var reportedSs))
        {
            ssSpawnAnnounced = true;
            ssWatchDeadlineUtc = DateTime.MaxValue;
            status = $"{reportedSs.CreatureName} was reported; holding its reservation and staging at the documented spawn until the entity is detectable";
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
                if (ssStagingLandingStartedUtc != DateTime.MinValue &&
                    (now - ssStagingLandingStartedUtc).TotalSeconds >= LandingAttemptTimeoutSeconds)
                {
                    log.Information(
                        "SS staging landing did not complete within {Seconds}s; rejecting the candidate and preserving the SS watch",
                        LandingAttemptTimeoutSeconds);
                    InvalidateSsStagingRoute(now);
                    status = "SS staging landing was obstructed; trying another connected-ground point";
                    return;
                }
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

        if (ssStagingPathTask is not null || ssStagingGroundPathTask is not null)
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
                ssStagingLandingStartedUtc = now;
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
        ssStagingGroundPathTask = null;
        pendingSsStagingFlightPath = null;
        ssStagingGroundPathGoal = default;
        selectedSsStagingPath = null;
        ssStagingDestination = null;

        if (!ssStagingProjectionFailureLogged && vnav.PointOnFloorSafe(anchor, 10f) is null)
        {
            ssStagingProjectionFailureLogged = true;
            log.Information("Fixed SS coordinate projection failed; trying nearby candidate");
        }

        var player = PlayerPosition();
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
            foreach (var lateral in new[] { 2f, -2f, 4f, -4f })
            {
                var offset = towardCrowd * SsStagingTargetRadius + tangent * lateral;
                offset = Vector3.Normalize(offset) * SsStagingTargetRadius;
                var candidate = anchor + offset;
                candidate.Y = 1024f;
                var projected = vnav.PointOnFloorSafe(candidate, 8f);
                var anchorDistance = projected is null
                    ? 0f
                    : HorizontalDistance(projected.Value, anchor);
                if (projected is null ||
                    MathF.Abs(anchorDistance - SsStagingTargetRadius) > SsStagingRadiusTolerance ||
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
        foreach (var radius in new[] { SsStagingTargetRadius, SsStagingTargetRadius - 1f, SsStagingTargetRadius + 1f })
        {
            foreach (var angle in new[] { 0f, 25f, -25f, 50f, -50f, 80f, -80f, 110f, -110f, 145f, -145f, 180f })
            {
                var radians = angle * MathF.PI / 180f;
                var direction = new Vector3(
                    away.X * MathF.Cos(radians) - away.Z * MathF.Sin(radians),
                    0f,
                    away.X * MathF.Sin(radians) + away.Z * MathF.Cos(radians));
                var candidate = anchor + direction * radius;
                candidate.Y = 1024f;
                var projected = vnav.PointOnFloorSafe(candidate, 8f);
                var anchorDistance = projected is null
                    ? 0f
                    : HorizontalDistance(projected.Value, anchor);
                if (projected is null ||
                    MathF.Abs(anchorDistance - SsStagingTargetRadius) > SsStagingRadiusTolerance ||
                    accepted.Any(point => HorizontalDistance(point, projected.Value) < 2f))
                    continue;
                accepted.Add(projected.Value);
                ssStagingCandidates.Enqueue(new SsStagingCandidate(projected.Value, false, 0, Vector3.Zero));
            }
        }

        log.Information(
            "Prepared {Count} SS staging candidates in the documented {Radius:0}y spawn envelope near {Ss} ({CrowdCount} crowd-aware)",
            ssStagingCandidates.Count, SsStagingTargetRadius,
            activeSsProfile?.SsName ?? "SS", crowdCandidates.Count);
    }

    private bool TryStartNextSsStagingRoute(DateTime now, Vector3 anchor)
    {
        var protectedRadius = SsStagingProtectedRadius;
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
            ssStagingGroundPathTask = null;
            pendingSsStagingFlightPath = null;
            ssStagingGroundPathGoal = default;
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
        var candidate = selectedSsStagingCandidate;
        if (candidate is null)
            return;

        if (ssStagingGroundPathTask is not null)
        {
            var groundTask = ssStagingGroundPathTask;
            if (!groundTask.IsCompleted)
            {
                if ((now - ssStagingPathStartedUtc).TotalSeconds <= SsStagingPathQueryTimeoutSeconds)
                {
                    status = "Checking that the SS staging ground is connected to the documented spawn side";
                    return;
                }
                RejectSsStagingCandidate(now, anchor,
                    "SS staging candidate rejected: ground-connectivity query timed out");
                return;
            }

            List<Vector3>? groundPath = null;
            try
            {
                if (groundTask.IsCompletedSuccessfully)
                    groundPath = groundTask.Result;
            }
            catch (Exception ex)
            {
                log.Debug(ex, "SS staging ground-connectivity query failed");
            }
            ssStagingGroundPathTask = null;

            if (!IsReasonableGroundConnection(candidate.Position, ssStagingGroundPathGoal, groundPath,
                    out var groundReason))
            {
                RejectSsStagingCandidate(now, anchor,
                    $"SS staging candidate rejected: {groundReason}");
                return;
            }

            var flightPath = pendingSsStagingFlightPath;
            if (flightPath is null || flightPath.Count == 0 || !vnav.MovePathSafe(flightPath, true))
            {
                RejectSsStagingCandidate(now, anchor,
                    "SS staging candidate rejected: validated flight route became unavailable");
                return;
            }

            pendingSsStagingFlightPath = null;
            ssStagingDestination = candidate.Position;
            selectedSsStagingPath = flightPath;
            nextSsStagingAttemptUtc = now.AddSeconds(SsStagingRouteRetrySeconds);
            status = candidate.IsCrowd
                ? $"Navigating to connected-ground SS staging near {candidate.CrowdPopulation} players"
                : "Navigating to connected-ground SS staging area";
            log.Information(
                "SS staging candidate selected after flight and ground-connectivity validation: {Type}, {Distance:0.0}y from documented spawn",
                candidate.IsCrowd ? $"crowd ({candidate.CrowdPopulation} players)" : "standard",
                HorizontalDistance(candidate.Position, anchor));
            return;
        }

        var task = ssStagingPathTask;
        if (task is null)
            return;
        if (!task.IsCompleted)
        {
            if ((now - ssStagingPathStartedUtc).TotalSeconds <= SsStagingPathQueryTimeoutSeconds)
            {
                status = "Validating a protected vnavmesh route to the fixed SS staging area";
                return;
            }
            RejectSsStagingCandidate(now, anchor,
                "SS staging candidate rejected: protected flight-path query timed out");
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

        var protectedRadius = SsStagingProtectedRadius;
        if (path is null || path.Count == 0 ||
            !ProtectedSegmentIsSafe(PlayerPosition(), candidate.Position, anchor, protectedRadius, true) ||
            !PathStaysOutsideProtectedRadius(PlayerPosition(), path, anchor, protectedRadius, true))
        {
            RejectSsStagingCandidate(now, anchor,
                "SS staging candidate rejected: no protected flight route to the documented spawn envelope");
            return;
        }

        var groundAnchor = new Vector3(anchor.X, 1024f, anchor.Z);
        var groundGoal = vnav.PointOnFloorSafe(groundAnchor, 10f);
        var groundPathTask = groundGoal is null
            ? null
            : vnav.PathfindSafe(candidate.Position, groundGoal.Value, false);
        if (groundGoal is null || groundPathTask is null)
        {
            RejectSsStagingCandidate(now, anchor,
                "SS staging candidate rejected: could not start a ground-connectivity query toward the spawn side");
            return;
        }

        pendingSsStagingFlightPath = path;
        ssStagingGroundPathGoal = groundGoal.Value;
        ssStagingGroundPathTask = groundPathTask;
        ssStagingPathStartedUtc = now;
        status = "Protected flight route found; validating connected landing ground before moving";
    }

    private void RejectSsStagingCandidate(DateTime now, Vector3 anchor, string reason)
    {
        log.Information("{Reason}", reason);
        status = reason;
        ssStagingPathTask = null;
        ssStagingGroundPathTask = null;
        pendingSsStagingFlightPath = null;
        ssStagingGroundPathGoal = default;
        selectedSsStagingCandidate = null;
        if (!TryStartNextSsStagingRoute(now, anchor))
            nextSsStagingAttemptUtc = now.AddSeconds(SsStagingRouteRetrySeconds);
    }

    private bool RevalidateSsStagingDestination(Vector3 anchor, out string reason)
    {
        var candidate = selectedSsStagingCandidate;
        if (candidate is null || ssStagingDestination is null)
        {
            reason = "SS staging destination was lost; sampling another nearby point";
            return false;
        }

        if (MathF.Abs(HorizontalDistance(candidate.Position, anchor) - SsStagingTargetRadius) >
            SsStagingRadiusTolerance)
        {
            reason = $"SS staging destination left the documented {SsStagingTargetRadius:0}y spawn envelope; resampling";
            return false;
        }

        var protectedRadius = SsStagingProtectedRadius;
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
        ssStagingGroundPathTask = null;
        pendingSsStagingFlightPath = null;
        ssStagingGroundPathGoal = default;
        selectedSsStagingPath = null;
        ssStagingArrived = false;
        ssStagingLandingStartedUtc = DateTime.MinValue;
        nextSsStagingAttemptUtc = now.AddSeconds(SsStagingRouteRetrySeconds);
    }

    private string SsWatchRemaining(DateTime now) => ssSpawnAnnounced
        ? "spawn announced; timeout disabled"
        : $"{Math.Max(0, (int)Math.Ceiling((ssWatchDeadlineUtc - now).TotalSeconds))}s remaining";

    private void BeginSafeParking(IBattleChara target, bool fly)
    {
        LatchTagRequirement(target, DateTime.UtcNow);
        if (tagRequired)
        {
            BeginTagRequiredRecovery(target, DateTime.UtcNow,
                "Tag gate is open; suspending safe parking until the ranged tag is confirmed");
            return;
        }
        if (!vnav.IsReadySafe())
        {
            BeginMeshWait("vnavmesh readiness was lost before dynamic safe parking");
            return;
        }
        if (parkingRecoveryStartedUtc == DateTime.MinValue)
            parkingRecoveryStartedUtc = DateTime.UtcNow;
        if (CheckParkingRecoveryBudget(target, DateTime.UtcNow))
            return;
        if (fly && !EnsureMounted(DateTime.UtcNow))
            return;
        PrepareParkingCandidates(target, ActiveDistanceProfile.WaitingDistance, preferCrowd: true);
        if (!TryStartNextParkingRoute(fly, target))
        {
            vnav.StopSafe();
            if (RegisterParkingRecoveryFailure(target,
                    "no sampled safe parking route was available", DateTime.UtcNow))
                return;
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
        if (tagRequired && !pullCycleTagged && !tagAttempted)
        {
            BeginTagRequiredRecovery(target, DateTime.UtcNow,
                "Tag remains required; ordinary retreat cannot replace the pending ranged tag");
            return;
        }
        postTagRetreatActive = postTag;
        if (parkingRecoveryStartedUtc == DateTime.MinValue)
            parkingRecoveryStartedUtc = DateTime.UtcNow;
        PrepareParkingCandidates(target, ActiveDistanceProfile.WaitingDistance,
            preferCrowd: postTag, randomizedRetreat: postTag);
        if (!TryStartNextParkingRoute(false, target))
        {
            vnav.StopSafe();
            postTagRetreatActive = false;
            if (!postTag && RegisterParkingRecoveryFailure(target,
                    "no sampled ground-retreat route was available", DateTime.UtcNow))
                return;
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
        parkingGroundPathTask = null;
        parkingPathStartedUtc = DateTime.MinValue;
        pendingParkingFlightPath = null;
        parkingGroundPathGoal = default;
        selectedParkingPath = null;
        crowdFallbackAnnounced = !preferCrowd;
        parkingDeviationLogged = false;
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
            ? [Random.Shared.NextSingle() * 2f, Random.Shared.NextSingle() * 2f + 2f]
            : [0f, 8f];
        var hitboxPadding = target.HitboxRadius + (objects.LocalPlayer?.HitboxRadius ?? 0f);
        var centerRadius = clearance + hitboxPadding;
        var minimumCenterDistance = ActiveDistanceProfile.EmergencyDistance + hitboxPadding + 3f;
        var accepted = new List<Vector3>();
        var rankedCandidates = new List<(ParkingCandidate Candidate, float ClearanceError, float Score)>();
        var crowdCandidateCount = 0;

        if (preferCrowd)
        {
            var clusters = DetectPlayerClusters(target);
            log.Information("Detected {ClusterCount} player clusters near {Mark}", clusters.Count, target.Name.TextValue);

            foreach (var cluster in clusters)
            {
                var towardCrowd = cluster.Center - target.Position;
                towardCrowd.Y = 0;
                if (towardCrowd.LengthSquared() < 0.01f)
                    continue;
                towardCrowd = Vector3.Normalize(towardCrowd);
                var tangent = new Vector3(-towardCrowd.Z, 0f, towardCrowd.X);
                var crowdCenterRadius = HorizontalDistance(cluster.Center, target.Position);

                // Park beside the crowd instead of on its centroid. Retain the crowd's natural
                // radius only when the resulting point remains inside the 35y waiting envelope.
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
                        VerticalSeparation(projected.Value, target.Position) > ParkingMaximumVerticalSeparation ||
                        ClearanceAtPoint(projected.Value, target) < clearance - 0.5f ||
                        ClearanceAtPoint(projected.Value, target) > MaximumParkingClearance + 0.5f ||
                        HorizontalDistance(projected.Value, cluster.Center) > CrowdRevalidationRadius ||
                        accepted.Any(existing => HorizontalDistance(existing, projected.Value) < 2f))
                        continue;

                    var candidateClearance = ClearanceAtPoint(projected.Value, target);
                    var clearanceError = HuntProgressPolicy.ParkingClearanceError(
                        candidateClearance, clearance);
                    // Clearance is the dominant term. Crowd size only breaks close calls, and a
                    // tiny per-process jitter lets two clients choose different equally safe points.
                    var score = clearanceError * 100f +
                                cluster.Tightness * 3f +
                                HorizontalDistance(projected.Value, cluster.Center) * 2f +
                                HorizontalDistance(player, projected.Value) * 0.2f -
                                cluster.Population * 8f +
                                Random.Shared.NextSingle() * 20f;
                    var parking = new ParkingCandidate(projected.Value, true, cluster.Population,
                        cluster.Center, true, randomizedRetreat);
                    rankedCandidates.Add((parking, clearanceError, score));
                    accepted.Add(projected.Value);
                    crowdCandidateCount++;
                }
            }

            if (crowdCandidateCount == 0)
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
                    VerticalSeparation(projected.Value, target.Position) > ParkingMaximumVerticalSeparation ||
                    HorizontalDistance(projected.Value, target.Position) < minimumCenterDistance ||
                    ClearanceAtPoint(projected.Value, target) < clearance - 0.5f ||
                    ClearanceAtPoint(projected.Value, target) > MaximumParkingClearance + 0.5f)
                    continue;
                if (accepted.Any(point => HorizontalDistance(point, projected.Value) < 2f))
                    continue;
                accepted.Add(projected.Value);
                var candidateClearance = ClearanceAtPoint(projected.Value, target);
                var clearanceError = HuntProgressPolicy.ParkingClearanceError(
                    candidateClearance, clearance);
                var score = clearanceError * 100f +
                            HorizontalDistance(player, projected.Value) * 0.2f +
                            Random.Shared.NextSingle() * 20f;
                rankedCandidates.Add((
                    new ParkingCandidate(projected.Value, false, 0, Vector3.Zero,
                        true, randomizedRetreat),
                    clearanceError,
                    score));
            }
        }

        foreach (var entry in rankedCandidates
                     .OrderBy(entry => entry.ClearanceError <= ParkingPreferredClearanceTolerance ? 0 : 1)
                     .ThenBy(entry => entry.Score))
            parkingCandidates.Enqueue(entry.Candidate);

        var preferredCount = rankedCandidates.Count(entry =>
            entry.ClearanceError <= ParkingPreferredClearanceTolerance);
        log.Information(
            "Prepared {Count} parking candidate(s) for preferred {Preferred:0.0}y clearance: {PreferredCount} within ±{Tolerance:0.0}y, {CrowdCount} crowd-derived",
            rankedCandidates.Count, clearance, preferredCount, ParkingPreferredClearanceTolerance,
            crowdCandidateCount);
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
                parkingLastProgressPosition = PlayerPosition();
                parkingLastProgressUtc = DateTime.UtcNow;
                return true;
            }

            var protectedRadius = ProtectedCenterRadius(target);
            var startDistance = HorizontalDistance(PlayerPosition(), target.Position);
            var allowOutwardEscape = startDistance < protectedRadius;
            if (!ProtectedSegmentIsSafe(PlayerPosition(), candidate.Position, target.Position, protectedRadius,
                    allowOutwardEscape))
            {
                var rejection = candidate.IsRandomizedRetreat
                    ? "Retreat candidate rejected: crosses safety radius"
                    : $"Rejected {(candidate.IsCrowd ? "crowd" : "geometric")} parking candidate: route crosses mark safety radius";
                log.Information("{Rejection}", rejection);
                status = rejection;
                continue;
            }

            var queryAvoidRadius = allowOutwardEscape
                ? MathF.Max(1f, startDistance - 1f)
                : protectedRadius;
            var pathTask = vnav.PathfindAvoidSafe(
                PlayerPosition(), candidate.Position, fly, target.Position, queryAvoidRadius);
            if (pathTask is null)
            {
                log.Information(candidate.IsRandomizedRetreat
                    ? "Retreat candidate rejected: vnavmesh could not start a protected path query"
                    : $"Rejected {(candidate.IsCrowd ? "crowd" : "geometric")} parking candidate: vnavmesh could not start a protected path query");
                continue;
            }

            selectedParkingCandidate = candidate;
            parkingPathTask = pathTask;
            parkingGroundPathTask = null;
            pendingParkingFlightPath = null;
            parkingGroundPathGoal = default;
            parkingPathUsesFlight = fly;
            parkingPathStartedUtc = DateTime.UtcNow;
            safePoint = null;
            selectedParkingPath = null;
            status = candidate.IsRandomizedRetreat
                ? "Validating randomized post-tag retreat route"
                : candidate.IsCrowd
                    ? $"Validating a protected route toward a {candidate.CrowdPopulation}-player crowd"
                    : "Validating a protected geometric parking route";
            return true;
        }
        safePoint = null;
        selectedParkingCandidate = null;
        parkingPathTask = null;
        parkingGroundPathTask = null;
        pendingParkingFlightPath = null;
        parkingGroundPathGoal = default;
        selectedParkingPath = null;
        return false;
    }

    private void PollCrowdParkingPath(IBattleChara target, DateTime now)
    {
        var candidate = selectedParkingCandidate;
        if (candidate is null || !candidate.RequiresProtectedRoute)
            return;

        if (parkingGroundPathTask is not null)
        {
            var groundTask = parkingGroundPathTask;
            if (!groundTask.IsCompleted)
            {
                if ((now - parkingPathStartedUtc).TotalSeconds <= CrowdPathQueryTimeoutSeconds)
                {
                    status = "Checking that the parking ground connects to the hunt side without a long detour";
                    return;
                }
                RejectParkingCandidate(target, now,
                    "Parking candidate rejected: ground-connectivity query timed out");
                return;
            }

            List<Vector3>? groundPath = null;
            try
            {
                if (groundTask.IsCompletedSuccessfully)
                    groundPath = groundTask.Result;
            }
            catch (Exception ex)
            {
                log.Debug(ex, "Parking ground-connectivity query failed");
            }
            parkingGroundPathTask = null;

            if (!IsReasonableGroundConnection(candidate.Position, parkingGroundPathGoal, groundPath,
                    out var groundReason))
            {
                RejectParkingCandidate(target, now, $"Parking candidate rejected: {groundReason}");
                return;
            }

            var flightPath = pendingParkingFlightPath;
            if (flightPath is null || flightPath.Count == 0)
            {
                RejectParkingCandidate(target, now,
                    "Parking candidate rejected: validated flight route was lost");
                return;
            }

            pendingParkingFlightPath = null;
            AcceptParkingPath(target, candidate, flightPath, now);
            return;
        }

        var task = parkingPathTask;
        if (task is null)
            return;

        if (!task.IsCompleted)
        {
            if ((now - parkingPathStartedUtc).TotalSeconds <= CrowdPathQueryTimeoutSeconds)
            {
                status = candidate.IsRandomizedRetreat
                    ? "Validating randomized post-tag retreat route"
                    : candidate.IsCrowd
                        ? $"Validating a protected route toward a {candidate.CrowdPopulation}-player crowd"
                        : "Validating a protected geometric parking route";
                return;
            }

            RejectParkingCandidate(target, now, candidate.IsRandomizedRetreat
                ? "Retreat candidate rejected: protected vnavmesh path query timed out"
                : $"Rejected {(candidate.IsCrowd ? "crowd" : "geometric")} parking candidate: protected vnavmesh path query timed out");
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
            RejectParkingCandidate(target, now, candidate.IsRandomizedRetreat
                ? "Retreat candidate rejected: vnavmesh found no protected route"
                : $"Rejected {(candidate.IsCrowd ? "crowd" : "geometric")} parking candidate: vnavmesh found no protected route");
            return;
        }

        var protectedRadius = ProtectedCenterRadius(target);
        var startDistance = HorizontalDistance(PlayerPosition(), target.Position);
        var allowOutwardEscape = startDistance < protectedRadius;
        if (!ProtectedSegmentIsSafe(PlayerPosition(), candidate.Position, target.Position, protectedRadius,
                allowOutwardEscape) ||
            !PathStaysOutsideProtectedRadius(PlayerPosition(), path, target.Position, protectedRadius,
                allowOutwardEscape))
        {
            var rejection = candidate.IsRandomizedRetreat
                ? "Retreat candidate rejected: crosses safety radius"
                : $"Rejected {(candidate.IsCrowd ? "crowd" : "geometric")} parking candidate: route crosses mark safety radius";
            RejectParkingCandidate(target, now, rejection);
            return;
        }

        if (ClearanceAtPoint(candidate.Position, target) < ActiveDistanceProfile.WaitingDistance - 0.5f ||
            ClearanceAtPoint(candidate.Position, target) > MaximumParkingClearance + 0.5f)
        {
            RejectParkingCandidate(target, now, candidate.IsRandomizedRetreat
                ? "Retreat candidate rejected: destination or protected route became unavailable"
                : $"Rejected {(candidate.IsCrowd ? "crowd" : "geometric")} parking candidate: destination or protected route became unavailable");
            return;
        }

        if (parkingPathUsesFlight)
        {
            if (!TryStartParkingGroundConnectivityQuery(candidate, target, path, now))
            {
                RejectParkingCandidate(target, now,
                    "Parking candidate rejected: could not validate connected ground on the hunt side");
            }
            return;
        }

        AcceptParkingPath(target, candidate, path, now);
    }

    private bool TryStartParkingGroundConnectivityQuery(
        ParkingCandidate candidate,
        IBattleChara target,
        List<Vector3> flightPath,
        DateTime now)
    {
        var away = candidate.Position - target.Position;
        away.Y = 0f;
        if (away.LengthSquared() < 0.01f)
            return false;
        away = Vector3.Normalize(away);
        var tagCenterRange = target.HitboxRadius + (objects.LocalPlayer?.HitboxRadius ?? 0f) +
                             TagApproachClearance;
        var intendedGoal = target.Position + away * tagCenterRange;
        intendedGoal.Y = 1024f;
        var groundGoal = vnav.PointOnFloorSafe(intendedGoal, 8f);
        var groundTask = groundGoal is null
            ? null
            : vnav.PathfindSafe(candidate.Position, groundGoal.Value, false);
        if (groundGoal is null || groundTask is null)
            return false;

        pendingParkingFlightPath = flightPath;
        parkingGroundPathGoal = groundGoal.Value;
        parkingGroundPathTask = groundTask;
        parkingPathStartedUtc = now;
        status = "Protected flight route found; validating connected landing ground before moving";
        return true;
    }

    private void AcceptParkingPath(
        IBattleChara target,
        ParkingCandidate candidate,
        List<Vector3> path,
        DateTime now)
    {
        if (!vnav.MovePathSafe(path, parkingPathUsesFlight))
        {
            RejectParkingCandidate(target, now,
                "Parking candidate rejected: validated route became unavailable before movement");
            return;
        }

        safePoint = candidate.Position;
        selectedParkingPath = path;
        parkingLastProgressPosition = PlayerPosition();
        parkingLastProgressUtc = now;
        parkingPathStartedUtc = DateTime.MinValue;
        var clearance = ClearanceAtPoint(candidate.Position, target);
        var clearanceError = HuntProgressPolicy.ParkingClearanceError(
            clearance, ActiveDistanceProfile.WaitingDistance);
        if (clearanceError > ParkingPreferredClearanceTolerance && !parkingDeviationLogged)
        {
            parkingDeviationLogged = true;
            log.Warning(
                "Parking deviates from configured preference: selected={Selected:0.0}y, preferred={Preferred:0.0}y, difference={Difference:0.0}y because closer candidates failed projection/path/ground-safety validation",
                clearance, ActiveDistanceProfile.WaitingDistance, clearanceError);
        }
        if (candidate.IsRandomizedRetreat)
        {
            status = $"Post-tag retreat target: {clearance:0}y from mark";
            log.Information("Post-tag retreat: {CandidateType} candidate selected; target {Clearance:0}y from mark",
                candidate.IsCrowd ? "crowd" : "standard", clearance);
        }
        else if (candidate.IsCrowd)
        {
            status = $"Selected crowd parking candidate: {candidate.CrowdPopulation} players, {clearance:0}y from mark";
            log.Information("Selected crowd parking candidate: {Players} players, {Clearance:0}y from mark",
                candidate.CrowdPopulation, clearance);
        }
        else
        {
            status = $"Selected protected geometric parking candidate: {clearance:0}y from mark";
            log.Information("Selected protected geometric parking candidate: {Clearance:0}y from mark", clearance);
        }
    }

    private void RejectParkingCandidate(IBattleChara target, DateTime now, string reason)
    {
        log.Information("{Reason}", reason);
        status = reason;
        parkingPathTask = null;
        parkingGroundPathTask = null;
        pendingParkingFlightPath = null;
        parkingGroundPathGoal = default;
        selectedParkingCandidate = null;
        ContinueParkingAfterCrowdRejection(target, now);
    }

    private void ContinueParkingAfterCrowdRejection(IBattleChara target, DateTime now)
    {
        var fly = parkingPathUsesFlight;
        safePoint = null;
        selectedParkingPath = null;
        parkingPathTask = null;
        parkingGroundPathTask = null;
        pendingParkingFlightPath = null;
        parkingGroundPathGoal = default;
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
        if (RegisterParkingRecoveryFailure(target,
                "all protected parking candidates failed route or ground-connectivity validation", now))
            return;
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
            ClearanceAtPoint(candidate.Position, target) > MaximumParkingClearance + 0.5f ||
            VerticalSeparation(candidate.Position, target.Position) > ParkingMaximumVerticalSeparation ||
            ClearanceFromMark(target) < ActiveDistanceProfile.EmergencyDistance)
        {
            reason = "Mark movement invalidated parking clearance or vertical reach; resampling safely";
            return false;
        }

        var protectedRadius = ProtectedCenterRadius(target);
        if (candidate.RequiresProtectedRoute &&
            (HorizontalSegmentDistance(PlayerPosition(), candidate.Position, target.Position) < protectedRadius ||
             !FinalApproachStaysOutsideProtectedRadius(selectedParkingPath, target.Position, protectedRadius)))
        {
            reason = "Parking final approach now crosses the mark safety radius; resampling safely";
            return false;
        }

        if (!candidate.IsCrowd)
        {
            reason = string.Empty;
            return true;
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

    private bool RegisterParkingRecoveryFailure(IBattleChara target, string reason, DateTime now)
    {
        if (tagRequired && !pullCycleTagged && !tagAttempted)
        {
            BeginTagRequiredRecovery(target, now,
                $"Parking/landing recovery failed while a tag is required: {reason}");
            return true;
        }

        // Once credit is already secured, holding safely for the confirmed death is preferable
        // to abandoning a live hunt only because an optional retreat point was unavailable.
        if (pullCycleTagged && tagAttempted)
            return false;

        if (parkingRecoveryStartedUtc == DateTime.MinValue)
            parkingRecoveryStartedUtc = now;
        parkingRecoveryFailures++;
        var elapsed = (now - parkingRecoveryStartedUtc).TotalSeconds;
        log.Warning(
            "Parking/landing recovery failure {Failure}/{Limit} after {Elapsed:0.0}s for {Mark}: {Reason}",
            parkingRecoveryFailures, MaximumParkingRecoveryFailures, elapsed,
            current?.CreatureName ?? target.Name.TextValue, reason);
        if (parkingRecoveryFailures < MaximumParkingRecoveryFailures &&
            elapsed < ParkingRecoveryBudgetSeconds)
            return false;

        FailCurrent(
            $"parking/landing remained unresolved after {parkingRecoveryFailures} failures and {elapsed:0}s ({reason})",
            HuntExitRequestSource.RecoveryBudget);
        return true;
    }

    private bool CheckParkingRecoveryBudget(IBattleChara target, DateTime now)
    {
        if (parkingRecoveryStartedUtc == DateTime.MinValue ||
            pullCycleTagged && tagAttempted ||
            (now - parkingRecoveryStartedUtc).TotalSeconds < ParkingRecoveryBudgetSeconds)
            return false;

        if (tagRequired)
        {
            BeginTagRequiredRecovery(target, now,
                "Parking/landing recovery budget expired while the tag latch was active");
            return true;
        }

        FailCurrent(
            $"parking/landing made no terminal progress within {ParkingRecoveryBudgetSeconds:0}s; the hunt is unresolved, not dead",
            HuntExitRequestSource.RecoveryBudget);
        return true;
    }

    private void ResetParkingRecoveryTracking()
    {
        parkingRecoveryStartedUtc = DateTime.MinValue;
        parkingRecoveryFailures = 0;
        parkingDeviationLogged = false;
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
        return Math.Max(0f, ActiveDistanceProfile.WaitingDistance - 0.5f) +
               target.HitboxRadius + playerRadius;
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

    private static bool IsReasonableGroundConnection(
        Vector3 start,
        Vector3 goal,
        List<Vector3>? path,
        out string reason)
    {
        if (path is null || path.Count == 0)
        {
            reason = "vnavmesh found no ground path to the hunt side";
            return false;
        }

        var pathLength = 0f;
        var previous = start;
        foreach (var waypoint in path)
        {
            pathLength += Vector3.Distance(previous, waypoint);
            previous = waypoint;
        }

        var endpointGap = HorizontalDistance(previous, goal);
        if (endpointGap > MaximumGroundPathEndpointGap)
        {
            reason = $"ground path ended {endpointGap:0.0}y short of the hunt side";
            return false;
        }

        var directDistance = MathF.Max(1f, Vector3.Distance(start, goal));
        var maximumReasonableLength = MathF.Max(
            directDistance * MaximumGroundPathDetourRatio,
            directDistance + MaximumGroundPathDetourAllowance);
        if (pathLength > maximumReasonableLength)
        {
            reason = $"ground path detours {pathLength:0.0}y for a {directDistance:0.0}y direct approach";
            return false;
        }

        reason = string.Empty;
        return true;
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
        if (current is null ||
            (territoryId != 0 && current.TerritoryId != territoryId))
            return false;
        var alertWorld = string.IsNullOrWhiteSpace(world) ? travel.CurrentWorld : world.Trim();
        var currentInstance = travel.CurrentInstance > 0 ? travel.CurrentInstance : current.Instance;
        return current.World.Equals(alertWorld, StringComparison.OrdinalIgnoreCase) &&
               (instance <= 0 || currentInstance == Math.Max(1, instance));
    }

    private void ObserveOrLatchSsChain(SsProfile profile, string reason)
    {
        if (current is null ||
            HuntCatalog.GetSsProfileForTerritory(current.TerritoryId) != profile)
            return;

        if ((state is SentinelState.PostKillSsGrace or SentinelState.SsWatch) && activeSsProfile == profile)
        {
            ObserveSsChain(profile, reason);
            return;
        }

        if (state == SentinelState.ResetToUldah && killConfirmed &&
            activeSsProfile == profile)
        {
            QueueSsAfterCompletedHunt(null, profile, DateTime.UtcNow, reason);
            return;
        }

        // The zone-wide precursor line can be delivered before the reward/death line that closes
        // the normal S hunt. Reserve it briefly across that callback ordering boundary. Territory,
        // World, instance/provider matching, and normal-S identity prevent unrelated chains from
        // being carried into another hunt; positive kill evidence is still required before SS watch.
        if (!HuntCatalog.IsSupportedNormalS(current.TerritoryId, current.CreatureName) ||
            clientState.TerritoryType != current.TerritoryId ||
            !travel.CurrentWorld.Equals(current.World, StringComparison.OrdinalIgnoreCase))
            return;

        pendingSsChainEvidenceUtc = DateTime.UtcNow;
        pendingSsChainAlertKey = current.Key;
        pendingSsChainReason = reason;
        status = $"{reason}; reserving this SS follow-up until the S-rank kill is confirmed";
        log.Information(
            "Latched SS precursor evidence across the kill transition: {Ss}; alert={AlertKey}; reason={Reason}",
            profile.SsName, current.Key, reason);
    }

    private bool TryConsumePendingSsChainEvidence(DateTime now, out string reason)
    {
        reason = pendingSsChainReason;
        var matches = current is not null &&
                      pendingSsChainAlertKey == current.Key &&
                      pendingSsChainEvidenceUtc != DateTime.MinValue &&
                      now - pendingSsChainEvidenceUtc <= TimeSpan.FromSeconds(SsChainKillTransitionLatchSeconds);
        ClearPendingSsChainEvidence();
        return matches;
    }

    private void ClearPendingSsChainEvidence()
    {
        pendingSsChainEvidenceUtc = DateTime.MinValue;
        pendingSsChainAlertKey = string.Empty;
        pendingSsChainReason = string.Empty;
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

    private bool TryFindQueuedSsForActiveContext(out HuntAlertSnapshot queuedSs)
    {
        queuedSs = null!;
        if (current is null || activeSsProfile is null)
            return false;
        queuedSs = pendingAlerts.FirstOrDefault(alert =>
            alert.World.Equals(current.World, StringComparison.OrdinalIgnoreCase) &&
            alert.TerritoryId == current.TerritoryId &&
            HuntCatalog.IsSsName(alert.CreatureName, activeSsProfile))!;
        return queuedSs is not null;
    }

    private void QueueSsAfterCompletedHunt(
        HuntAlertSnapshot? reportedSs,
        SsProfile profile,
        DateTime now,
        string reason)
    {
        if (current is null || !killConfirmed)
            return;

        var ssAlert = reportedSs ?? BuildReservedSsWatch(current, profile, now);
        if (ssAlert is not null && HuntCatalog.TryGetSsStagingLocation(current.TerritoryId, out var location))
            ssAlert = ssAlert with { MapX = location.MapX, MapY = location.MapY };
        if (ssAlert is not null && reportedSs is null && ssSpawnAnnounced)
            ssAlert = ssAlert with { HuntType = "ssrank" };
        if (ssAlert is null)
        {
            log.Warning(
                "Could not reserve {Ss} after completed {Mark}: fixed SS staging coordinates are unavailable",
                profile.SsName, current.CreatureName);
            nextActionUtc = now;
            SetState(SentinelState.ResetToUldah,
                $"{reason}; returning after the completed S rank while waiting for an SS alert");
            return;
        }

        var existing = pendingAlerts.FirstOrDefault(alert => alert.Key == ssAlert.Key);
        if (existing is null)
        {
            EnqueuePersistent(ssAlert);
        }
        else if (IsReservedSsWatch(existing) && !IsReservedSsWatch(ssAlert))
        {
            var updatedQueue = pendingAlerts
                .Select(alert => alert.Key == ssAlert.Key ? ssAlert : alert)
                .ToArray();
            pendingAlerts.Clear();
            foreach (var alert in updatedQueue)
                pendingAlerts.Enqueue(alert);
            ReorderPendingQueue();
            PersistQueue();
        }

        nextActionUtc = now;
        SetState(SentinelState.ResetToUldah,
            $"{reason}; {profile.SsName} is reserved next, returning to Ul'dah before traveling back");
        log.Information(
            "Post-completion SS handoff reserved {Ss} ahead of ordinary S ranks after positive completion of {Mark}",
            profile.SsName, current.CreatureName);
    }

    private static HuntAlertSnapshot? BuildReservedSsWatch(
        HuntAlertSnapshot completedS,
        SsProfile profile,
        DateTime now)
    {
        if (!HuntCatalog.TryGetSsStagingLocation(completedS.TerritoryId, out var location))
            return null;
        var definition = HuntCatalog.Resolve(completedS.TerritoryId, profile.SsName);
        if (definition is null)
            return null;
        return new HuntAlertSnapshot(
            ReservedSsWatchHuntType,
            completedS.World,
            profile.SsName,
            completedS.TerritoryId,
            definition.DataId,
            definition.PreferredAetheryteId,
            Math.Max(1, completedS.Instance),
            location.MapX,
            location.MapY,
            now);
    }

    private static bool IsReservedSsWatch(HuntAlertSnapshot alert) =>
        alert.HuntType.Equals(ReservedSsWatchHuntType, StringComparison.OrdinalIgnoreCase);

    private void RemoveQueuedSsReservation(SsProfile profile)
    {
        if (current is null)
            return;
        var removed = pendingAlerts.Count(alert =>
            IsReservedSsWatch(alert) &&
            alert.World.Equals(current.World, StringComparison.OrdinalIgnoreCase) &&
            alert.TerritoryId == current.TerritoryId &&
            HuntCatalog.IsSsName(alert.CreatureName, profile));
        if (removed == 0)
            return;
        var survivors = pendingAlerts.Where(alert =>
            !IsReservedSsWatch(alert) ||
            !alert.World.Equals(current.World, StringComparison.OrdinalIgnoreCase) ||
            alert.TerritoryId != current.TerritoryId ||
            !HuntCatalog.IsSsName(alert.CreatureName, profile)).ToArray();
        pendingAlerts.Clear();
        foreach (var survivor in survivors)
            pendingAlerts.Enqueue(survivor);
        PersistQueue();
        log.Information(
            "Removed {Count} queued {Ss} reservation(s) after the precursor chain withdrew",
            removed, profile.SsName);
    }

    private void ConfirmKill(string reason)
    {
        if (current is null || killConfirmed)
            return;

        // An external notice or generic reward line must never overrule the live game object.
        // This is the final shared gate for every path into PostKillSsGrace/ResetToUldah.
        if (clientState.TerritoryType == current.TerritoryId &&
            travel.CurrentWorld.Equals(current.World, StringComparison.OrdinalIgnoreCase))
        {
            var visiblyLiveMark = FindMark();
            if (visiblyLiveMark is not null && !visiblyLiveMark.IsDead && visiblyLiveMark.CurrentHp > 0 &&
                HuntProgressPolicy.DecideHuntExit(
                    HuntExitRequestSource.ExternalDeathEvidence,
                    true,
                    true,
                    true) == HuntExitDecision.BlockForVisibleLiveEntity)
            {
                MarkWasIdentified(visiblyLiveMark);
                status = $"Ignored conflicting death evidence for {current.CreatureName}: the exact current entity is still alive";
                log.Warning(
                    "Rejected conflicting kill evidence because the current entity is visibly alive: mark={Mark}, object={ObjectId}, HP={Hp:0.0}%, world={World}, territory={Territory}, instance={Instance}, evidence={Evidence}",
                    current.CreatureName, visiblyLiveMark.GameObjectId,
                    CombatController.HpPercent(visiblyLiveMark), current.World,
                    current.TerritoryId, current.Instance, reason);
                return;
            }
        }

        killConfirmed = true;
        pullResetCandidateSinceUtc = DateTime.MinValue;
        ResetPendingTagDispatch();
        discardAtUldah = false;
        discardReason = string.Empty;
        vnav.StopSafe();
        var now = DateTime.UtcNow;
        BeginDeadPostKillRewardGrace(now);
        MarkKilled(current, now);
        log.Information("Hunt cleared/completed with positive evidence: {Mark} on {World}; reason={Reason}",
            current.CreatureName, current.World, reason);
        if (HuntCatalog.IsSupportedNormalS(current.TerritoryId, current.CreatureName))
            activeSsProfile = HuntCatalog.GetSsProfileForTerritory(current.TerritoryId);
        var normalSInCurrentContext =
            HuntCatalog.IsSupportedNormalS(current.TerritoryId, current.CreatureName) &&
            clientState.TerritoryType == current.TerritoryId &&
            travel.CurrentWorld.Equals(current.World, StringComparison.OrdinalIgnoreCase);
        if (normalSInCurrentContext)
        {
            var hadLatchedSsEvidence = TryConsumePendingSsChainEvidence(now, out var latchedSsReason);

            // PostKillSsGrace is reserved for a hunt that this client actually identified and
            // observed in combat. A confirmed dead-on-arrival hunt has no local pull cycle to
            // grace; begin recovery immediately, preserving any already-observed SS chain next.
            if (!markEverIdentified || !markCombatObserved)
            {
                if (hadLatchedSsEvidence && activeSsProfile is not null)
                {
                    QueueSsAfterCompletedHunt(null, activeSsProfile, now,
                        $"{latchedSsReason}; the S rank was confirmed dead before local combat observation");
                    return;
                }

                nextActionUtc = now;
                SetState(SentinelState.ResetToUldah,
                    $"{reason}; confirmed dead before arrival/combat, returning to Ul'dah now",
                    HuntExitRequestSource.ConfirmedDeath);
                return;
            }

            ssChainObserved = false;
            ssSpawnAnnounced = false;
            postKillSsGraceDeadlineUtc = now.AddSeconds(config.PostKillSsGraceSeconds);
            ssWatchDeadlineUtc = DateTime.MinValue;
            ResetSsStagingTracking();
            nextActionUtc = DateTime.MinValue;
            SetState(SentinelState.PostKillSsGrace,
                $"{reason}; checking for {activeSsProfile!.PrecursorName}/{activeSsProfile.SsName} evidence for " +
                $"{config.PostKillSsGraceSeconds}s");
            if (hadLatchedSsEvidence)
                ObserveSsChain(activeSsProfile, latchedSsReason);
            return;
        }

        ClearPendingSsChainEvidence();
        nextActionUtc = now;
        SetState(SentinelState.ResetToUldah,
            $"{reason}; returning to Ul'dah on the current visited world",
            HuntExitRequestSource.ConfirmedDeath);
    }

    private void FailCurrent(
        string reason,
        HuntExitRequestSource source = HuntExitRequestSource.FailCurrent)
    {
        if (TryBlockAutomaticHuntExit(source, state, reason))
            return;

        vnav.StopSafe();
        discardAtUldah = true;
        discardReason = reason;
        nextActionUtc = DateTime.UtcNow;
        log.Warning("Abandoning active hunt {Mark} without confirmed kill only after explicit failure: {Reason}",
            current?.CreatureName ?? "(none)", reason);
        SetState(SentinelState.ResetToUldah,
            $"{reason}; discarding this alert after the Ul'dah reset",
            source);
    }

    private void ClearCurrent()
    {
        vnav.StopSafe();
        if (current is not null)
            faloopReportIdsByAlertKey.Remove(current.Key);
        current = null;
        mark = null;
        alertPoint = null;
        approachPoint = null;
        safePoint = null;
        selectedParkingCandidate = null;
        parkingPathTask = null;
        parkingGroundPathTask = null;
        parkingPathStartedUtc = DateTime.MinValue;
        pendingParkingFlightPath = null;
        parkingGroundPathGoal = default;
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
        ResetTagRecoveryTracking();
        identifiedMarkGameObjectId = 0;
        pullCycle = 1;
        pullResetCandidateSinceUtc = DateTime.MinValue;
        activeTagActionId = 0;
        ResetPendingTagDispatch();
        discardAtUldah = false;
        discardReason = string.Empty;
        ssChainObserved = false;
        ssSpawnAnnounced = false;
        activeSsProfile = null;
        postKillSsGraceDeadlineUtc = DateTime.MinValue;
        ssWatchDeadlineUtc = DateTime.MinValue;
        ClearPendingSsChainEvidence();
        ResetSsStagingTracking();
        playerReadySinceUtc = DateTime.MinValue;
        lastMarkSeenUtc = DateTime.MinValue;
        ResetReturnRecoveryTracking();
        resetToUldahAllowsLiveEntityExit = false;
        resetToUldahRequestSource = HuntExitRequestSource.AutomaticStateTransition;
        resetToUldahRequestedFromState = SentinelState.Idle;
        resetToUldahRequestReason = string.Empty;
        ResetRaiseTracking();
        ResetReturnLandingRecovery();
        ResetIncidentalAggroTracking();
        ResetParkingRecoveryTracking();
        parkingCandidates.Clear();
        ResetApproachRouteTracking(clearProjectionCandidates: true);
        ResetLocalApproachRecovery();
        ResetLocateSearchTracking();
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
        ResetLocateSearchTracking();
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

    private void LatchTagRequirement(IBattleChara target, DateTime now)
    {
        var hp = CombatController.HpPercent(target);
        if (!HuntProgressPolicy.ShouldLatchTag(
                !target.IsDead && target.CurrentHp > 0,
                CombatController.IsMarkInCombat(target),
                hp,
                ActiveDistanceProfile.EngageHpPercent,
                pullCycleTagged || tagAttempted))
            return;

        if (!tagRequired)
        {
            tagRequired = true;
            tagRequiredSinceUtc = now;
            tagLastProgressUtc = now;
            tagLastProgressPosition = PlayerPosition();
            tagBestClearance = ClearanceFromMark(target);
            tagRecoveryFailures = 0;
            tagHpEscalationMask = 0;
            activeTagActionId = combat.ResolveTagActionId();
            log.Warning(
                "Tag requirement latched: {Mark}, pull cycle={PullCycle}, HP={Hp:0.0}%, mounted={Mounted}, flying={Flying}; parking/mounting cannot clear this obligation",
                target.Name.TextValue, pullCycle, hp,
                condition[ConditionFlag.Mounted], condition[ConditionFlag.InFlight]);
        }

        ReportTagRecoveryEscalation(target);
    }

    private void BeginTagRequiredRecovery(IBattleChara? target, DateTime now, string reason)
    {
        if (!tagRequired)
            return;

        vnav.StopSafe("tag requirement supersedes parking and landing");
        parkingPathTask = null;
        parkingGroundPathTask = null;
        pendingParkingFlightPath = null;
        selectedParkingPath = null;
        parkingCandidates.Clear();
        safePoint = null;
        selectedParkingCandidate = null;
        postTagRetreatActive = false;
        tagEntityMissingSinceUtc = target is null ? now : DateTime.MinValue;
        if (tagLastProgressUtc == DateTime.MinValue)
        {
            tagLastProgressUtc = now;
            tagLastProgressPosition = PlayerPosition();
            tagBestClearance = target is null ? float.MaxValue : ClearanceFromMark(target);
        }
        SetState(SentinelState.TagApproach, reason);
    }

    private void TickTagLandingAndDismount(IBattleChara target, DateTime now)
    {
        if (tagLandingStartedUtc == DateTime.MinValue)
        {
            tagLandingStartedUtc = now;
            tagLandingLastProgressUtc = now;
            tagLandingLastPosition = PlayerPosition();
            vnav.StopSafe("beginning tag-required landing");
            log.Information(
                "Tag requirement took priority while mounted/flying; beginning landing and dismount for {Mark}",
                target.Name.TextValue);
        }

        if (!condition[ConditionFlag.InFlight])
        {
            tagLandingPoint = null;
            vnav.StopSafe("dismounting on the ground for tag recovery");
            if (now >= nextActionUtc)
            {
                UseGeneralAction(23);
                nextActionUtc = now.AddSeconds(1);
            }
            status = $"Tag required: dismounting on safe ground before targeting {target.Name.TextValue}";
            return;
        }

        var player = PlayerPosition();
        if (HorizontalDistance(player, tagLandingLastPosition) >= ParkingMeaningfulProgressDistance)
        {
            tagLandingLastPosition = player;
            tagLandingLastProgressUtc = now;
        }

        if (tagLandingPoint is not null)
        {
            if (Vector3.Distance(player, tagLandingPoint.Value) <= 5f)
            {
                vnav.StopSafe("tag landing recovery point reached");
                tagLandingPoint = null;
                tagLandingStartedUtc = now;
                nextActionUtc = now;
            }
            else if ((vnav.IsPathRunningSafe() || vnav.IsPathfindInProgressSafe()) &&
                     (now - tagLandingLastProgressUtc).TotalSeconds < TagRouteStallSeconds)
            {
                status = $"Tag required: moving to a safe landing point before dismounting for {target.Name.TextValue}";
                return;
            }
            else
            {
                vnav.StopSafe("tag landing recovery route stalled");
                tagLandingPoint = null;
                tagRecoveryFailures++;
                tagLandingLastProgressUtc = now;
            }
        }

        if ((now - tagLandingStartedUtc).TotalSeconds >= LandingAttemptTimeoutSeconds &&
            TryStartTagLandingRecovery(target, now))
            return;

        vnav.StopSafe("requesting normal landing for required tag");
        if (now >= nextActionUtc)
        {
            UseGeneralAction(23);
            nextActionUtc = now.AddSeconds(1);
        }
        status = $"Tag required: landing before the one ranged tag on {target.Name.TextValue}; remounting is suppressed";
    }

    private bool TryStartTagLandingRecovery(IBattleChara target, DateTime now)
    {
        var player = PlayerPosition();
        var away = player - target.Position;
        away.Y = 0f;
        if (away.LengthSquared() < 0.01f)
            away = Vector3.UnitX;
        away = Vector3.Normalize(away);
        var tangent = new Vector3(-away.Z, 0f, away.X);

        foreach (var (outward, lateral) in new[]
                 {
                     (0f, 0f), (8f, 0f), (12f, 6f), (12f, -6f), (18f, 10f), (18f, -10f),
                 })
        {
            var sample = player + away * outward + tangent * lateral;
            sample.Y = 1024f;
            var floor = vnav.PointOnFloorSafe(sample, 12f);
            if (floor is null ||
                ClearanceAtPoint(floor.Value, target) < ActiveDistanceProfile.EmergencyDistance ||
                !ProtectedSegmentIsSafe(player, floor.Value, target.Position,
                    ProtectedCenterRadius(target), true) ||
                !vnav.MoveToSafe(floor.Value, true))
                continue;

            tagLandingPoint = floor.Value;
            tagLandingLastPosition = player;
            tagLandingLastProgressUtc = now;
            tagRecoveryFailures++;
            log.Warning(
                "Normal tag landing did not finish; using protected landing recovery point {Attempt} at {Clearance:0.0}y clearance",
                tagRecoveryFailures, ClearanceAtPoint(floor.Value, target));
            status = "Normal landing did not finish; moving to an alternate protected landing point for the required tag";
            return true;
        }

        tagRecoveryFailures++;
        tagLandingStartedUtc = now;
        log.Warning(
            "No protected tag landing recovery point resolved; retrying normal landing (failure {Failure})",
            tagRecoveryFailures);
        return false;
    }

    private void UpdateTagRecoveryProgress(IBattleChara target, float clearance, DateTime now)
    {
        var player = PlayerPosition();
        if (HorizontalDistance(player, tagLastProgressPosition) < ParkingMeaningfulProgressDistance &&
            tagBestClearance - clearance < ParkingMeaningfulProgressDistance)
            return;

        tagLastProgressPosition = player;
        tagBestClearance = Math.Min(tagBestClearance, clearance);
        tagLastProgressUtc = now;
    }

    private void ReportTagRecoveryEscalation(IBattleChara target)
    {
        if (!tagRequired || pullCycleTagged || tagAttempted)
            return;
        var hp = CombatController.HpPercent(target);
        var bit = hp <= 25f ? 2 : hp <= 50f ? 1 : 0;
        if (bit == 0 || (tagHpEscalationMask & bit) != 0)
            return;
        tagHpEscalationMask |= bit;
        log.Warning(
            "Tag still unconfirmed at {Hp:0.0}% HP for {Mark}; escalating landing/range recovery without issuing any extra attack after confirmation",
            hp, target.Name.TextValue);
    }

    private void ResetTagRecoveryTracking(bool clearRequirement = true)
    {
        if (clearRequirement)
            tagRequired = false;
        tagRequiredSinceUtc = DateTime.MinValue;
        tagEntityMissingSinceUtc = DateTime.MinValue;
        tagLastProgressUtc = DateTime.MinValue;
        tagLastProgressPosition = default;
        tagBestClearance = float.MaxValue;
        tagLandingPoint = null;
        tagLandingStartedUtc = DateTime.MinValue;
        tagLandingLastProgressUtc = DateTime.MinValue;
        tagLandingLastPosition = default;
        tagRecoveryFailures = 0;
        tagHpEscalationMask = 0;
    }

    private static bool IsLocalHuntState(SentinelState value) => value is
        SentinelState.PrepareApproachDestination or
        SentinelState.ApproachAlertCoordinates or
        SentinelState.LocateMark or
        SentinelState.MoveToSafePoint or
        SentinelState.Landing or
        SentinelState.SafeWait or
        SentinelState.GroundRetreat;

    private void ResetPendingTagDispatch() => pendingTagDispatch = null;

    private static bool ActionSequenceWasHandled(
        ushort baselineSequence,
        ushort submittedSequence,
        ushort handledSequence)
    {
        var submittedDistance = (ushort)(submittedSequence - baselineSequence);
        var handledDistance = (ushort)(handledSequence - baselineSequence);
        return submittedDistance > 0 && submittedDistance < 0x8000 &&
               handledDistance >= submittedDistance && handledDistance < 0x8000;
    }

    private bool ObservePullCycleReset(IBattleChara target, DateTime now)
    {
        if (current is null || !markEverIdentified || !pullCycleCombatObserved ||
            (!tagRequired && (!pullCycleTagged || !tagAttempted)) ||
            killConfirmed || target.IsDead || target.CurrentHp == 0 ||
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
        var completedCycleTagged = pullCycleTagged;
        var stableSeconds = (now - pullResetCandidateSinceUtc).TotalSeconds;
        pullCycle++;
        tagAttempted = false;
        pullCycleCombatObserved = false;
        pullCycleTagged = false;
        ResetTagRecoveryTracking();
        activeTagActionId = 0;
        ResetPendingTagDispatch();
        postTagRetreatActive = false;
        pullResetCandidateSinceUtc = DateTime.MinValue;
        vnav.StopSafe();
        log.Information(
            "Reset confirmed: same {Mark} entity {ObjectId}, previous combat=true, previous tag={PreviousTag}, combat=false, HP={Hp:0.0}%, stable={Stable:0.0}s; completed pull cycle {PullCycle}",
            target.Name.TextValue, target.GameObjectId, completedCycleTagged,
            CombatController.HpPercent(target), stableSeconds, completedCycle);
        log.Information("Tag gate re-armed for pull cycle {PullCycle}", pullCycle);

        var resetClearance = ClearanceFromMark(target);
        if (resetClearance < ActiveDistanceProfile.WaitingDistance ||
            resetClearance > MaximumParkingClearance + 0.5f ||
            state != SentinelState.SafeWait)
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

    private bool KillNoticeMatchesAlert(
        string killedWorld,
        uint killedTerritory,
        int killedInstance,
        HuntAlertSnapshot alert)
    {
        if (!string.IsNullOrWhiteSpace(killedWorld) &&
            !alert.World.Equals(killedWorld, StringComparison.OrdinalIgnoreCase))
            return false;
        if (killedTerritory != 0 && alert.TerritoryId != killedTerritory)
            return false;
        if (killedInstance > 0 && alert.Instance != killedInstance)
            return false;

        // If the notice omitted any routing identity, accept it only when the missing identity
        // can be proven from the character's current World/territory/instance. This prevents a
        // same-name notice from another instance clearing the active or queued hunt.
        var physicallyInAlertContext =
            clientState.TerritoryType == alert.TerritoryId &&
            travel.CurrentWorld.Equals(alert.World, StringComparison.OrdinalIgnoreCase) &&
            (travel.CurrentInstance <= 0 || travel.CurrentInstance == alert.Instance);
        if (string.IsNullOrWhiteSpace(killedWorld) || killedTerritory == 0 || killedInstance <= 0)
            return physicallyInAlertContext;

        return true;
    }

    private void RemoveKilledQueuedAlerts(
        string sonarText,
        string killedWorld,
        uint killedTerritory,
        int killedInstance)
    {
        var removed = pendingAlerts.Where(alert =>
            HuntCatalog.TextMentionsMark(sonarText, alert.CreatureName) &&
            KillNoticeMatchesAlert(killedWorld, killedTerritory, killedInstance, alert)).ToArray();
        var unresolvedRemoved = unresolvedFaloopAlerts
            .Where(pair => HuntCatalog.TextMentionsMark(sonarText, pair.Value.Alert.CreatureName) &&
                           KillNoticeMatchesAlert(killedWorld, killedTerritory, killedInstance, pair.Value.Alert))
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
        {
            killedAlerts[alert.Key] = now;
            faloopReportIdsByAlertKey.Remove(alert.Key);
        }
        foreach (var key in unresolvedRemoved)
        {
            unresolvedFaloopAlerts.Remove(key);
            faloopReportIdsByAlertKey.Remove(key);
        }
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
        {
            unresolvedFaloopAlerts.Remove(key);
            faloopReportIdsByAlertKey.Remove(key);
        }
        if (removed.Length > 0)
        {
            var removedKeys = removed.Select(alert => alert.Key).ToHashSet(StringComparer.Ordinal);
            var survivors = pendingAlerts.Where(alert => !removedKeys.Contains(alert.Key)).ToArray();
            pendingAlerts.Clear();
            foreach (var alert in survivors)
                pendingAlerts.Enqueue(alert);
            var now = DateTime.UtcNow;
            foreach (var alert in removed)
            {
                killedAlerts[alert.Key] = now;
                faloopReportIdsByAlertKey.Remove(alert.Key);
            }
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
            if (HuntCatalog.IsChernobog(alert.TerritoryId, alert.CreatureName))
            {
                log.Warning(
                    "Removed persisted Chernobog alert; U'Ghamaro Mines navigation is intentionally unsupported");
                continue;
            }
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
            .OrderByDescending(candidate => HuntCatalog.IsAnySsName(candidate.CreatureName))
            .ThenByDescending(candidate => ExpansionQueuePriority(HuntCatalog.GetExpansion(candidate.TerritoryId)))
            .ThenBy(candidate => candidate.ReceivedAtUtc)
            .ToArray();
        var skipped = queued.Length - valid.Length;
        var validKeys = valid.Select(candidate => candidate.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var candidate in queued.Where(candidate => !validKeys.Contains(candidate.Key)))
            faloopReportIdsByAlertKey.Remove(candidate.Key);
        pendingAlerts.Clear();
        alert = valid.FirstOrDefault()!;
        foreach (var remaining in valid.Skip(1))
            pendingAlerts.Enqueue(remaining);
        PersistQueue();
        if (skipped > 0)
            log.Information("Skipped {Count} killed, stale, or no-longer-eligible queued alerts", skipped);
        if (alert is not null)
        {
            log.Information("Selected queued {Mark} by SS/expansion priority ({Expansion}); {Remaining} remain",
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
            .OrderByDescending(alert => HuntCatalog.IsAnySsName(alert.CreatureName))
            .ThenByDescending(alert => ExpansionQueuePriority(HuntCatalog.GetExpansion(alert.TerritoryId)))
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
        (!IsReservedSsWatch(alert) ||
         now < alert.ReceivedAtUtc.AddSeconds(config.SsChainTimeoutSeconds)) &&
        HasUsableMapCoordinates(alert) &&
        !HuntCatalog.IsChernobog(alert.TerritoryId, alert.CreatureName) &&
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
        faloopReportIdsByAlertKey.Remove(alert.Key);
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
        var expiredKeys = unresolvedFaloopAlerts
            .Where(pair => !IsWithinFreshnessWindow(pair.Value.Alert.ReceivedAtUtc, now) ||
                           !config.IsExpansionEnabled(HuntCatalog.GetExpansion(pair.Value.Alert.TerritoryId)))
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var key in expiredKeys)
        {
            unresolvedFaloopAlerts.Remove(key);
            faloopReportIdsByAlertKey.Remove(key);
        }
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
        var survivorKeys = survivors.Select(alert => alert.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var alert in pendingAlerts.Where(alert => !survivorKeys.Contains(alert.Key)))
            faloopReportIdsByAlertKey.Remove(alert.Key);
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

    private static float VerticalSeparation(Vector3 a, Vector3 b) => MathF.Abs(a.Y - b.Y);

    private bool TryGetExactVisibleLiveCurrentMark(out IBattleChara liveMark)
    {
        liveMark = null!;
        if (current is null || !markEverIdentified || identifiedMarkGameObjectId == 0 ||
            clientState.TerritoryType != current.TerritoryId ||
            !travel.CurrentWorld.Equals(current.World, StringComparison.OrdinalIgnoreCase))
            return false;

        var loadedInstance = travel.CurrentInstance;
        if (loadedInstance > 0 && current.Instance > 0 && loadedInstance != current.Instance)
            return false;

        var candidate = objects.OfType<IBattleChara>().FirstOrDefault(actor =>
            actor.ObjectKind == ObjectKind.BattleNpc &&
            actor.GameObjectId == identifiedMarkGameObjectId &&
            ((current.MarkDataId != 0 && actor.BaseId == current.MarkDataId) ||
             actor.Name.TextValue.Equals(current.CreatureName, StringComparison.OrdinalIgnoreCase)));
        if (candidate is null || candidate.IsDead || candidate.CurrentHp == 0)
            return false;

        liveMark = candidate;
        return true;
    }

    private bool TryBlockAutomaticHuntExit(
        HuntExitRequestSource source,
        SentinelState requestedFromState,
        string reason)
    {
        if (source == HuntExitRequestSource.ManualSkip ||
            !TryGetExactVisibleLiveCurrentMark(out var liveMark) ||
            HuntProgressPolicy.DecideHuntExit(source, true, true, true) !=
            HuntExitDecision.BlockForVisibleLiveEntity)
            return false;

        var now = DateTime.UtcNow;
        var active = current!;
        var hp = CombatController.HpPercent(liveMark);
        var loadedInstance = travel.CurrentInstance > 0 ? travel.CurrentInstance : active.Instance;
        log.Error(
            "Blocked automatic hunt exit: source={Source}, state={State}, mark={Mark}, object={ObjectId}, HP={Hp:0.0}%, world={World}, territory={Territory}, instance={Instance}, reason={Reason}",
            source, requestedFromState, active.CreatureName, liveMark.GameObjectId, hp,
            travel.CurrentWorld, clientState.TerritoryType, loadedInstance, reason);

        vnav.StopSafe("visible live current entity vetoed automatic hunt exit");
        var removedFalseKillRecord = killedAlerts.Remove(active.Key);
        killConfirmed = false;
        discardAtUldah = false;
        discardReason = string.Empty;
        postKillSsGraceDeadlineUtc = DateTime.MinValue;
        ssWatchDeadlineUtc = DateTime.MinValue;
        ssChainObserved = false;
        ssSpawnAnnounced = false;
        ResetDeadPostKillRewardGrace();
        ResetReturnRecoveryTracking();
        ResetReturnLandingRecovery();
        ResetParkingRecoveryTracking();
        ResetApproachRouteTracking(clearProjectionCandidates: true);
        ResetLocalApproachRecovery();
        ResetLocateSearchTracking();
        resetToUldahAllowsLiveEntityExit = false;
        resetToUldahRequestSource = HuntExitRequestSource.AutomaticStateTransition;
        resetToUldahRequestedFromState = SentinelState.Idle;
        resetToUldahRequestReason = string.Empty;
        nextActionUtc = now;
        mark = liveMark;
        MarkWasIdentified(liveMark);

        if (removedFalseKillRecord)
        {
            PersistQueue();
            log.Warning(
                "Removed the tentative killed-alert record for {Mark} because its exact entity remained visibly alive",
                active.CreatureName);
        }

        if (tagRequired && !pullCycleTagged && !tagAttempted)
        {
            // A tag budget may itself have requested the blocked exit. Rebase only the recovery
            // budget; never clear the pull-cycle obligation or submit an additional attack.
            tagRequiredSinceUtc = now;
            tagEntityMissingSinceUtc = DateTime.MinValue;
            tagLastProgressUtc = now;
            tagLastProgressPosition = PlayerPosition();
            tagBestClearance = ClearanceFromMark(liveMark);
            tagLandingPoint = null;
            tagLandingStartedUtc = DateTime.MinValue;
            tagLandingLastProgressUtc = DateTime.MinValue;
            tagRecoveryFailures = 0;
        }

        LatchTagRequirement(liveMark, now);
        status = $"Blocked automatic exit from {requestedFromState}: {active.CreatureName} is visibly alive at {hp:0.0}% HP";
        if (tagRequired)
        {
            BeginTagRequiredRecovery(liveMark, now,
                $"Automatic exit blocked: exact live {active.CreatureName} still requires one confirmed ranged tag");
        }
        else
        {
            SetState(SentinelState.SafeWait,
                $"Automatic exit blocked: exact live {active.CreatureName} retained locally at {hp:0.0}% HP");
        }

        return true;
    }

    private bool SetState(
        SentinelState next,
        string message,
        HuntExitRequestSource exitSource = HuntExitRequestSource.AutomaticStateTransition)
    {
        var previous = state;
        if (next == SentinelState.ResetToUldah &&
            TryBlockAutomaticHuntExit(exitSource, previous, message))
            return false;

        if (next == SentinelState.ResetToUldah)
        {
            resetToUldahAllowsLiveEntityExit = exitSource == HuntExitRequestSource.ManualSkip;
            resetToUldahRequestSource = exitSource;
            resetToUldahRequestedFromState = previous;
            resetToUldahRequestReason = message;
        }
        else
        {
            resetToUldahAllowsLiveEntityExit = false;
            resetToUldahRequestSource = HuntExitRequestSource.AutomaticStateTransition;
            resetToUldahRequestedFromState = SentinelState.Idle;
            resetToUldahRequestReason = string.Empty;
        }

        state = next;
        stateSinceUtc = DateTime.UtcNow;
        status = message;
        log.Information("State -> {State}: {Message}", next, message);
        return true;
    }

    private static bool IsSonarKillNotice(string text) =>
        !string.IsNullOrWhiteSpace(text) &&
        (text.Contains("was just killed", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("was killed at", StringComparison.OrdinalIgnoreCase)) &&
        (text.Contains(" on ", StringComparison.OrdinalIgnoreCase) || text.Contains('@') || text.Contains('<'));

    private static string ParseSonarWorld(string text)
    {
        var killedAt = text.IndexOf(" was just killed", StringComparison.OrdinalIgnoreCase);
        if (killedAt < 0)
            killedAt = text.IndexOf(" was killed at", StringComparison.OrdinalIgnoreCase);
        var prefix = text[..(killedAt > 0 ? killedAt : text.Length)].TrimEnd();

        // Current Sonar messages use two @ fields: territory first and World last, for example:
        // Rank S: Tyger @Lakeland <11.2, 13.0> @Coeurl was just killed
        var atWorld = prefix.LastIndexOf('@');
        if (atWorld >= 0)
        {
            var world = new string(prefix[(atWorld + 1)..].Where(char.IsLetterOrDigit).ToArray());
            if (!string.IsNullOrWhiteSpace(world))
                return world;
        }

        // The compact notification format instead uses "on <World> was killed at".
        var onWorld = prefix.LastIndexOf(" on ", StringComparison.OrdinalIgnoreCase);
        if (onWorld >= 0)
        {
            var world = new string(prefix[(onWorld + 4)..].Where(char.IsLetterOrDigit).ToArray());
            if (!string.IsNullOrWhiteSpace(world))
                return world;
        }

        // Retain compatibility with older <World> formatting, but never mistake a coordinate
        // pair such as <11.2, 13.0> for the World name.
        var start = prefix.LastIndexOf('<');
        var end = start >= 0 ? prefix.IndexOf('>', start + 1) : -1;
        if (start < 0 || end <= start)
            return string.Empty;
        var bracketed = prefix[(start + 1)..end];
        return bracketed.Any(char.IsDigit) || bracketed.Contains(',') || bracketed.Contains('.')
            ? string.Empty
            : new string(bracketed.Where(char.IsLetterOrDigit).ToArray());
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
        ImGui.SetNextWindowSize(new Vector2(680, 720), ImGuiCond.FirstUseEver);
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
            config.Save();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("STANDALONE S-RANK ORCHESTRATOR");
        ImGui.TextWrapped("HuntAlerts and Sonar supply alerts. Sentinel handles World Visit, teleport, safe movement, one gated ranged tag, SS watch, and recovery.");
        ImGui.Spacing();
        ImGui.TextUnformatted($"State: {state}");
        ImGui.TextWrapped($"Status: {status}");
        if (current is not null)
            ImGui.TextWrapped($"Current: {current.CreatureName} | {current.World} | territory {current.TerritoryId} | instance {current.Instance}");
        if (pendingAlerts.Count > 0)
            ImGui.TextWrapped($"Queued: {pendingAlerts.Count} | Next: {pendingAlerts.Peek().CreatureName}");

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
            5f,
            5f,
            5f);
        DrawDistanceProfile(
            "Proximity-sensitive profile — Endwalker + Dawntrail (Evercold future)",
            "proximity-sensitive",
            config.ProximitySensitiveProfile,
            20f,
            20f,
            15f);
        var freshnessMinutes = config.AlertFreshnessMinutes;
        if (ImGui.InputInt("Queued-alert freshness (minutes)", ref freshnessMinutes))
            config.AlertFreshnessMinutes = Math.Clamp(freshnessMinutes, 10, 180);

        if (ImGui.Button("Save settings"))
            config.Save();
        ImGui.SameLine();
        ImGui.BeginDisabled(current is null);
        if (ImGui.Button("SKIP CURRENT + KEEP QUEUE"))
            FailCurrent("Current hunt skipped manually", HuntExitRequestSource.ManualSkip);
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("STOP + RESET THROUGH UL'DAH"))
        {
            pendingAlerts.Clear();
            PersistQueue();
            if (current is null)
                SetState(SentinelState.ResetToUldah,
                    "Manual reset requested",
                    HuntExitRequestSource.ManualSkip);
            else
                FailCurrent("Stopped manually", HuntExitRequestSource.ManualSkip);
        }

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
            profile.FlagApproachDistance, flagMinimum, 35f);
        profile.WaitingDistance = DrawFloat($"Safe parking clearance##{id}",
            profile.WaitingDistance, safeMinimum, 35f);
        var emergencyMaximum = Math.Min(35f, profile.WaitingDistance);
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

internal sealed record TagDispatch(
    uint ActionId,
    ulong TargetId,
    ushort BaselineSequence,
    ushort? Sequence,
    bool DeferredForTarget,
    DateTime SubmittedAtUtc);

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
