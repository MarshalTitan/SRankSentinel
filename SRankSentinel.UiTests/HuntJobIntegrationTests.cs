using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using SRankSentinel;

internal static partial class Program
{
    private static void TestRealHuntJobBoundaries()
    {
        var now = DateTime.UtcNow;
        var alert = PresenceFixture(now);
        var cfg = new Configuration();
        // A gearset selected on a separate character must never be used on this client.
        cfg.HuntingGearsets[202] = new(5, 21, "Tank");
        cfg = JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(cfg))!;
        var plugin = PresenceConsumer(cfg, alert, new());
        var objects = DispatchProxy.Create<IObjectTable, JobFixtureService>();
        var player = DispatchProxy.Create<IPlayerCharacter, JobFixtureService>();
        ((JobFixtureService)(object)objects).Player = player;
        var actor = (JobFixtureService)(object)player;
        var conditions = DispatchProxy.Create<ICondition, SsWatchNullService>();
        SetField(plugin, "huntJobPlayer", DispatchProxy.Create<IPlayerState, JobFixtureService>());
        SetField(plugin, "clientState", DispatchProxy.Create<IClientState, SsWatchNullService>());
        SetField(plugin, "objects", objects);
        SetField(plugin, "condition", conditions);
        SetField(plugin, "combat", new CombatController(null!, conditions, objects, null!));
        SetField(plugin, "vnav", RuntimeHelpers.GetUninitializedObject(typeof(VNavmeshIpc)));
        SetField(plugin, "huntJob", new HuntJobReadiness());
        SetField(plugin, "fullAssist", new FullCombatAssist());
        SetField(plugin, "tagCycle", new TagCheckpointCycle());
        var queue = new Queue<HuntAlertSnapshot>([alert with { World = "Kraken" }]);
        SetField(plugin, "pendingAlerts", queue);
        object? Read(string field) => typeof(Plugin).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin);

        actor.Job = 18;
        Check(CombatController.GetBaseRangedTagAction(actor.Job) == 0 && CombatController.GetBaseRangedTagAction(21) == 46,
            "Readiness must use the native tag action map; Fisher has no ranged tag.");
        foreach (var s in new[] { "ResetToUldah", "TeleportToTerritory", "WaitForPlayerReady", "TagApproach" })
        {
            PresenceState(plugin, s);
            PresenceCall(plugin, "Tick", now);
            Check(Read("state")!.ToString() == s && Read("current") == alert && queue.Count == 1 &&
                  Read("status")!.ToString()!.Contains("select a combat gearset"),
                  $"Fisher must hold {s} and its queue; another character's gearset must not be used.");
        }
        actor.Job = 21;
        Check((bool)PresenceCall(plugin, "EnsureHuntJob", now.AddSeconds(1))!, "Manual Warrior correction did not release pause.");
        SetField(plugin, "activeTagActionId", (uint)46);
        actor.Job = 18;
        Check(!(bool)PresenceCall(plugin, "EnsureHuntJob", now.AddSeconds(2))! && (uint)Read("activeTagActionId")! == 0,
            "Switch to Fisher must invalidate Warrior's cached Tomahawk before tag dispatch.");
    }
}

public class JobFixtureService : DispatchProxy
{
    public IPlayerCharacter? Player;
    public uint Job;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        "get_IsLoaded" => true,
        "get_ContentId" => (ulong)101,
        "get_LocalPlayer" => Player,
        "get_ClassJob" => Activator.CreateInstance(method.ReturnType, new object?[] { null, Job, null }),
        _ => method?.ReturnType is { IsValueType: true } t && t != typeof(void) ? Activator.CreateInstance(t) : null,
    };
}
