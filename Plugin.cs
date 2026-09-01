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
    private readonly LifestreamIpc lifestream;
    private readonly ICallGateSubscriber<HuntTrainMessageDto, object> huntAlerts;
    private readonly Configuration config;
    private readonly Queue<HuntAlertSnapshot> pendingAlerts = new();
    private readonly Queue<Vector3> parkingCandidates = new();

    private bool configOpen;
    private HuntAlertSnapshot? current;
    private IGameObject? mark;
    private Vector3? flagPoint;
    private Vector3? safePoint;
    private SentinelState state = SentinelState.Idle;
    private DateTime stateSinceUtc = DateTime.UtcNow;
    private DateTime lastTickUtc = DateTime.MinValue;
    private DateTime nextActionUtc = DateTime.MinValue;
    private uint queuedAetheryteId;
    private MapLinkPayload? queuedMapLink;
    private bool ownsQueuedTravel;
    private bool queuedFlagPrepared;
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
        lifestream = new LifestreamIpc(pi);

        // HuntAlerts owns its message type. A local DTO with matching public properties lets
        // Dalamud preserve the cross-plugin payload without a compile-time dependency.
        // Subscribing as object would deserialize the payload as JObject and hide its fields.
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

        log.Information("S Rank Sentinel v0.1 loaded. Safety prototype: it never attacks.");
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
                if (current is not null && text.Contains(current.CreatureName, StringComparison.OrdinalIgnoreCase))
                    MarkCurrentComplete($"Sonar confirmed {current.CreatureName} was killed");

                RemoveKilledQueuedAlerts(text);
                return;
            }

            const string sRankPrefix = "Rank S:";
            var namePayload = chatMessage.Message.Payloads
                .OfType<TextPayload>()
                .Select(payload => payload.Text)
                .FirstOrDefault(value => value?.StartsWith(sRankPrefix, StringComparison.OrdinalIgnoreCase) == true);

            // "Rank SS:" and other ranks intentionally do not match the exact S-rank prefix.
            if (namePayload is null)
                return;

            var creature = namePayload[sRankPrefix.Length..].Trim();
            var mapLink = chatMessage.Message.Payloads.OfType<MapLinkPayload>().FirstOrDefault();
            if (mapLink is null || string.IsNullOrWhiteSpace(creature))
            {
                status = "Sonar S-rank alert received, but its creature/map link could not be read";
                log.Warning("Ignored Sonar S-rank message because creature or map-link data was unavailable.");
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
            status = "Sonar alert could not be parsed; navigation was not started";
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
        huntType ??= string.Empty;
        world ??= string.Empty;
        creature ??= string.Empty;

        if (!huntType.Equals("srank", StringComparison.OrdinalIgnoreCase))
            return;

        if (territory == 0 || string.IsNullOrWhiteSpace(creature))
        {
            status = $"S-rank alert received, but {source} did not include creature/territory data";
            log.Warning("Ignored {Source} S-rank event because territory or creature name was unavailable.", source);
            return;
        }

        if (instance < 1)
            instance = 1;

        var incoming = new HuntAlertSnapshot(huntType, world, creature, territory, instance, mapX, mapY, DateTime.UtcNow);
        if (current?.Key == incoming.Key || pendingAlerts.Any(alert => alert.Key == incoming.Key))
            return;

        // Never abandon an active approach. Preserve later S ranks in arrival order so
        // the completed mark can be cleared before the next travel workflow begins.
        if ((current is not null && state is not SentinelState.Idle and not SentinelState.Aborted) ||
            state is SentinelState.ReturningHome)
        {
            pendingAlerts.Enqueue(incoming);
            status = $"Queued {creature}; {pendingAlerts.Count} S rank(s) waiting";
            log.Information("Queued S-rank alert while another lifecycle is active: {Name} ({Count} waiting)", creature, pendingAlerts.Count);
            return;
        }

        StartAlert(incoming, source);
    }

    private void StartAlert(HuntAlertSnapshot incoming, string source)
    {
        current = incoming;
        mark = null;
        flagPoint = null;
        safePoint = null;
        parkingCandidates.Clear();
        queuedAetheryteId = 0;
        queuedMapLink = null;
        queuedFlagPrepared = false;
        ownsQueuedTravel = source.Equals("queued alert", StringComparison.OrdinalIgnoreCase);
        nextActionUtc = DateTime.MinValue;
        if (ownsQueuedTravel)
            PrepareQueuedTravel(incoming);
        SetState(SentinelState.WaitForTerritory, $"Waiting for travel to {incoming.CreatureName} ({incoming.World})");
        log.Information("Accepted {Source} S-rank alert: {Name}, territory {Territory}, world {World}, instance {Instance}", source, incoming.CreatureName, incoming.TerritoryId, incoming.World, incoming.Instance);
    }

    private void PrepareQueuedTravel(HuntAlertSnapshot alert)
    {
        var aetheryte = data.GetExcelSheet<Aetheryte>()
            .FirstOrDefault(row => row.IsAetheryte && row.Territory.RowId == alert.TerritoryId);
        queuedAetheryteId = aetheryte.RowId;

        var map = data.GetExcelSheet<Map>()
            .FirstOrDefault(row => row.TerritoryType.RowId == alert.TerritoryId);
        if (map.RowId != 0)
            queuedMapLink = new MapLinkPayload(alert.TerritoryId, map.RowId, alert.MapX, alert.MapY);

        if (queuedAetheryteId == 0)
            log.Warning("Queued alert {Name} has no territory aetheryte; waiting for the existing travel plugins instead.", alert.CreatureName);
    }

    private void DriveQueuedTravel(DateTime now)
    {
        if (!ownsQueuedTravel || current is null)
            return;

        var localPlayer = objects.LocalPlayer;
        var currentWorld = localPlayer?.CurrentWorld.Value.Name.ToString() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(current.World) &&
            !currentWorld.Equals(current.World, StringComparison.OrdinalIgnoreCase))
        {
            if (now < nextActionUtc || lifestream.IsBusySafe())
                return;

            if (lifestream.ChangeWorldSafe(current.World))
            {
                status = $"Queued S rank: changing world to {current.World} through Lifestream";
                nextActionUtc = now.AddSeconds(15);
            }
            else
            {
                status = $"Queued S rank: waiting to retry world travel to {current.World}";
                nextActionUtc = now.AddSeconds(5);
            }
            return;
        }

        if (clientState.TerritoryType == current.TerritoryId)
        {
            PrepareQueuedMapFlag();
            return;
        }

        if (queuedAetheryteId == 0 || now < nextActionUtc || lifestream.IsBusySafe())
            return;

        if (lifestream.TeleportSafe(queuedAetheryteId))
        {
            status = $"Queued S rank: teleporting normally toward {current.CreatureName}";
            nextActionUtc = now.AddSeconds(15);
        }
        else
        {
            status = $"Queued S rank: waiting to retry the zone teleport for {current.CreatureName}";
            nextActionUtc = now.AddSeconds(5);
        }
    }

    private void PrepareQueuedMapFlag()
    {
        if (queuedFlagPrepared || queuedMapLink is null)
            return;

        queuedFlagPrepared = gameGui.OpenMapWithMapLink(queuedMapLink);
        if (!queuedFlagPrepared)
            status = "Queued S rank reached; waiting to retry its map flag";
    }

    private void RemoveKilledQueuedAlerts(string sonarText)
    {
        if (pendingAlerts.Count == 0)
            return;

        var survivors = pendingAlerts
            .Where(alert => !sonarText.Contains(alert.CreatureName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (survivors.Length == pendingAlerts.Count)
            return;

        pendingAlerts.Clear();
        foreach (var alert in survivors)
            pendingAlerts.Enqueue(alert);
    }

    private static string ParseSonarWorld(string text)
    {
        var start = text.LastIndexOf('<');
        var end = start >= 0 ? text.IndexOf('>', start + 1) : -1;
        if (start < 0 || end <= start)
            return string.Empty;

        // Sonar may place a private-use cross-world icon inside the angle brackets.
        return new string(text[(start + 1)..end]
            .Where(character => char.IsLetterOrDigit(character))
            .ToArray());
    }

    private static int ParseSonarInstance(string text)
    {
        // Sonar's instance glyphs are U+E0B1 through U+E0B9.
        for (var instance = 1; instance <= 9; instance++)
        {
            if (text.Contains((char)(0xE0B0 + instance)))
                return instance;
        }

        return 1;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!config.Enabled || (current is null && state is not SentinelState.ReturningHome))
            return;

        // Keep this intentionally low-frequency; none of the safety decisions require
        // per-frame polling.
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
            log.Error(ex, "S Rank Sentinel state machine failed; aborting safely.");
            Abort("Internal error; navigation stopped");
        }
    }

    private void Tick(DateTime now)
    {
        if (current is null && state is not SentinelState.ReturningHome)
            return;

        switch (state)
        {
            case SentinelState.WaitForTerritory:
                DriveQueuedTravel(now);

                if ((now - stateSinceUtc).TotalSeconds > config.ArrivalTimeoutSeconds)
                {
                    Abort("Timed out waiting for HuntAlerts/HuntTrainAssistant/Lifestream travel");
                    return;
                }

                if (clientState.TerritoryType != current.TerritoryId || !vnav.IsReadySafe())
                    return;

                SetState(SentinelState.WaitForFlag, "Correct territory reached; waiting for map flag/navmesh");
                return;

            case SentinelState.WaitForFlag:
                PrepareQueuedMapFlag();

                if ((now - stateSinceUtc).TotalSeconds > 30)
                {
                    Abort("Map flag could not be resolved; refusing to guess a hunt position");
                    return;
                }

                flagPoint = vnav.FlagToPointSafe();
                if (flagPoint is null)
                    return;

                if (!EnsureMounted(now))
                    return;

                if (!vnav.MoveCloseToSafe(flagPoint.Value, true, config.FlagApproachDistance))
                {
                    Abort("vnavmesh refused the flight approach");
                    return;
                }

                SetState(SentinelState.ApproachFlag, $"Flying toward flag; stopping about {config.FlagApproachDistance:0}y away");
                return;

            case SentinelState.ApproachFlag:
                mark = FindMark();
                if (mark is not null)
                {
                    vnav.StopSafe();
                    BeginSafeParking(mark);
                    return;
                }

                if ((now - stateSinceUtc).TotalSeconds > config.LocateTimeoutSeconds)
                {
                    Abort("S rank never became positively identifiable; refusing to fly onto the flag");
                    return;
                }
                return;

            case SentinelState.MoveToSafePoint:
                mark = FindMark();
                if (mark is null)
                {
                    Abort("Lost positive identification of the S rank while parking");
                    return;
                }

                if (safePoint is null)
                {
                    BeginSafeParking(mark);
                    return;
                }

                if (HorizontalDistance(PlayerPosition(), safePoint.Value) <= 5f)
                {
                    vnav.StopSafe();
                    SetState(SentinelState.Landing, "At safe parking point; landing normally");
                    return;
                }

                if ((now - stateSinceUtc).TotalSeconds > 4 &&
                    !vnav.IsPathRunningSafe() &&
                    !vnav.IsPathfindInProgressSafe())
                {
                    if (!TryStartNextParkingRoute(true))
                    {
                        Abort("All sampled safe parking routes were unreachable");
                        return;
                    }

                    SetState(SentinelState.MoveToSafePoint, $"Trying another safe parking route ({parkingCandidates.Count} alternatives remain)");
                }
                return;

            case SentinelState.Landing:
                if (now < nextActionUtc)
                    return;

                if (condition[ConditionFlag.InFlight])
                {
                    UseGeneralAction(23); // normal game land/dismount action
                    nextActionUtc = now.AddSeconds(1);
                    return;
                }

                if (condition[ConditionFlag.Mounted])
                {
                    UseGeneralAction(23); // normal dismount once grounded
                    nextActionUtc = now.AddSeconds(1);
                    return;
                }

                SetState(SentinelState.SafeWait, $"Safe wait: maintaining at least {config.EmergencyDistance:0}y");
                return;

            case SentinelState.SafeWait:
                mark = FindMark();
                if (mark is null)
                {
                    // Do not move toward the flag if the mark disappears. It may have died,
                    // despawned, or moved out of object range; remaining stationary is safest.
                    status = "Mark not visible; holding position safely (v0.1 will not chase)";
                    return;
                }

                if (mark is IBattleChara battle && battle.CurrentHp == 0)
                {
                    MarkCurrentComplete($"{mark.Name.TextValue} is dead");
                    return;
                }

                var distance = HorizontalDistance(PlayerPosition(), mark.Position);
                status = $"Safe wait: {mark.Name.TextValue} is {distance:0.0}y away";
                if (distance < config.EmergencyDistance)
                {
                    BeginGroundRetreat(mark);
                }
                return;

            case SentinelState.GroundRetreat:
                mark = FindMark();
                if (mark is null)
                {
                    vnav.StopSafe();
                    SetState(SentinelState.SafeWait, "Mark lost while retreating; stopped safely");
                    return;
                }

                if (HorizontalDistance(PlayerPosition(), mark.Position) >= config.WaitingDistance - 2f)
                {
                    vnav.StopSafe();
                    SetState(SentinelState.SafeWait, "Safe radius restored");
                    return;
                }


                if ((now - stateSinceUtc).TotalSeconds > 4 &&
                    !vnav.IsPathRunningSafe() &&
                    !vnav.IsPathfindInProgressSafe())
                {
                    if (!TryStartNextParkingRoute(false))
                    {
                        vnav.StopSafe();
                        SetState(SentinelState.SafeWait, "No alternate ground-retreat route was reachable; holding position");
                        return;
                    }

                    SetState(SentinelState.GroundRetreat, $"Trying another ground-retreat route ({parkingCandidates.Count} alternatives remain)");
                }
                return;

            case SentinelState.Complete:
                if (now >= nextActionUtc)
                    FinishCompletedAlert();
                return;

            case SentinelState.ReturningHome:
                TickReturnHome(now);
                return;
        }
    }

    private IGameObject? FindMark()
    {
        if (current is null || clientState.TerritoryType != current.TerritoryId)
            return null;

        return objects
            .Where(o => o is not null && o.ObjectKind == ObjectKind.BattleNpc)
            .FirstOrDefault(o => o.Name.TextValue.Equals(current.CreatureName, StringComparison.OrdinalIgnoreCase));
    }

    private void BeginSafeParking(IGameObject target)
    {
        if (!EnsureMounted(DateTime.UtcNow))
            return;

        PrepareParkingCandidates(target, config.WaitingDistance);
        if (!TryStartNextParkingRoute(true))
        {
            Abort("No reachable point was found among the sampled safe parking positions");
            return;
        }

        SetState(SentinelState.MoveToSafePoint, $"Parking about {config.WaitingDistance:0}y from actual S rank ({parkingCandidates.Count} alternatives ready)");
    }

    private void BeginGroundRetreat(IGameObject target)
    {
        PrepareParkingCandidates(target, config.WaitingDistance);
        if (!TryStartNextParkingRoute(false))
        {
            vnav.StopSafe();
            status = "S rank moved inside emergency radius, but no sampled ground-retreat route was reachable; holding position";
            return;
        }

        SetState(SentinelState.GroundRetreat, $"S rank roamed too close; backing away on the ground ({parkingCandidates.Count} alternatives ready)");
    }

    private void PrepareParkingCandidates(IGameObject target, float distance)
    {
        parkingCandidates.Clear();
        safePoint = null;

        var player = PlayerPosition();
        var away = player - target.Position;
        away.Y = 0;
        if (away.LengthSquared() < 0.01f)
            away = Vector3.UnitX;
        away = Vector3.Normalize(away);

        // Try the natural away-from-mark direction first, then fan out to both sides.
        // A second, slightly wider ring helps when the requested radius lands on a
        // cliff, building, hole in the mesh, or disconnected terrain island.
        ReadOnlySpan<float> angleOffsets = [0f, 30f, -30f, 60f, -60f, 90f, -90f, 120f, -120f, 150f, -150f, 180f];
        ReadOnlySpan<float> radiusOffsets = [0f, 8f];
        var accepted = new List<Vector3>();
        var minimumDistance = MathF.Max(config.EmergencyDistance + 3f, distance - 6f);

        foreach (var radiusOffset in radiusOffsets)
        {
            foreach (var angleOffset in angleOffsets)
            {
                var radians = angleOffset * MathF.PI / 180f;
                var cos = MathF.Cos(radians);
                var sin = MathF.Sin(radians);
                var direction = new Vector3(
                    away.X * cos - away.Z * sin,
                    0f,
                    away.X * sin + away.Z * cos);

                var candidate = target.Position + direction * (distance + radiusOffset);
                candidate.Y = 1024f; // let vnavmesh project each sample onto real terrain
                var projected = vnav.PointOnFloorSafe(candidate, 12f);
                if (projected is null || HorizontalDistance(projected.Value, target.Position) < minimumDistance)
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

    private Vector3 PlayerPosition() => objects.LocalPlayer?.Position ?? Vector3.Zero;

    private bool EnsureMounted(DateTime now)
    {
        if (condition[ConditionFlag.Mounted])
            return true;

        if (now < nextActionUtc)
            return false;

        UseGeneralAction(9); // Mount Roulette / normal mount action
        nextActionUtc = now.AddSeconds(2);
        status = "Mounting normally before flight";
        return false;
    }

    private unsafe void UseGeneralAction(uint id)
    {
        var manager = ActionManager.Instance();
        if (manager is null)
            return;
        manager->UseAction(ActionType.GeneralAction, id);
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private void MarkCurrentComplete(string reason)
    {
        if (current is null || state is SentinelState.Complete)
            return;

        vnav.StopSafe();
        nextActionUtc = DateTime.UtcNow.AddSeconds(2);
        SetState(SentinelState.Complete, $"{reason}; clearing completed alert");
    }

    private void FinishCompletedAlert()
    {
        var completedName = current?.CreatureName ?? "S rank";
        ClearCurrentAlert();

        if (pendingAlerts.TryDequeue(out var next))
        {
            StartAlert(next, "queued alert");
            status = $"{completedName} cleared; processing queued S rank {next.CreatureName} ({pendingAlerts.Count} still waiting)";
            return;
        }

        if (config.ReturnToUldahAfterKill)
        {
            nextActionUtc = DateTime.UtcNow;
            SetState(SentinelState.ReturningHome, $"{completedName} cleared; returning to Ul'dah through Lifestream");
            return;
        }

        SetState(SentinelState.Idle, $"{completedName} cleared; idle");
    }

    private void TickReturnHome(DateTime now)
    {
        // Ul'dah - Steps of Nald / Steps of Thal.
        if (clientState.TerritoryType is 130 or 131)
        {
            if (pendingAlerts.TryDequeue(out var next))
            {
                StartAlert(next, "queued alert");
                return;
            }

            SetState(SentinelState.Idle, "Returned to Ul'dah; idle");
            return;
        }

        if ((now - stateSinceUtc).TotalSeconds > 180)
        {
            SetState(SentinelState.Idle, "Completed alert cleared, but the return to Ul'dah timed out");
            return;
        }

        if (now < nextActionUtc || lifestream.IsBusySafe())
            return;

        if (lifestream.TeleportToUldahSafe())
        {
            status = "Lifestream accepted the normal teleport to Ul'dah";
            nextActionUtc = now.AddSeconds(15);
        }
        else
        {
            status = "Waiting to retry the normal Lifestream teleport to Ul'dah";
            nextActionUtc = now.AddSeconds(5);
        }
    }

    private void ClearCurrentAlert()
    {
        vnav.StopSafe();
        current = null;
        mark = null;
        safePoint = null;
        flagPoint = null;
        parkingCandidates.Clear();
        queuedAetheryteId = 0;
        queuedMapLink = null;
        ownsQueuedTravel = false;
        queuedFlagPrepared = false;
    }

    private void SetState(SentinelState next, string message)
    {
        state = next;
        stateSinceUtc = DateTime.UtcNow;
        status = message;
        log.Information("State -> {State}: {Message}", next, message);
    }

    private void Abort(string reason)
    {
        vnav.StopSafe();
        SetState(SentinelState.Aborted, reason);
    }

    private void DrawUi()
    {
        if (!configOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(520, 360), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("S Rank Sentinel v0.1###SRankSentinel", ref configOpen))
        {
            ImGui.End();
            return;
        }

        var enabled = config.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled))
        {
            config.Enabled = enabled;
            if (!enabled) vnav.StopSafe();
            config.Save();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("v0.1 SAFETY PROTOTYPE — NEVER ATTACKS");
        ImGui.TextWrapped("HuntAlerts or Sonar can trigger HuntTrainAssistant/Lifestream travel. Sentinel only handles the final in-zone safe approach using vnavmesh.");
        ImGui.Spacing();
        ImGui.TextUnformatted($"State: {state}");
        ImGui.TextWrapped($"Status: {status}");
        if (current is not null)
            ImGui.TextWrapped($"Current: {current.CreatureName} | {current.World} | territory {current.TerritoryId} | instance {current.Instance}");
        if (pendingAlerts.Count > 0)
            ImGui.TextWrapped($"Queued S ranks: {pendingAlerts.Count} | Next: {pendingAlerts.Peek().CreatureName}");

        ImGui.Separator();
        config.FlagApproachDistance = DrawFloat("Flag approach distance", config.FlagApproachDistance, 30f, 90f);
        config.WaitingDistance = DrawFloat("Waiting distance", config.WaitingDistance, 25f, 70f);
        config.EmergencyDistance = DrawFloat("Emergency minimum", config.EmergencyDistance, 15f, 50f);
        var returnToUldah = config.ReturnToUldahAfterKill;
        if (ImGui.Checkbox("Return to Ul'dah after final queued S rank", ref returnToUldah))
            config.ReturnToUldahAfterKill = returnToUldah;
        ImGui.BeginDisabled();
        config.EngageHpPercent = DrawFloat("Engage HP % (reserved for v0.2)", config.EngageHpPercent, 1f, 99f);
        ImGui.EndDisabled();

        if (ImGui.Button("Save settings"))
            config.Save();
        ImGui.SameLine();
        if (ImGui.Button("STOP / ABORT"))
            Abort("Stopped manually");
        ImGui.SameLine();
        if (ImGui.Button("Clear alert / queue"))
        {
            ClearCurrentAlert();
            pendingAlerts.Clear();
            SetState(SentinelState.Idle, "Idle");
        }

        ImGui.Separator();
        ImGui.TextWrapped("Safety: no coordinate warps, no direct position writes, no attack commands. Local movement uses vnavmesh; landing/dismount uses the game's normal General Action 23.");
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
        WaitForTerritory,
        WaitForFlag,
        ApproachFlag,
        MoveToSafePoint,
        Landing,
        SafeWait,
        GroundRetreat,
        Complete,
        ReturningHome,
        Aborted,
    }
}


// Property names intentionally mirror HuntAlerts' IPC payload. Dalamud serializes the
// provider's type into this local type, keeping the plugins decoupled at compile time.
internal sealed class HuntTrainMessageDto
{
    public HuntTrainMessageDto()
    {
    }

    public string huntType { get; set; } = string.Empty;
    public string huntWorld { get; set; } = string.Empty;
    public string creatureName { get; set; } = string.Empty;
    public uint startTerritoryTypeId { get; set; }
    public int instance { get; set; }
    public float mapLocationX { get; set; }
    public float mapLocationY { get; set; }
}
