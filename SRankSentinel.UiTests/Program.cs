using System.Numerics;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dalamud.Bindings.ImGui;
using SentinelCore.UI;
using SRankSentinel;

internal static class Program
{
    private static int passed;
    private static string DalamudDirectory => Environment.GetEnvironmentVariable("DALAMUD_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XIVLauncher", "addon", "Hooks", "dev");

    public static int Main()
    {
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var path = Path.Combine(DalamudDirectory, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        try
        {
            Test("v15 migration preserves all hunt values, credentials, and queues", TestLegacyMigration);
            Test("Modern choice and selected page survive save/load", TestThemeRoundTrip);
            Test("merged pages migrate to Main without changing theme or hunt settings", TestMergedPageSelection);
            Test("new and invalid appearance states default safely to Classic", TestAppearanceDefaults);
            Test("canonical switch toggles once on a mouse click/release", TestMouseToggle);
            Test("canonical switch accepts keyboard navigation activation", () => TestNavigationToggle(ImGuiKey.Space));
            Test("canonical switch accepts controller navigation activation", () => TestNavigationToggle(ImGuiKey.GamepadFaceDown));
            Test("disabled canonical switch rejects pointer and keyboard activation", TestDisabledToggle);
            Test("shared shell/cards balance style and render at multiple UI scales", TestModernShell);
            Test("Classic and every Modern page render at the minimum size", TestConsumerPages);
            Test("saved position and larger size survive Classic/Modern switching", TestWindowPlacement);
            Test("undersized saved Modern window expands without moving", TestMinimumWindow);
            Test("collapse and close retain native window behavior", TestWindowControls);
            Console.WriteLine($"{passed}/13 UI and migration tests passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            Console.Error.WriteLine($"UI test runner failed after {passed} passing tests.");
            return 1;
        }
    }

    private static void Test(string name, Action action)
    {
        action();
        passed++;
        Console.WriteLine("PASS  " + name);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static JsonObject HuntSnapshot(Configuration config)
    {
        var snapshot = JsonSerializer.SerializeToNode(config)!.AsObject();
        snapshot.Remove("Version");
        snapshot.Remove("WindowTheme");
        snapshot.Remove("WindowPage");
        return snapshot;
    }

    private static void TestLegacyMigration()
    {
        var config = new Configuration
        {
            Version = 15, Enabled = false, WindowTheme = 1, WindowPage = 4,
            EnableCenturio = true, EnableShadowbringers = false, EnableEndwalker = false,
            AlertFreshnessMinutes = 73, TravelTimeoutSeconds = 237, LocateTimeoutSeconds = 57,
            FaloopUsername = "migration-fixture", FaloopSessionId = "test-fixture-session",
            RememberFaloopLogin = true, FaloopProtectedPassword = "test-fixture-ciphertext",
            CloseSafeProfile = new() { FlagApproachDistance = 17, WaitingDistance = 14, EmergencyDistance = 8, EngageHpPercent = 55 },
            ProximitySensitiveProfile = new() { FlagApproachDistance = 33, WaitingDistance = 29, EmergencyDistance = 21, EngageHpPercent = 25 },
            PendingAlerts = [new() { World = "Coeurl", CreatureName = "Tyger", TerritoryId = 813, Instance = 2, MapX = 13.5f, MapY = 17.8f }],
            KilledAlerts = [new() { Key = "fixture-killed-alert", KilledAtUtc = new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc) }],
        };
        var before = HuntSnapshot(config);
        config.MigrateWindowAppearance();
        Check(config.Version == 16 && config.WindowTheme == 0 && config.WindowPage == 0, "Legacy users must explicitly migrate to Classic.");
        Check(JsonNode.DeepEquals(before, HuntSnapshot(config)), "Appearance migration changed a hunt/configuration value.");
        config.MigrateWindowAppearance();
        Check(JsonNode.DeepEquals(before, HuntSnapshot(config)), "Migration is not idempotent.");
    }

    private static void TestThemeRoundTrip()
    {
        foreach (var page in Enum.GetValues<ConfigurationPage>())
        {
            var config = new Configuration { WindowTheme = 1, WindowPage = (int)page };
            config.MigrateWindowAppearance();
            var reloaded = JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(config))!;
            reloaded.MigrateWindowAppearance();
            Check(reloaded.WindowTheme == 1 && reloaded.WindowPage == (int)page, "Saved Modern selection/page were lost.");
        }
        Check(Plugin.ConfigurationWindowId == "S Rank Sentinel###SRankSentinel", "Window identity changed.");
    }

    private static void TestMergedPageSelection()
    {
        foreach (var theme in new[] { 0, 1 })
        foreach (var (previous, expected) in new[] { (0, 0), (1, 0), (2, 2), (3, 0), (4, 0) })
        {
            var config = new Configuration
            {
                Version = 16, WindowTheme = theme, WindowPage = previous,
                Enabled = false, EnableCenturio = true, EnableEndwalker = false,
                AlertFreshnessMinutes = 73,
                FaloopUsername = "page-migration-fixture", FaloopSessionId = "fixture-session",
                CloseSafeProfile = new() { FlagApproachDistance = 17, WaitingDistance = 14, EmergencyDistance = 8, EngageHpPercent = 55 },
            };
            var before = HuntSnapshot(config);
            config.MigrateWindowAppearance();
            Check(config.WindowTheme == theme && config.WindowPage == expected,
                $"Saved theme/page mapping regressed for {theme}/{previous}.");
            Check(JsonNode.DeepEquals(before, HuntSnapshot(config)), "Merging pages changed hunt settings.");
            var reloaded = JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(config))!;
            reloaded.MigrateWindowAppearance();
            Check(reloaded.WindowTheme == theme && reloaded.WindowPage == expected &&
                JsonNode.DeepEquals(before, HuntSnapshot(reloaded)), "Merged page migration did not persist idempotently.");
        }
    }

