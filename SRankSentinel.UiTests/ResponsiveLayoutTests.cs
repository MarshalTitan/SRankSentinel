using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dalamud.Bindings.ImGui;
using SentinelCore.UI;
using SRankSentinel;

internal static partial class Program
{
    private static void TestNarrowWindowPlacement()
    {
        var core = SentinelModernAppLayout.MinimumWindowSize(hasActionDock: true);
        Check(core == new Vector2(384, 408) && Plugin.ModernMinimumWindowSize == new Vector2(480, 520),
            "Re-audit the published Core minimum before changing the consumer constraint.");
        foreach (var scale in new[] { 1f, 1.5f, 2f }) InContext(() =>
        {
            ImGui.GetIO().FontGlobalScale = scale;
            var config = new Configuration { WindowTheme = 1 };
            var plugin = CreateConsumer(config);
            LoadWindowPlacement(new Vector2(480, 740) * scale);
            uint id = 0;
            foreach (var theme in new[] { 1, 0, 1 })
            {
                config.WindowTheme = theme;
                for (var frame = 0; frame < 20; frame++) ConsumerFrame(plugin);
                var window = FindWindow(Plugin.ConfigurationWindowId);
                Check(window.Pos == new Vector2(145, 95) && window.Size == new Vector2(480, 740) * scale,
                    "Theme switching expanded or moved the narrower saved window.");
                if (id == 0) id = window.ID;
                Check(window.ID == id, "Theme switching changed the persisted identity.");
            }
        });
    }

    private static void TestNarrowClassic()
    {
        foreach (var scale in new[] { 1f, 1.5f, 2f }) InContext(() =>
        {
            ImGui.GetIO().FontGlobalScale = scale;
            var config = new Configuration { WindowTheme = 0 };
            var snapshot = HuntSnapshot(config);
            var plugin = CreateConsumer(config);
            LoadWindowPlacement(new Vector2(480, 520) * scale);
            for (var frame = 0; frame < 20; frame++) ConsumerFrame(plugin);
            var window = FindWindow(Plugin.ConfigurationWindowId);
            Check(window.ContentSize.X <= window.Size.X + 1f && window.ScrollMax.Y > 0,
                "Classic overflows horizontally or cannot scroll to its final controls.");
            var io = ImGui.GetIO();
            io.AddMousePosEvent(window.Pos.X + window.Size.X / 2f, window.Pos.Y + 70f * scale);
            ConsumerFrame(plugin);
            for (var frame = 0; frame < 12 && window.Scroll.Y < window.ScrollMax.Y - 1f; frame++)
            {
                io.AddMouseWheelEvent(0f, -10f);
                ConsumerFrame(plugin);
            }
            Check(window.Scroll.Y > 0, "Classic did not respond to native wheel scrolling.");
            // A hovered input can consume wheel events near the bottom. Explicitly request
            // the native end position to verify the final controls remain in the scroll range.
            ImGui.NewFrame();
            ImGui.Begin(Plugin.ConfigurationWindowId);
            ImGui.SetScrollY(window.ScrollMax.Y);
            ImGui.End();
            DrawConsumer(plugin);
            ImGui.Render();
            ConsumerFrame(plugin);
            Check(window.Scroll.Y >= window.ScrollMax.Y - 1f &&
                (window.Flags & (ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoNav)) == 0,
                $"Classic final controls unreachable at {scale}: scroll {window.Scroll.Y}/{window.ScrollMax.Y}, flags {window.Flags}.");
            Check(JsonNode.DeepEquals(snapshot, HuntSnapshot(config)), "Classic layout changed hunt settings.");
        });
    }

    private static void TestHistoryViewport()
    {
        foreach (var scale in new[] { 1f, 1.5f, 2f })
        foreach (var width in new[] { 480f, 620f }) InContext(() =>
        {
            ImGui.GetIO().FontGlobalScale = scale;
            var config = new Configuration { WindowTheme = 1, WindowPage = (int)ConfigurationPage.History };
            var journal = new HuntHistoryJournal(config);
            for (var index = 0; index < 50; index++)
                journal.Spawn(HistoryAlert(DateTime.UtcNow.AddHours(-index), "Long creature name for wrapped history " + index), "Sonar");
            var saved = JsonSerializer.Serialize(config);
            var plugin = CreateConsumer(config);
            SetField(plugin, "historyJournal", journal);
            var draw = (Action)typeof(Plugin).GetMethod("DrawModernContent", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate(typeof(Action), plugin);
            Vector2 clearMinimum = default, clearMaximum = default;
            SetField(plugin, "drawModernContent", (Action)(() =>
            {
                draw();
                clearMinimum = ImGui.GetItemRectMin();
                clearMaximum = ImGui.GetItemRectMax();
            }));
            float previousHeight = 0;
            foreach (var height in new[] { 520f, 800f, 1080f })
            {
                LoadWindowPlacement(new Vector2(width, height) * scale);
                for (var frame = 0; frame < 20; frame++) ConsumerFrame(plugin);
                var page = FindWindow(Plugin.ConfigurationWindowId, "##Modern2Page");
                var body = FindWindow(Plugin.ConfigurationWindowId, "##HistoryBody");
                var list = FindWindow(Plugin.ConfigurationWindowId, "##SpawnHistory");
                Check(clearMinimum.X >= page.Pos.X && clearMaximum.X <= page.Pos.X + page.Size.X + 1f &&
                    clearMaximum.Y <= page.Pos.Y + page.Size.Y &&
                    MathF.Abs(clearMaximum.Y - (page.Pos.Y + page.Size.Y - page.WindowPadding.Y)) <= 2f,
                    "Clear history escaped the pane or stopped being bottom-anchored.");
                Check(!page.ScrollbarY && !page.ScrollbarX, "The outer page can scroll the Clear button away.");
                Check(list.Size.Y >= 120f * scale - 1f,
                    $"Report viewport minimum at {width}x{height}/{scale}: {list.Size}.");
                // Hidden native children retain content measurements from an earlier width.
                // Check actual content after the short body's list has been scrolled into view.
                if (!list.SkipItems)
                    Check(list.ContentSize.X <= list.Size.X + 1f,
                        $"Visible reports overflow at {width}x{height}/{scale}: size {list.Size}, content {list.ContentSize}.");
                if (height == 1080f)
                    Check(list.Size.Y > previousHeight + 200f * scale && list.Size.Y > 300f * scale,
                        "Taller windows still cap the report list at about two rows.");
                previousHeight = list.Size.Y;
                if (body.ScrollMax.Y > 0)
                {
                    // Wheel the short body while hovering its summary, outside the nested report list.
                    var io = ImGui.GetIO();
                    io.AddMousePosEvent(body.Pos.X + body.Size.X / 2f, body.Pos.Y + 5f);
                    ConsumerFrame(plugin);
                    io.AddMouseWheelEvent(0f, -100f);
                    ConsumerFrame(plugin);
                    for (var frame = 0; frame < 20; frame++) ConsumerFrame(plugin);
                    Check(body.Scroll.Y > 0 && list.Active && !list.SkipItems && list.ContentSize.Y > list.Size.Y &&
                        list.ContentSize.X <= list.Size.X + 1f,
                        $"Short-body reports unreachable/overflow at {width}x{height}/{scale}: size {list.Size}, content {list.ContentSize}, skip {list.SkipItems}, body {body.Scroll}/{body.ScrollMax}.");
                }
                Check(JsonSerializer.Serialize(config) == saved, "Resizing History changed filters, records, tags, or credit.");
            }
        });
    }
}
