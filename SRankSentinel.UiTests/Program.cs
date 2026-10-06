using System.Numerics;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using SentinelCore.UI;
using SRankSentinel;

internal static partial class Program
{
    private static int passed;
    private static SentinelModernMotion testMotion = null!;
    private static ImFontPtr testIconFont;
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
            Test("separate bottom-left Classic button saves on mouse, keyboard, and controller activation", TestSidebarThemeSwitch);
            Test("saved position and larger size survive Classic/Modern switching", TestWindowPlacement);
            Test("undersized saved Modern window expands without moving", TestMinimumWindow);
            Test("Classic native controls and Modern custom minimize/close work", TestWindowControls);
            Test("responsive settings remain within the right pane at multiple sizes/scales", TestResponsiveConsumer);
            Test("Modern header dragging and native resizing preserve placement", TestDragAndResize);
            Test("Dalamud reduced motion disables decorative animation", TestReducedMotion);
            Test("mouse-click navigation labels stop following the pointer; navigation focus retains labels", TestNavigationTooltips);
            Test("Plugins list includes all companions and handles disabled, missing, and loaded states", TestCompanionPlugins);
            Test("Dalamud activation contracts match the API-15 installer", TestActivationContract);
            Test("history deduplicates reports, separates respawns, and persists bounded records", TestHistoryPersistence);
            Test("personal credit requires a tagged final pull, kill, and contextual reward in either order", TestHistoryCredit);
            Test("history rejects stale, wrong-instance, ambiguous, reset, and restored-session credit", TestHistoryCreditRejections);
            Test("reward recognition accepts acquisition and rejects other messages", TestRewardReceipts);
            Test("both populated histories render and balance scopes at multiple sizes/scales", TestPopulatedHistory);
            Test("compact companion cards contain their status/actions at all UI scales", TestCompanionCardLayout);
            Test("empty History renders summary cards without fabricating records", TestEmptyHistoryLayout);
            Console.WriteLine($"{passed}/27 UI, migration, companion, and history tests passed.");
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
        snapshot.Remove("ModernWindowCollapsed");
        snapshot.Remove("ModernExpandedWidth");
        snapshot.Remove("ModernExpandedHeight");
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
            io.DisplaySize = new Vector2(2800, 2200);
            io.DeltaTime = 1f / 60f;
            io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard | ImGuiConfigFlags.NavEnableGamepad;
            io.BackendFlags |= ImGuiBackendFlags.HasGamepad;
            io.Fonts.AddFontDefault();
            var iconPath = Environment.GetEnvironmentVariable("DALAMUD_ICON_FONT")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "XIVLauncher", "dalamudAssets", "dev", "UIRes", "FontAwesomeFreeSolid.otf");
            ushort[] iconRanges = [0xF000, 0xF8FF, 0];
            fixed (ushort* range = iconRanges)
            {
                testIconFont = io.Fonts.AddFontFromFileTTF(iconPath, 16f, null, range);
                Check(io.Fonts.Build(), "Font atlas did not build.");
                testMotion = new SentinelModernMotion();
                try { action(); }
                finally { testMotion.Dispose(); }
            }
        }
        finally { ImGui.DestroyContext(context); }
    }

    private static (Vector2 Minimum, Vector2 Maximum, bool Focused) ToggleFrame(ref bool value, bool focus = false, bool disabled = false)
    {
        ImGui.NewFrame();
        testMotion.BeginFrame(ImGui.GetIO().DeltaTime, false);
        ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(520, 320), ImGuiCond.Always);
        ImGui.Begin("Toggle-test", ImGuiWindowFlags.NoSavedSettings);
        ImGui.BeginDisabled(disabled);
        SentinelModernSwitch.Draw("Test", "Enable hunting", ref value, testMotion, 1f);
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
        Check(!value, "The native switch activated before pointer release.");
        io.AddMouseButtonEvent(0, false);
        ToggleFrame(ref value);
        Check(value, "Pointer release did not activate the canonical switch.");
    });

    private static void TestNavigationToggle(ImGuiKey key) => InContext(() =>
    {
        var value = false;
        ToggleFrame(ref value, focus: true);
        var focused = ToggleFrame(ref value);
        Check(focused.Focused, "The canonical switch did not receive native navigation focus.");
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
        var row = ToggleFrame(ref value, disabled: true);
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
        using var shell = new SentinelModernAppShellState();
        foreach (var scale in new[] { 1f, 1.5f })
        foreach (var size in new[] { new Vector2(620, 520), new Vector2(700, 600), new Vector2(920, 720) })
        {
            var originalRounding = ImGui.GetStyle().WindowRounding;
            for (var frame = 0; frame < 3; frame++)
            {
                var navigationCalls = 0;
                var contentCalls = 0;
                scope.PushAppShell(scale);
                try
                {
                    ImGui.NewFrame();
                    ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always);
                    ImGui.SetNextWindowSize(size * scale, ImGuiCond.Always);
                    ImGui.Begin("Modern-shell-test", SentinelModernWindowChrome.UseCustomHeader(ImGuiWindowFlags.NoSavedSettings));
                    SentinelModernAppShell.Draw(
                        new SentinelModernAppShellOptions("Test", "S Rank Sentinel", "Main")
                        { Scale = scale, ReducedMotion = true, SurfaceStyle = SentinelModernAppSurfaceStyle.Unified,
                          AmbientIntensity = 0.9f, DrawPluginIcon = _ => { } },
                        shell,
                        [new SentinelModernNavItem("Main", null, "Main") { DrawIcon = _ => navigationCalls++ }],
                        _ => { },
                        () =>
                        {
                            contentCalls++;
                            using var card = SentinelModernGlassCard.Begin("Test-card", scale: scale);
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
        var header = FindWindow(parent, "##Modern2Header");
        var navigation = FindWindow(parent, "##Modern2Rail");
        var content = FindWindow(parent, "##Modern2Page");
        Check(MathF.Abs(header.Size.Y - 56f * scale) < 1f, "The compact Core header size changed.");
        Check((header.Flags & (ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)) ==
            (ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse) &&
            !header.ScrollbarX && !header.ScrollbarY, "Header scrollbars or mouse scrolling were enabled.");
        Check(navigation.Pos.X + navigation.Size.X <= content.Pos.X &&
            MathF.Abs(navigation.Pos.Y - content.Pos.Y) < 1f, "Navigation stacked above content or left the left pane.");
        Check(header.Pos.Y + header.Size.Y <= navigation.Pos.Y, "Navigation was inserted into the header.");
        Check(content.Size.X >= 320f * scale && MathF.Abs(navigation.Size.X - 64f * scale) < 1f,
            "Icon rail or right pane is unusable at the minimum window width.");
        Check((FindWindow(parent).Flags & ImGuiWindowFlags.NoTitleBar) != 0,
            "Modern drew a duplicate native title bar.");
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
        SetField(plugin, "modernShell", new SentinelModernAppShellState());
        SetField(plugin, "modernIconFont", (Func<ImFontPtr>)(() => testIconFont));
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
        var fonts = context.FontStack.Size;
        try { typeof(Plugin).GetMethod("DrawUi", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(plugin, null); }
        catch (TargetInvocationException exception) { ExceptionDispatchInfo.Capture(exception.InnerException!).Throw(); }
        Check(context.ColorStack.Size == colors && context.StyleVarStack.Size == styles &&
            context.CurrentWindowStack.Size == windows && context.FontStack.Size == fonts,
            "Consumer style, font, or Begin/End stack is unbalanced.");
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
            for (var frame = 0; frame < 20; frame++) ConsumerFrame(plugin);
            Check(ImGui.GetDrawData().TotalVtxCount > 0, "Consumer page did not render.");
            if (theme == 1)
            {
                CheckShellGeometry(Plugin.ConfigurationWindowId, 1f);
                var content = FindWindow(Plugin.ConfigurationWindowId, "##Modern2Page");
                Check(content.ContentSize.X <= content.Size.X + 1f,
                    $"{page} controls overflow the right pane at 620x520.");
            }
            Check(JsonNode.DeepEquals(snapshot, HuntSnapshot(config)), "Rendering a theme/page changed hunt configuration.");
        }
    });

    private static void TestSidebarThemeSwitch()
    {
        foreach (var page in Enum.GetValues<ConfigurationPage>())
        foreach (var key in new[] { ImGuiKey.None, ImGuiKey.Space, ImGuiKey.GamepadFaceDown })
            InContext(() =>
            {
                var config = new Configuration { WindowTheme = 1, WindowPage = (int)page };
                var pluginInterface = DispatchProxy.Create<IDalamudPluginInterface, RecordingPluginInterface>();
                var store = (RecordingPluginInterface)pluginInterface;
                config.Initialize(pluginInterface);
                var snapshot = HuntSnapshot(config);
                var savedBefore = store.SaveCalls;
                var plugin = CreateConsumer(config);
                LoadWindowPlacement(new Vector2(620, 520));
                Vector2 minimum = default, maximum = default;
                var focus = false;
                var focused = false;
                var classic = (Action)typeof(Plugin).GetMethod("DrawModernActionDock",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate(typeof(Action), plugin);
                SetField(plugin, "drawModernActionDock", (Action)(() =>
                {
                    if (focus) ImGui.SetKeyboardFocusHere();
                    classic();
                    minimum = ImGui.GetItemRectMin();
                    maximum = ImGui.GetItemRectMax();
                    focused = ImGui.IsItemFocused();
                }));
                for (var frame = 0; frame < 3; frame++) ConsumerFrame(plugin);
                var dock = FindWindow(Plugin.ConfigurationWindowId, "##Modern2Dock");
                var navigation = FindWindow(Plugin.ConfigurationWindowId, "##Modern2Rail");
                Check(minimum.X >= dock.Pos.X && maximum.X <= dock.Pos.X + dock.Size.X &&
                    minimum.Y >= navigation.Pos.Y + navigation.Size.Y && maximum.Y <= dock.Pos.Y + dock.Size.Y &&
                    minimum.X < dock.Pos.X + dock.Size.X / 2,
                    "The Classic button is clipped or is not separate at the bottom left at 620x520.");
                Check(maximum - minimum == new Vector2(170f, 34f), "The Classic switch expanded into a full-width bar.");
                var io = ImGui.GetIO();
                if (key == ImGuiKey.None)
                {
                    var center = (minimum + maximum) / 2f;
                    io.AddMousePosEvent(center.X, center.Y);
                    ConsumerFrame(plugin);
                    io.AddMouseButtonEvent(0, true);
                    ConsumerFrame(plugin);
                    Check(config.WindowTheme == 1, "Theme changed before the button release.");
                    io.AddMouseButtonEvent(0, false);
                    ConsumerFrame(plugin);
                }
                else
                {
                    focus = true;
                    ConsumerFrame(plugin);
                    focus = false;
                    ConsumerFrame(plugin);
                    Check(focused, "The sidebar Classic action did not receive native navigation focus.");
                    io.AddKeyEvent(key, true);
                    ConsumerFrame(plugin);
                    io.AddKeyEvent(key, false);
                    ConsumerFrame(plugin);
                }
                ConsumerFrame(plugin);
                Check(config.WindowTheme == 0 && config.WindowPage == (int)page,
                    $"{key} did not switch to Classic while retaining the selected page.");
                Check(store.SaveCalls == savedBefore + 1, "The theme action did not save exactly once.");
                var reloaded = JsonSerializer.Deserialize<Configuration>(store.SavedJson!)!;
                reloaded.MigrateWindowAppearance();
                Check(reloaded.WindowTheme == 0 && reloaded.WindowPage == (int)page,
                    "The sidebar theme choice did not persist.");
                Check(JsonNode.DeepEquals(snapshot, HuntSnapshot(config)) &&
                    JsonNode.DeepEquals(snapshot, HuntSnapshot(reloaded)), "Switching themes changed hunt settings.");
                var window = FindWindow(Plugin.ConfigurationWindowId);
                Check(window.Pos == new Vector2(145, 95) && window.Size == new Vector2(620, 520),
                    "The actual sidebar action reset saved window placement.");
            });
    }

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

    private static unsafe void TestWindowControls() => InContext(() =>
    {
        var config = new Configuration();
        var pluginInterface = DispatchProxy.Create<IDalamudPluginInterface, RecordingPluginInterface>();
        var store = (RecordingPluginInterface)pluginInterface;
        config.Initialize(pluginInterface);
        var plugin = CreateConsumer(config);
        LoadWindowPlacement(new Vector2(920, 720), collapsed: true);
        ConsumerFrame(plugin);
        var window = FindWindow(Plugin.ConfigurationWindowId);
        Check(window.Collapsed && (window.Flags & (ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoNav)) == 0,
            "Classic native collapse/title/navigation behavior regressed.");
        config.WindowTheme = 1;
        for (var frame = 0; frame < 3; frame++) ConsumerFrame(plugin);
        var snapshot = HuntSnapshot(config);
        var header = FindWindow(Plugin.ConfigurationWindowId, "##Modern2Header");
        var minimize = header.Pos + new Vector2(header.Size.X - 65f, header.Size.Y / 2f);
        ClickConsumer(plugin, minimize);
        ConsumerFrame(plugin);
        Check(config.ModernWindowCollapsed && !window.Collapsed && window.Size == new Vector2(920, 56),
            "Modern minimize did not leave its custom header visible.");
        Check(config.ModernExpandedHeight == 720 && config.ModernExpandedWidth == 920 &&
            window.Pos == new Vector2(145, 95), "Modern minimize lost expanded size or placement.");
        Check(JsonSerializer.Deserialize<Configuration>(store.SavedJson!)!.ModernWindowCollapsed,
            "Modern minimize state did not persist.");
        config = JsonSerializer.Deserialize<Configuration>(store.SavedJson!)!;
        config.Initialize(pluginInterface);
        plugin = CreateConsumer(config);
        for (var frame = 0; frame < 3; frame++) ConsumerFrame(plugin);
        Check(window.Size == new Vector2(920, 56) && window.Pos == new Vector2(145, 95),
            "Reloading a minimized Modern window lost its size or position.");
        // Focus the actual Core button captured by the persistence callback, then send real input events.
        foreach (var key in new[] { ImGuiKey.Space, ImGuiKey.GamepadFaceDown })
        {
            var io = ImGui.GetIO();
            io.AddMousePosEvent(-100, -100);
            ConsumerFrame(plugin);
            var ctx = ImGui.GetCurrentContext();
            ctx.NavWindow = header;
            ctx.NavId = store.LastSaveItemId;
            ctx.NavDisableHighlight = false;
            ctx.NavDisableMouseHover = true;
            ConsumerFrame(plugin);
            io.AddKeyEvent(key, true);
            ConsumerFrame(plugin);
            io.AddKeyEvent(key, false);
            ConsumerFrame(plugin);
            Check(!config.ModernWindowCollapsed && window.Size == new Vector2(920, 720),
                $"{key} did not restore the original expanded size.");
            CheckShellGeometry(Plugin.ConfigurationWindowId, 1f);
            ClickConsumer(plugin, minimize);
            ConsumerFrame(plugin);
            Check(config.ModernWindowCollapsed, "Core minimize stopped responding after navigation activation.");
        }
        ClickConsumer(plugin, minimize);
        ConsumerFrame(plugin);
        header = FindWindow(Plugin.ConfigurationWindowId, "##Modern2Header");
        foreach (var key in new[] { ImGuiKey.Space, ImGuiKey.GamepadFaceDown })
        {
            // The minimize save occurs immediately before Core submits the Close button.
            // Request native focus there, without adding a consumer activation overlay.
            store.FocusNextAfterSave = true;
            ClickConsumer(plugin, minimize);
            store.FocusNextAfterSave = false;
            ConsumerFrame(plugin);
            TapConsumerKey(plugin, key);
            Check(!(bool)typeof(Plugin).GetField("configOpen", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin)!,
                $"{key} did not activate the focused Core close button.");
            SetField(plugin, "configOpen", true);
            for (var frame = 0; frame < 3; frame++) ConsumerFrame(plugin);
        }
        ClickConsumer(plugin, header.Pos + new Vector2(header.Size.X - 29f, header.Size.Y / 2f));
        Check(!(bool)typeof(Plugin).GetField("configOpen", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin)!,
            "Core close action did not close the configuration window.");
        ConsumerFrame(plugin);
        Check(JsonNode.DeepEquals(snapshot, HuntSnapshot(config)), "Window controls changed hunt settings.");
    });

    private static void ClickConsumer(Plugin plugin, Vector2 position)
    {
        var io = ImGui.GetIO();
        io.AddMousePosEvent(position.X, position.Y);
        ConsumerFrame(plugin);
        io.AddMouseButtonEvent(0, true);
        ConsumerFrame(plugin);
        io.AddMouseButtonEvent(0, false);
        ConsumerFrame(plugin);
    }

    private static void TapConsumerKey(Plugin plugin, ImGuiKey key)
    {
        ImGui.GetIO().AddKeyEvent(key, true);
        ConsumerFrame(plugin);
        ImGui.GetIO().AddKeyEvent(key, false);
        ConsumerFrame(plugin);
    }

    private static unsafe void TestResponsiveConsumer()
    {
        foreach (var scale in new[] { 1f, 1.5f, 2f })
        foreach (var size in new[] { new Vector2(620, 520), new Vector2(800, 640), new Vector2(1040, 860) })
            InContext(() =>
            {
                ImGui.GetIO().FontGlobalScale = scale;
                Check(MathF.Abs(ImGuiHelpers.GlobalScale - scale) < 0.01f, "Dalamud UI scale fixture failed.");
                var config = new Configuration { WindowTheme = 1 };
                var snapshot = HuntSnapshot(config);
                var plugin = CreateConsumer(config);
                LoadWindowPlacement(size * scale);
                foreach (var page in Enum.GetValues<ConfigurationPage>())
                {
                    config.WindowPage = (int)page;
                    for (var frame = 0; frame < 20; frame++) ConsumerFrame(plugin);
                    CheckShellGeometry(Plugin.ConfigurationWindowId, scale);
                    var content = FindWindow(Plugin.ConfigurationWindowId, "##Modern2Page");
                    Check(content.ContentSize.X <= content.Size.X + 1f, $"{page} overflow at {size}/{scale}.");
                    var rows = 0;
                    var windows = ImGui.GetCurrentContext().Windows;
                    for (var index = 0; index < windows.Size; index++)
                    {
                        var row = windows[index];
                        var name = Marshal.PtrToStringUTF8((nint)row.Name)!;
                        if (!row.Active || !(name.Contains("Freshness") || name.Contains("Initial coordinate stop") ||
                            name.Contains("Safe parking clearance") || name.Contains("Emergency clearance") || name.Contains("Engage only")))
                            continue;
                        rows++;
                        // Check the actual native control extent against the Core row's measured text/control layout.
                        var layout = SentinelModernSettingsRowLayout.Resolve(row.Size.X, ImGui.GetTextLineHeight(), 0f,
                            ImGui.GetFrameHeight(), false, scale);
                        Check(row.DC.CursorMaxPos.X <= row.Pos.X + row.Size.X + 1f &&
                            row.DC.CursorMaxPos.Y <= row.Pos.Y + row.Size.Y + 1f,
                            $"A control escapes its responsive Core row: {name} at {size}/{scale}.");
                        Check(layout.ControlOffset.Y >= layout.TextOffset.Y + ImGui.GetTextLineHeight() ||
                            layout.ControlOffset.X >= layout.TextOffset.X + layout.TextWidth,
                            "Core placed the control over its label.");
                        Check(!row.ScrollbarX && !row.ScrollbarY, "A settings row acquired an internal scrollbar.");
                    }
                    Check(rows == (page == ConfigurationPage.Main ? 1 : page == ConfigurationPage.DistanceProfiles ? 8 : 0),
                        $"Missing responsive rows on {page}: {rows}.");
                    Check(JsonNode.DeepEquals(snapshot, HuntSnapshot(config)), "Scale/size changes modified configuration.");
                }
            });
    }

    private static void TestDragAndResize() => InContext(() =>
    {
        var config = new Configuration { WindowTheme = 1 };
        var plugin = CreateConsumer(config);
        LoadWindowPlacement(new Vector2(800, 640));
        for (var frame = 0; frame < 3; frame++) ConsumerFrame(plugin);
        var window = FindWindow(Plugin.ConfigurationWindowId);
        var header = FindWindow(Plugin.ConfigurationWindowId, "##Modern2Header");
        var start = header.Pos + new Vector2(header.Size.X - 150f, header.Size.Y / 2f);
        var io = ImGui.GetIO();
        io.AddMousePosEvent(start.X, start.Y);
        ConsumerFrame(plugin);
        io.AddMouseButtonEvent(0, true);
        ConsumerFrame(plugin);
        io.AddMousePosEvent(start.X + 47, start.Y + 26);
        ConsumerFrame(plugin);
        io.AddMouseButtonEvent(0, false);
        ConsumerFrame(plugin);
        Check(window.Pos == new Vector2(192, 121), "The Core header drag did not move the original top-level window.");
        var grip = window.Pos + window.Size - new Vector2(2f);
        io.AddMousePosEvent(grip.X, grip.Y);
        ConsumerFrame(plugin);
        io.AddMouseButtonEvent(0, true);
        ConsumerFrame(plugin);
        io.AddMousePosEvent(grip.X + 120f, grip.Y + 80f);
        ConsumerFrame(plugin);
        io.AddMouseButtonEvent(0, false);
        ConsumerFrame(plugin);
        Check(window.Size.X > 900f && window.Size.Y > 700f, "Native resize grip no longer resizes Modern.");
        Check(ImGui.SaveIniSettingsToMemory().Contains("Pos=192,121"), "Header dragging did not persist window position.");
    });

    private static void TestReducedMotion() => InContext(() =>
    {
        var config = new Configuration { WindowTheme = 1 };
        var pluginInterface = DispatchProxy.Create<IDalamudPluginInterface, RecordingPluginInterface>();
        ((RecordingPluginInterface)pluginInterface).ReducedMotion = true;
        var plugin = CreateConsumer(config);
        SetField(plugin, "pi", pluginInterface);
        LoadWindowPlacement(new Vector2(620, 520));
        var shell = (SentinelModernAppShellState)typeof(Plugin).GetField("modernShell", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin)!;
        foreach (var page in Enum.GetValues<ConfigurationPage>())
        {
            config.WindowPage = (int)page;
            for (var frame = 0; frame < 3; frame++) ConsumerFrame(plugin);
            Check(shell.Motion.ReducedMotion && shell.Motion.ElapsedSeconds == 0f && shell.PageRevealProgress == 1f,
                "Core did not honor Dalamud's reduced-motion preference.");
        }
    });
}