    private static void TestAppearanceDefaults()
    {
        var fresh = new Configuration();
        Check(fresh.Version == 16 && fresh.WindowTheme == 0 && fresh.WindowPage == 0, "New defaults changed.");
        fresh.WindowTheme = -99;
        fresh.WindowPage = 999;
        fresh.MigrateWindowAppearance();
        Check(fresh.WindowTheme == 0 && fresh.WindowPage == 0, "Invalid appearance did not normalize safely.");
    }

    private static unsafe void InContext(Action action)
    {
        var context = ImGui.CreateContext();
        try
        {
            var io = ImGui.GetIO();
            io.IniFilename = null;
            io.LogFilename = null;
            io.DisplaySize = new Vector2(1600, 1200);
            io.DeltaTime = 1f / 60f;
            io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard | ImGuiConfigFlags.NavEnableGamepad;
            io.BackendFlags |= ImGuiBackendFlags.HasGamepad;
            io.Fonts.AddFontDefault();
            Check(io.Fonts.Build(), "Font atlas did not build.");
            action();
        }
        finally { ImGui.DestroyContext(context); }
    }

    private static (Vector2 Minimum, Vector2 Maximum, bool Focused) ToggleFrame(ref bool value, bool focus = false, bool disabled = false)
    {
        ImGui.NewFrame();
        ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(520, 320), ImGuiCond.Always);
        ImGui.Begin("Toggle-test", ImGuiWindowFlags.NoSavedSettings);
        ImGui.BeginDisabled(disabled);
        ConsumerToggleActivation.Draw("Test", "Enable hunting", ref value, 1f);
        var result = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), ImGui.IsItemFocused());
        if (focus) ImGui.SetKeyboardFocusHere(-1);
        ImGui.EndDisabled();
        ImGui.End();
        ImGui.Render();
        return result;
    }

    private static void TestMouseToggle() => InContext(() =>
    {
        var value = false;
        var row = ToggleFrame(ref value);
        var center = (row.Minimum + row.Maximum) / 2f;
        var io = ImGui.GetIO();
        io.AddMousePosEvent(center.X, center.Y);
        ToggleFrame(ref value);
        io.AddMouseButtonEvent(0, true);
        ToggleFrame(ref value);
        Check(value, "Pointer press did not activate the canonical switch.");
        io.AddMouseButtonEvent(0, false);
        ToggleFrame(ref value);
        Check(value, "Pointer release toggled the value twice.");
    });

    private static void TestNavigationToggle(ImGuiKey key) => InContext(() =>
    {
        var value = false;
        ToggleFrame(ref value, focus: true);
        var focused = ToggleFrame(ref value);
        Check(focused.Focused, "The invisible activation bridge did not receive native navigation focus.");
        var io = ImGui.GetIO();
        io.AddKeyEvent(key, true);
        ToggleFrame(ref value);
        Check(value, $"{key} did not activate the canonical switch.");
        io.AddKeyEvent(key, false);
        ToggleFrame(ref value);
        Check(value, $"{key} release toggled the value twice.");
    });

    private static void TestDisabledToggle() => InContext(() =>
    {
        var value = false;
        var row = ToggleFrame(ref value, focus: true, disabled: true);
        var io = ImGui.GetIO();
        var center = (row.Minimum + row.Maximum) / 2f;
        io.AddMousePosEvent(center.X, center.Y);
        ToggleFrame(ref value, disabled: true);
        io.AddMouseButtonEvent(0, true);
        io.AddKeyEvent(ImGuiKey.Space, true);
        ToggleFrame(ref value, disabled: true);
        Check(!value, "Disabled canonical control activated.");
        io.AddMouseButtonEvent(0, false);
        io.AddKeyEvent(ImGuiKey.Space, false);
        ToggleFrame(ref value, disabled: true);
        Check(!value, "Disabled canonical control activated on release.");
    });

    private static void TestModernShell() => InContext(() =>
    {
        var scope = new SentinelModernStyleScope();
        foreach (var scale in new[] { 1f, 1.5f })
        foreach (var size in new[] { new Vector2(620, 520), new Vector2(700, 600), new Vector2(920, 720) })
        {
            var originalRounding = ImGui.GetStyle().WindowRounding;
            for (var frame = 0; frame < 3; frame++)
            {
                var navigationCalls = 0;
                var contentCalls = 0;
                scope.Push(scale);
                try
                {
                    ImGui.NewFrame();
                    ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always);
                    ImGui.SetNextWindowSize(size * scale, ImGuiCond.Always);
                    ImGui.Begin("Modern-shell-test", ImGuiWindowFlags.NoSavedSettings);
                    SentinelModernConfigurationShell.Draw(
                        new SentinelModernShellOptions("Test", "SENTINEL", "S RANK SENTINEL", "Hunt status")
                        { Scale = scale },
                        () => { navigationCalls++; SentinelModernNavigation.Item("Main", "Main", true, scale); },
                        () =>
                        {
                            contentCalls++;
                            SentinelModernUi.PageHeading("Main", string.Empty);
                            using var card = SentinelModernCard.Begin("Test-card");
                            if (card.IsVisible) SentinelModernUi.SectionHeader("HUNTING");
                        });
                    ImGui.End();
                    CheckShellGeometry("Modern-shell-test", scale);
                    ImGui.Render();
                    if (frame > 0)
                    {
                        Check(navigationCalls == 1 && contentCalls == 1, "Shared shell omitted a pane.");
                        Check(ImGui.GetDrawData().TotalVtxCount > 0, "Shared shell produced no geometry.");
                    }
                }
                finally { scope.Pop(); }
                Check(ImGui.GetStyle().WindowRounding == originalRounding, "Modern style scope did not restore Classic style.");
            }
        }
        scope.Dispose();
    });

    private static unsafe ImGuiWindowPtr FindWindow(string parent, string? child = null)
    {
        var windows = ImGui.GetCurrentContext().Windows;
        for (var index = 0; index < windows.Size; index++)
        {
            var window = windows[index];
            var name = Marshal.PtrToStringUTF8((nint)window.Name)!;
            if (child is null ? name == parent : name.StartsWith(parent + "/") && name.Contains(child))
                return window;
        }
        throw new Exception($"Missing native window: {parent}/{child}");
    }

    private static void CheckShellGeometry(string parent, float scale)
    {
        var header = FindWindow(parent, "##Header");
        var navigation = FindWindow(parent, "##Navigation");
        var content = FindWindow(parent, "##Content");
        Check(header.Size.Y >= 84f * scale, "Header is below Core's corrected minimum.");
        Check((header.Flags & (ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)) ==
            (ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse) &&
            !header.ScrollbarX && !header.ScrollbarY, "Header scrollbars or mouse scrolling were enabled.");
        Check(navigation.Pos.X + navigation.Size.X <= content.Pos.X &&
            MathF.Abs(navigation.Pos.Y - content.Pos.Y) < 1f, "Navigation stacked above content or left the left pane.");
        Check(header.Pos.Y + header.Size.Y <= navigation.Pos.Y, "Navigation was inserted into the header.");
        Check(content.Size.X >= 250f * scale, "Right pane is unusable at the minimum window width.");
    }

    private static Plugin CreateConsumer(Configuration config)
    {
        // Exercise the real renderer without constructing game services or starting hunt automation.
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        SetField(plugin, "config", config);
        SetField(plugin, "configOpen", true);
        SetField(plugin, "pendingAlerts", new Queue<HuntAlertSnapshot>());
        SetField(plugin, "status", "UI regression fixture");
        SetField(plugin, "modernStyle", new SentinelModernStyleScope());
        return plugin;
    }

    private static void SetField(Plugin plugin, string name, object value) =>
        typeof(Plugin).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, value);

    private static void DrawConsumer(Plugin plugin)
    {
        var context = ImGui.GetCurrentContext();
        var colors = context.ColorStack.Size;
        var styles = context.StyleVarStack.Size;
        var windows = context.CurrentWindowStack.Size;
        try { typeof(Plugin).GetMethod("DrawUi", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(plugin, null); }
        catch (TargetInvocationException exception) { ExceptionDispatchInfo.Capture(exception.InnerException!).Throw(); }
        Check(context.ColorStack.Size == colors && context.StyleVarStack.Size == styles &&
            context.CurrentWindowStack.Size == windows, "Consumer style or Begin/End stack is unbalanced.");
    }

    private static void ConsumerFrame(Plugin plugin)
    {
        ImGui.NewFrame();
        DrawConsumer(plugin);
        ImGui.Render();
    }

    private static void LoadWindowPlacement(Vector2 size, bool collapsed = false) =>
        ImGui.LoadIniSettingsFromMemory($"[Window][{Plugin.ConfigurationWindowId}]\nPos=145,95\nSize={(int)size.X},{(int)size.Y}\nCollapsed={(collapsed ? 1 : 0)}\n");

    private static void TestConsumerPages() => InContext(() =>
    {
        var config = new Configuration
        {
            CloseSafeProfile = new() { FlagApproachDistance = 17, WaitingDistance = 14, EmergencyDistance = 8, EngageHpPercent = 55 },
            ProximitySensitiveProfile = new() { FlagApproachDistance = 33, WaitingDistance = 29, EmergencyDistance = 21, EngageHpPercent = 25 },
        };
        var snapshot = HuntSnapshot(config);
        var plugin = CreateConsumer(config);
        LoadWindowPlacement(new Vector2(620, 520));
        foreach (var theme in new[] { 0, 1 })
        foreach (var page in Enum.GetValues<ConfigurationPage>())
        {
            config.WindowTheme = theme;
            config.WindowPage = (int)page;
            for (var frame = 0; frame < 3; frame++) ConsumerFrame(plugin);
            Check(ImGui.GetDrawData().TotalVtxCount > 0, "Consumer page did not render.");
            if (theme == 1)
            {
                CheckShellGeometry(Plugin.ConfigurationWindowId, 1f);
                var content = FindWindow(Plugin.ConfigurationWindowId, "##Content");
                var card = FindWindow(Plugin.ConfigurationWindowId, "SRankSentinel-Page");
                Check(card.Pos.X >= content.Pos.X, "Settings left the right content pane.");
                Check(MathF.Abs(card.Pos.Y - content.Pos.Y - content.WindowPadding.Y) < 1f,
                    $"A redundant heading is still taking space above {page} settings.");
                Check(card.ContentSize.X <= card.Size.X + 1f,
                    $"{page} controls overflow the right pane at 620x520.");
            }
            Check(JsonNode.DeepEquals(snapshot, HuntSnapshot(config)), "Rendering a theme/page changed hunt configuration.");
        }
    });

    private static void TestWindowPlacement() => InContext(() =>
    {
        var config = new Configuration();
        var plugin = CreateConsumer(config);
        LoadWindowPlacement(new Vector2(1040, 860));
        uint id = 0;
        foreach (var theme in new[] { 0, 1, 0, 1 })
        {
            config.WindowTheme = theme;
            ConsumerFrame(plugin);
            var window = FindWindow(Plugin.ConfigurationWindowId);
            Check(window.Pos == new Vector2(145, 95) && window.Size == new Vector2(1040, 860),
                "Theme switch reset saved position or a larger saved size.");
            if (id == 0) id = window.ID;
            Check(window.ID == id, "Theme switch changed native window identity.");
        }
        Check(ImGui.SaveIniSettingsToMemory().Contains("Pos=145,95\nSize=1040,860"), "Saved placement did not round-trip.");
    });

    private static void TestMinimumWindow() => InContext(() =>
    {
        var config = new Configuration { WindowTheme = 1 };
        var plugin = CreateConsumer(config);
        LoadWindowPlacement(new Vector2(480, 320));
        for (var frame = 0; frame < 3; frame++) ConsumerFrame(plugin);
        var window = FindWindow(Plugin.ConfigurationWindowId);
        Check(window.Size == new Vector2(620, 520) && window.Pos == new Vector2(145, 95),
            "Modern minimum did not preserve position while expanding an undersized window.");
        CheckShellGeometry(Plugin.ConfigurationWindowId, 1f);
    });

    private static void TestWindowControls() => InContext(() =>
    {
        var config = new Configuration { WindowTheme = 1 };
        var plugin = CreateConsumer(config);
        LoadWindowPlacement(new Vector2(920, 720), collapsed: true);
        ConsumerFrame(plugin);
        var window = FindWindow(Plugin.ConfigurationWindowId);
        Check(window.Collapsed && (window.Flags & (ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoNav)) == 0,
            "Native collapse/title/navigation behavior regressed.");
        SetField(plugin, "configOpen", false);
        var snapshot = HuntSnapshot(config);
        ConsumerFrame(plugin);
        Check(JsonNode.DeepEquals(snapshot, HuntSnapshot(config)), "Closing the window modified hunt settings.");
    });
}
