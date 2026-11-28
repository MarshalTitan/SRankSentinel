using System.Reflection;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using System.Text.Json;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using SRankSentinel;

internal static partial class Program
{
    private static unsafe void TestMountDropdownInput() => InContext(() =>
    {
        var pi = DispatchProxy.Create<IDalamudPluginInterface, RecordingPluginInterface>();
        var cfg = new Configuration();
        cfg.Initialize(pi);
        var plugin = CreateConsumer(cfg);
        SetField(plugin, "huntJobPlayer", DispatchProxy.Create<IPlayerState, JobFixtureService>());
        var choices = new MountChoices();
        choices.Refresh(101, DateTime.UtcNow.AddMinutes(1), () => [new(90, "Fenrir"), new(45, "Amaro")]);
        SetField(plugin, "mountChoices", choices);
        Vector2 combo = default;
        float lineHeight = 0f, halfText = 0f;
        var io = ImGui.GetIO();
        void Frame()
        {
            ImGui.NewFrame();
            ImGui.SetNextWindowPos(new(30, 30));
            ImGui.SetNextWindowSize(new(420, 260));
            ImGui.Begin("Mount selector test");
            lineHeight = ImGui.GetTextLineHeightWithSpacing();
            halfText = ImGui.GetTextLineHeight() / 2;
            combo = ImGui.GetCursorScreenPos() + new Vector2(12, ImGui.GetFrameHeight() / 2);
            PresenceCall(plugin, "DrawPreferredMount");
            ImGui.End();
            ImGui.Render();
        }
        void Click(Vector2 point)
        {
            io.AddMousePosEvent(point.X, point.Y); Frame();
            io.AddMouseButtonEvent(0, true); Frame();
            io.AddMouseButtonEvent(0, false); Frame();
            Frame();
        }

        Frame(); Frame();
        foreach (var (row, expected) in new (int, uint)[] { (2, 90), (0, 0) })
        {
            Click(combo);
            var popup = FindWindow("##Combo_00");
            Check(popup.Active, "Preferred Mount menu did not open.");
            Click(popup.DC.CursorStartPos + new Vector2(12, row * lineHeight + halfText));
            Check(cfg.PreferredMounts.TryGetValue(101, out var actual) && actual == expected,
                "Selecting an unlocked mount or roulette did not save its ID.");
        }
        var saved = (RecordingPluginInterface)pi;
        Check(saved.SaveCalls == 2 && JsonSerializer.Deserialize<Configuration>(saved.SavedJson!)!.GetPreferredMount(101) == 0,
            "Dropdown selection did not persist to the character configuration.");
    });

    private static void TestMountGuards()
    {
        var cfg = new Configuration();
        cfg.PreferredMounts[101] = 73;
        var plugin = CreateConsumer(cfg);
        var condition = DispatchProxy.Create<ICondition, MountConditionFixture>();
        SetField(plugin, "condition", condition);
        foreach (var flag in new[] { ConditionFlag.Mounted, ConditionFlag.InFlight, ConditionFlag.Mounting, ConditionFlag.Mounting71 })
        {
            ((MountConditionFixture)condition).Active = flag;
            var ready = (bool)PresenceCall(plugin, "EnsureMounted", DateTime.UtcNow)!;
            Check(ready == (flag is ConditionFlag.Mounted or ConditionFlag.InFlight), "Unexpected mount handoff for " + flag);
        }
        var player = DispatchProxy.Create<IPlayerState, JobFixtureService>();
        SetField(plugin, "huntJobPlayer", player);
        var owner = typeof(Plugin).GetProperty("CharacterContentId", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var world in new[] { "Diabolos", "Siren", "Leviathan" })
        {
            SetField(plugin, "current", PresenceFixture(DateTime.UtcNow) with { World = world });
            Check(cfg.GetPreferredMount((ulong)owner.GetValue(plugin)!) == 73, "Hunt World changed the mount preference.");
        }
    }

    private static void TestMountPreferences()
    {
        var config = JsonSerializer.Deserialize<Configuration>("{\"Version\":16}")!;
        var pi = DispatchProxy.Create<IDalamudPluginInterface, RecordingPluginInterface>();
        var store = (RecordingPluginInterface)pi;
        config.Initialize(pi);
        Check(config.GetPreferredMount(111) == 1 && config.GetPreferredMount(222) == 1, "Missing legacy selection changed the default.");
        config.SavePreferredMount(111, 1);
        config.SavePreferredMount(222, 0);
        config.SavePreferredMount(333, 73);
        config.SavePreferredMount(444, uint.MaxValue);
        var saves = store.SaveCalls;
        config.SavePreferredMount(0, 99);
        var restored = JsonSerializer.Deserialize<Configuration>(store.SavedJson!)!;
        Check(store.SaveCalls == saves && restored.PreferredMounts.Count == 4 &&
            restored.GetPreferredMount(111) == 1 && restored.GetPreferredMount(222) == 0 &&
            restored.GetPreferredMount(333) == 73 && restored.GetPreferredMount(444) == uint.MaxValue &&
            restored.GetPreferredMount(0) == 0, "Preferences leaked, were reset, or were saved without a character.");
        var choices = new MountChoices();
        choices.Refresh(444, DateTime.UtcNow, () => []);
        Check(choices.DisplaySelection(uint.MaxValue, true).Name.Contains("Mount Roulette") &&
            restored.GetPreferredMount(444) == uint.MaxValue, "Unresolved selection was lost.");
    }
}

public class MountConditionFixture : DispatchProxy
{
    public ConditionFlag Active;
    protected override object? Invoke(MethodInfo? method, object?[]? args) =>
        method?.Name == "get_Item" && args is [ConditionFlag flag] && flag == Active;
}
