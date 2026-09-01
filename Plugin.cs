using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
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
    private readonly IObjectTable objects;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly VNavmeshIpc vnav;
    private readonly ICallGateSubscriber<HuntTrainMessageDto, object> huntAlerts;
    private readonly Configuration config;

    private bool configOpen;
    private HuntAlertSnapshot? current;
    private IGameObject? mark;
    private Vector3? flagPoint;
    private Vector3? safePoint;
    private SentinelState state = SentinelState.Idle;
    private DateTime stateSinceUtc = DateTime.UtcNow;
    private DateTime lastTickUtc = DateTime.MinValue;
    private DateTime nextActionUtc = DateTime.MinValue;
    private string status = "Idle";

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IClientState clientState,
        ICondition condition,
        IObjectTable objectTable,
        IFramework framework,
        IPluginLog pluginLog)
    {
        pi = pluginInterface;
        commands = commandManager;
        this.clientState = clientState;
        this.condition = condition;
        objects = objectTable;
        this.framework = framework;
        log = pluginLog;

        config = pi.GetPluginConfig() as Configuration ?? new Configuration();
        config.Initialize(pi);
        vnav = new VNavmeshIpc(pi);

        // HuntAlerts owns its message type. A local DTO with matching public properties lets
        // Dalamud preserve the cross-plugin payload without a compile-time dependency.
        // Subscribing as object would deserialize the payload as JObject and hide its fields.
        huntAlerts = pi.GetIpcSubscriber<HuntTrainMessageDto, object>("HuntAlerts.OnHuntTrainMessageReceived");
        huntAlerts.Subscribe(OnHuntAlert);

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

        var huntType = payload.huntType ?? string.Empty;
        if (!huntType.Equals("srank", StringComparison.OrdinalIgnoreCase))
            return;

        var world = payload.huntWorld ?? string.Empty;
        var creature = payload.creatureName ?? string.Empty;
        var territory = payload.startTerritoryTypeId;
        var instance = payload.instance;
        var mapX = payload.mapLocationX;
        var mapY = payload.mapLocationY;

        if (territory == 0 || string.IsNullOrWhiteSpace(creature))
        {
            status = "S-rank alert received, but HuntAlerts did not include creature/territory data";
            log.Warning("Ignored S-rank IPC event because territory or creature name was unavailable.");
            return;
        }

        var incoming = new HuntAlertSnapshot(huntType, world, creature, territory, instance, mapX, mapY, DateTime.UtcNow);
        if (current?.Key == incoming.Key)
            return;

        // v0.1 intentionally does not queue multiple marks. Never abandon an active
        // approach to chase a new notification; that is safer for unattended testing.
        if (current is not null && state is not SentinelState.Idle and not SentinelState.Complete and not SentinelState.Aborted)
        {
            log.Information("Ignored new S-rank alert while another safe-approach test is active: {Name}", creature);
            return;
        }

        current = incoming;
        mark = null;
        flagPoint = null;
        safePoint = null;
        SetState(SentinelState.WaitForTerritory, $"Waiting for travel to {creature} ({world})");
        log.Information("Accepted S-rank alert: {Name}, territory {Territory}, world {World}, instance {Instance}", creature, territory, world, instance);
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!config.Enabled || current is null)
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
        if (current is null)
            return;

        switch (state)
        {
            case SentinelState.WaitForTerritory:
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
                    vnav.StopSafe();
                    SetState(SentinelState.Complete, "S rank is dead; v0.1 complete (no return-home automation yet)");
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
                }
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
        var point = CalculateSafeGroundPoint(target, config.WaitingDistance);
        if (point is null)
        {
            Abort("Could not find a valid ground parking point outside the S rank");
            return;
        }

        safePoint = point;
        if (!EnsureMounted(DateTime.UtcNow))
            return;

        if (!vnav.MoveToSafe(point.Value, true))
        {
            Abort("vnavmesh could not route to the safe parking point");
            return;
        }

        SetState(SentinelState.MoveToSafePoint, $"Parking about {config.WaitingDistance:0}y from actual S rank");
    }

    private void BeginGroundRetreat(IGameObject target)
    {
        var point = CalculateSafeGroundPoint(target, config.WaitingDistance);
        if (point is null)
        {
            vnav.StopSafe();
            status = "S rank moved inside emergency radius, but no safe ground retreat point was found; holding position";
            return;
        }

        safePoint = point;
        if (!vnav.MoveToSafe(point.Value, false))
        {
            vnav.StopSafe();
            status = "Ground retreat path failed; navigation stopped";
            return;
        }

        SetState(SentinelState.GroundRetreat, "S rank roamed too close; backing away on the ground");
    }

    private Vector3? CalculateSafeGroundPoint(IGameObject target, float distance)
    {
        var player = PlayerPosition();
        var away = player - target.Position;
        away.Y = 0;
        if (away.LengthSquared() < 0.01f)
            away = Vector3.UnitX;
        away = Vector3.Normalize(away);

        var candidate = target.Position + away * distance;
        candidate.Y = 1024f; // let vnavmesh project the candidate onto real terrain
        return vnav.PointOnFloorSafe(candidate, 12f);
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
        ImGui.TextWrapped("HuntAlerts/HuntTrainAssistant/Lifestream perform world/zone travel. Sentinel only handles the final in-zone safe approach using vnavmesh.");
        ImGui.Spacing();
        ImGui.TextUnformatted($"State: {state}");
        ImGui.TextWrapped($"Status: {status}");
        if (current is not null)
            ImGui.TextWrapped($"Current: {current.CreatureName} | {current.World} | territory {current.TerritoryId} | instance {current.Instance}");

        ImGui.Separator();
        config.FlagApproachDistance = DrawFloat("Flag approach distance", config.FlagApproachDistance, 60f, 120f);
        config.WaitingDistance = DrawFloat("Waiting distance", config.WaitingDistance, 55f, 100f);
        config.EmergencyDistance = DrawFloat("Emergency minimum", config.EmergencyDistance, 45f, 90f);
        ImGui.BeginDisabled();
        config.EngageHpPercent = DrawFloat("Engage HP % (reserved for v0.2)", config.EngageHpPercent, 1f, 99f);
        ImGui.EndDisabled();

        if (ImGui.Button("Save settings"))
            config.Save();
        ImGui.SameLine();
        if (ImGui.Button("STOP / ABORT"))
            Abort("Stopped manually");
        ImGui.SameLine();
        if (ImGui.Button("Clear completed alert"))
        {
            vnav.StopSafe();
            current = null;
            mark = null;
            safePoint = null;
            flagPoint = null;
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
