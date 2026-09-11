using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using SentinelHunts.Core;
using SentinelHunts.Orchestration;
using SentinelHunts.Services;
using SentinelHunts.Sources;
using System.Collections.Concurrent;
using System.Numerics;

namespace SentinelHunts;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/sentinelhunts";

    private readonly IDalamudPluginInterface pi;
    private readonly ICommandManager commands;
    private readonly IFramework framework;
    private readonly GameState game;
    private readonly VNavmeshService vnav;
    private readonly LifestreamService lifestream;
    private readonly ActionService actions;
    private readonly SonarSource sonar;
    private readonly HuntAlertsSource huntAlerts;
    private readonly HuntCoordinator coordinator;
    private readonly ConcurrentQueue<HuntEvent> incomingEvents = new();
    private readonly Configuration config;

    private bool windowOpen;
    private bool restoring;

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
        this.framework = framework;

        config = pi.GetPluginConfig() as Configuration ?? new Configuration();
        config.Initialize(pi);
        game = new GameState(clientState, condition, objectTable, dataManager);
        vnav = new VNavmeshService(pi);
        lifestream = new LifestreamService(pi);
        actions = new ActionService(gameGui, condition, objectTable, targetManager);
        coordinator = new HuntCoordinator(config, game, vnav, lifestream, actions, pluginLog);
        coordinator.Changed += PersistQueue;

        sonar = new SonarSource(chatGui, game, pluginLog);
        huntAlerts = new HuntAlertsSource(pi, game, pluginLog);
        sonar.EventReceived += OnSourceEvent;
        huntAlerts.EventReceived += OnSourceEvent;

        RestoreQueue();
        SyncSourceSettings();

        framework.Update += OnFrameworkUpdate;
        pi.UiBuilder.Draw += DrawUi;
        pi.UiBuilder.OpenMainUi += OpenUi;
        pi.UiBuilder.OpenConfigUi += OpenUi;
        commands.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Sentinel Hunts. Subcommands: test, stop, retry, skip."
        });

        pluginLog.Information(
            "Sentinel Hunts loaded independently with {CatalogCount} catalog entries and automation {Mode}",
            HuntCatalog.All.Count,
            config.AutomationEnabled ? "enabled" : "in observation mode");
    }

    public string Name => "Sentinel Hunts";

    public void Dispose()
    {
        coordinator.StopAutomation();
        PersistQueue();
        coordinator.Changed -= PersistQueue;
        sonar.EventReceived -= OnSourceEvent;
        huntAlerts.EventReceived -= OnSourceEvent;
        sonar.Dispose();
        huntAlerts.Dispose();
        framework.Update -= OnFrameworkUpdate;
        pi.UiBuilder.Draw -= DrawUi;
        pi.UiBuilder.OpenMainUi -= OpenUi;
        pi.UiBuilder.OpenConfigUi -= OpenUi;
        commands.RemoveHandler(Command);
    }

    private void OpenUi() => windowOpen = true;

    private void OnCommand(string _, string arguments)
    {
        switch (arguments.Trim().ToLowerInvariant())
        {
            case "test":
                AddTestReport();
                windowOpen = true;
                break;
            case "stop":
                config.AutomationEnabled = false;
                config.Save();
                coordinator.StopAutomation();
                break;
            case "retry":
                coordinator.RetryCurrent();
                break;
            case "skip":
                coordinator.SkipCurrent();
                break;
            default:
                windowOpen = true;
                break;
        }
    }

    private void OnSourceEvent(HuntEvent huntEvent) => incomingEvents.Enqueue(huntEvent);

    private void OnFrameworkUpdate(IFramework _)
    {
        SyncSourceSettings();
        while (incomingEvents.TryDequeue(out var huntEvent))
            coordinator.Observe(huntEvent);
        coordinator.Tick(DateTimeOffset.UtcNow);
    }

    private void SyncSourceSettings()
    {
        sonar.Enabled = config.EnableSonar;
        huntAlerts.Enabled = config.EnableHuntAlerts;
    }

    private void RestoreQueue()
    {
        restoring = true;
        try
        {
            foreach (var saved in config.PendingHunts)
            {
                if (HuntCatalog.FindByDataId(saved.MarkDataId) is null)
                    continue;
                coordinator.Observe(new HuntEvent(
                    HuntEventKind.Reported,
                    HuntSource.ManualTest,
                    null,
                    saved.WorldId,
                    saved.WorldName,
                    saved.TerritoryId,
                    saved.Instance,
                    saved.MarkDataId,
                    saved.MarkName,
                    saved.MapX,
                    saved.MapY,
                    saved.ObservedAt));
            }
        }
        finally
        {
            restoring = false;
        }
    }

    private void PersistQueue()
    {
        if (restoring)
            return;

        var pending = new List<HuntQueueEntry>();
        if (coordinator.Active is not null)
            pending.Add(coordinator.Active);
        pending.AddRange(coordinator.Queue.Entries);
        config.PendingHunts = pending.Select(entry => new PersistedHunt
        {
            WorldId = entry.Key.WorldId,
            WorldName = entry.WorldName,
            TerritoryId = entry.Key.TerritoryId,
            Instance = entry.Key.Instance,
            MarkDataId = entry.Key.MarkDataId,
            MarkName = entry.MarkName,
            MapX = entry.MapX,
            MapY = entry.MapY,
            ObservedAt = entry.LastObservedAt,
        }).ToList();
        config.Save();
    }

    private void AddTestReport()
    {
        if (game.CurrentWorldId == 0 || string.IsNullOrWhiteSpace(game.CurrentWorldName))
            return;

        coordinator.Observe(new HuntEvent(
            HuntEventKind.Reported,
            HuntSource.ManualTest,
            $"test-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            game.CurrentWorldId,
            game.CurrentWorldName,
            813,
            1,
            8905,
            "Tyger",
            23.3f,
            22.1f,
            DateTimeOffset.UtcNow));
    }

    private void DrawUi()
    {
        if (!windowOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(660, 760), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Sentinel Hunts###SentinelHunts", ref windowOpen))
        {
            ImGui.End();
            return;
        }

        ImGui.TextUnformatted("CLEAN S-RANK TEST BUILD");
        ImGui.TextWrapped("Sentinel Hunts has its own assembly, command, configuration, queue, and internal name. It can be installed beside S Rank Sentinel without replacing it.");
        ImGui.Separator();

        ImGui.TextUnformatted($"State: {coordinator.State}");
        ImGui.TextWrapped($"Status: {coordinator.Status}");
        ImGui.TextUnformatted($"vnavmesh: {(coordinator.VNavmeshReady ? "ready" : "not ready")}");
        ImGui.TextUnformatted($"Lifestream: {coordinator.LifestreamActivity}");
        if (coordinator.Active is { } active)
            ImGui.TextWrapped($"Active: {active.MarkName} | {active.WorldName} | territory {active.Key.TerritoryId} | instance {active.Key.Instance}");
        ImGui.TextUnformatted($"Queued hunts: {coordinator.Queue.Entries.Count}");

        ImGui.Separator();
        var automation = config.AutomationEnabled;
        if (ImGui.Checkbox("Enable live travel, movement, and combat", ref automation))
        {
            config.AutomationEnabled = automation;
            if (!automation)
                coordinator.StopAutomation();
            config.Save();
        }
        ImGui.TextWrapped("Off by default. Observation mode still receives, filters, deduplicates, and displays reports without controlling the character.");

        if (ImGui.Button("STOP NOW"))
        {
            config.AutomationEnabled = false;
            coordinator.StopAutomation();
            config.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button("Retry current"))
            coordinator.RetryCurrent();
        ImGui.SameLine();
        if (ImGui.Button("Skip current"))
            coordinator.SkipCurrent();

        ImGui.Separator();
        ImGui.TextUnformatted("ALERT SOURCES");
        var enableSonar = config.EnableSonar;
        if (DrawCheckbox("Sonar structured map links", ref enableSonar, save: false))
        {
            config.EnableSonar = enableSonar;
            config.Save();
        }
        var enableHuntAlerts = config.EnableHuntAlerts;
        if (DrawCheckbox("HuntAlerts IPC", ref enableHuntAlerts, save: false))
        {
            config.EnableHuntAlerts = enableHuntAlerts;
            config.Save();
        }
        ImGui.TextWrapped($"Sonar: {sonar.Status}");
        ImGui.TextWrapped($"HuntAlerts: {huntAlerts.Status}");
        ImGui.TextWrapped("Faloop direct feed: held offline in this first build while its undocumented private-feed contract and authentication are isolated and tested.");

        ImGui.Separator();
        ImGui.TextUnformatted("EXPANSIONS");
        var expansionChanged = false;
        var arr = config.EnableARealmReborn;
        if (DrawCheckbox("A Realm Reborn", ref arr, save: false))
        {
            config.EnableARealmReborn = arr;
            expansionChanged = true;
        }
        var heavensward = config.EnableHeavensward;
        if (DrawCheckbox("Heavensward", ref heavensward, save: false))
        {
            config.EnableHeavensward = heavensward;
            expansionChanged = true;
        }
        var stormblood = config.EnableStormblood;
        if (DrawCheckbox("Stormblood", ref stormblood, save: false))
        {
            config.EnableStormblood = stormblood;
            expansionChanged = true;
        }
        var shadowbringers = config.EnableShadowbringers;
        if (DrawCheckbox("Shadowbringers", ref shadowbringers, save: false))
        {
            config.EnableShadowbringers = shadowbringers;
            expansionChanged = true;
        }
        var endwalker = config.EnableEndwalker;
        if (DrawCheckbox("Endwalker", ref endwalker, save: false))
        {
            config.EnableEndwalker = endwalker;
            expansionChanged = true;
        }
        var dawntrail = config.EnableDawntrail;
        if (DrawCheckbox("Dawntrail", ref dawntrail, save: false))
        {
            config.EnableDawntrail = dawntrail;
            expansionChanged = true;
        }
        if (expansionChanged)
        {
            coordinator.Queue.Prune(config.IsEnabled, DateTimeOffset.UtcNow,
                TimeSpan.FromMinutes(config.AlertFreshnessMinutes));
            PersistQueue();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("SAFETY");
        var clearance = config.SafeHitboxClearance;
        if (ImGui.SliderFloat("Hitbox clearance", ref clearance, 20f, 40f, "%.0f yalms"))
        {
            config.SafeHitboxClearance = clearance;
            config.Save();
        }
        var hp = config.EngageHpPercent;
        if (ImGui.SliderFloat("Tomahawk HP threshold", ref hp, 1f, 99f, "%.0f%%"))
        {
            config.EngageHpPercent = hp;
            config.Save();
        }
        ImGui.TextWrapped("The clearance is measured from the S rank's hitbox. Sentinel Hunts approaches only after the mark is in combat and at/below the threshold, requests one WAR Tomahawk, then retreats.");

        ImGui.Separator();
        if (ImGui.Button("Add observation-mode Tyger test report"))
            AddTestReport();
        ImGui.SameLine();
        if (ImGui.Button("Clear queued reports"))
            coordinator.ClearQueue();

        if (coordinator.Queue.Entries.Count > 0)
        {
            ImGui.Spacing();
            foreach (var entry in coordinator.Queue.Entries.Take(12))
                ImGui.BulletText($"{entry.MarkName} — {entry.WorldName} — {string.Join("+", entry.Sources)}");
        }

        ImGui.End();
    }

    private bool DrawCheckbox(string label, ref bool value, bool save = true)
    {
        if (!ImGui.Checkbox(label, ref value))
            return false;
        if (save)
            config.Save();
        return true;
    }
}
