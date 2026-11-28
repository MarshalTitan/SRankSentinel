using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;
using SRankSentinel;

internal static partial class Program
{
    private static void TestDisclosurePersistence()
    {
        string[] sections = ["Main.Expansions", "Main.DataCenters", "Main.Mounting", "Main.Tagging", "Distance.close-safe",
            "Distance.proximity-sensitive", "Plugins.Normal", "Plugins.Combat", "History.Spawns"];
        foreach (var section in sections)
        foreach (var key in new[] { ImGuiKey.None, ImGuiKey.Space, ImGuiKey.GamepadFaceDown }) InContext(() =>
        {
            var pi = DispatchProxy.Create<IDalamudPluginInterface, RecordingPluginInterface>();
            var store = (RecordingPluginInterface)pi;
            var config = new Configuration { FullCombatAssist = true, TagAt25Percent = false,
                HuntDataCenters = NorthAmericanDataCenters.Aether, EnableCenturio = true };
            config.Initialize(pi);
            config.PreferredMounts[101] = 73;
            var snapshot = HuntSnapshot(config);
            var plugin = CreateConsumer(config);
            var method = typeof(Plugin).GetMethod("UiSection", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Vector2 center = default;
            bool focus = false;
            void Frame()
            {
                ImGui.NewFrame();
                ImGui.SetNextWindowPos(new(20), ImGuiCond.Always);
                ImGui.SetNextWindowSize(new(620, 300), ImGuiCond.Always);
                ImGui.Begin("Disclosure input");
                method.Invoke(plugin, [section, "A wrapping settings section title", true]);
                center = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) * 0.5f;
                if (focus) ImGui.SetKeyboardFocusHere(-1);
                ImGui.End();
                ImGui.Render();
            }
            Frame(); Frame();
            var saved = store.SaveCalls;
            var io = ImGui.GetIO();
            if (key == ImGuiKey.None)
            {
                io.AddMousePosEvent(center.X, center.Y); Frame();
                io.AddMouseButtonEvent(0, true); Frame();
                io.AddMouseButtonEvent(0, false); Frame();
            }
            else
            {
                io.AddMousePosEvent(-100, -100); focus = true; Frame(); focus = false; Frame();
                io.AddKeyEvent(key, true); Frame(); io.AddKeyEvent(key, false); Frame();
            }
            Check(config.CollapsedUiSections.Contains(section) && store.SaveCalls == saved + 1,
                $"Disclosure {section}/{key} did not toggle and save exactly once.");
            var loaded = JsonSerializer.Deserialize<Configuration>(store.SavedJson!)!;
            Check(loaded.CollapsedUiSections.Contains(section), "Disclosure preference did not survive reload.");
            Check(JsonNode.DeepEquals(snapshot, HuntSnapshot(config)) && JsonNode.DeepEquals(snapshot, HuntSnapshot(loaded)),
                "A presentation disclosure changed hunt, combat, distance, queue or history settings.");
        });
        Check(new Configuration().CollapsedUiSections.Count == 0,
            "Existing users must start with all sections expanded.");
        var old = JsonSerializer.Deserialize<Configuration>("{\"Version\":16,\"Enabled\":false}")!;
        Check(old.CollapsedUiSections.Count == 0 && !old.Enabled, "Upgrade lost section defaults or enabled state.");
    }

    private static void TestModeReadinessPresentation()
    {
        var normal = new PluginModeReadiness(true, true, true, true, "BossMod AI disabled", "Wrath missing", "Wrath Combo");
        Check(normal.NormalLabel == "Normal Tagging: READY" && normal.CombatLabel.Contains("BossMod AI disabled"),
            "Optional combat dependencies made ordinary tagging unhealthy.");
        var full = normal with { BossModReason = null, RotationReason = null };
        Check(full.CombatLabel == "Full Combat Assist: READY - BossMod + Wrath Combo", "Verified handoff is not ready.");
        Check((full with { Movement = false }).CombatLabel.Contains("vnavmesh"), "Full mode ignored normal requirements.");
        Check((full with { Alerts = false }).NormalLabel.Contains("HuntAlerts or Sonar"), "Alternative providers became mandatory together.");
        Check((full with { RotationName = "Rotation Solver Reborn", RotationReason = RotationSolverRebornIpc.Unsupported })
            .CombatLabel.Contains(RotationSolverRebornIpc.Unsupported), "Unverifiable RSR handoff was advertised as ready.");
    }

    private static void TestCollapsiblePageLayout()
    {
        foreach (var scale in new[] { 0.8f, 1f, 1.5f, 2f })
        foreach (var fontScale in new[] { 1f, 1.4f }) InContext(() =>
        {
            ImGui.GetIO().FontGlobalScale = scale;
            ImGui.GetIO().Fonts.Fonts[0].Scale = fontScale;
            var config = new Configuration { WindowTheme = 1, EnableCenturio = true };
            var plugin = CreateConsumer(config);
            LoadWindowPlacement(new Vector2(480, 520) * scale);
            foreach (var collapsed in new[] { false, true })
            {
                config.CollapsedUiSections = collapsed ? ["Main.Expansions", "Main.DataCenters", "Main.Mounting", "Main.Tagging"] : [];
                for (var frame = 0; frame < 20; frame++) ConsumerFrame(plugin);
                var page = FindWindow(Plugin.ConfigurationWindowId, "##Modern2Page");
                var settings = FindWindow(Plugin.ConfigurationWindowId, "##MainSettings");
                Check(page.ScrollMax.Y < 2f,
                    $"Recovery footer needs scrolling at {scale}/{fontScale}: {page.ScrollMax.Y}.");
                Check(settings.Size.Y > 0 && page.DC.CursorMaxPos.Y <= page.InnerRect.Max.Y + 1,
                    $"Recovery footer was clipped at {scale}/{fontScale}.");
            }
            config.WindowPage = (int)ConfigurationPage.DistanceProfiles;
            config.CollapsedUiSections.Clear();
            for (var frame = 0; frame < 4; frame++) ConsumerFrame(plugin);
            var expanded = FindWindow(Plugin.ConfigurationWindowId, "close-safe-profile").Size.Y;
            config.CollapsedUiSections.Add("Distance.close-safe");
            for (var frame = 0; frame < 4; frame++) ConsumerFrame(plugin);
            Check(FindWindow(Plugin.ConfigurationWindowId, "close-safe-profile").Size.Y < expanded,
                "Distance card did not collapse independently.");
            config.WindowPage = (int)ConfigurationPage.History;
            new HuntHistoryJournal(config).Spawn(HistoryAlert(DateTime.UtcNow), "HuntAlerts");
            for (var frame = 0; frame < 4; frame++) ConsumerFrame(plugin);
            Check(FindWindow(Plugin.ConfigurationWindowId, "##SpawnHistory").Active, "History list missing while expanded.");
            config.CollapsedUiSections.Add("History.Spawns");
            for (var frame = 0; frame < 4; frame++) ConsumerFrame(plugin);
            Check(!FindWindow(Plugin.ConfigurationWindowId, "##SpawnHistory").Active && config.SpawnHistory.Count == 1,
                "Collapse retained the list or removed History.");
        });
    }
}
