using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using SentinelCore.UI;

namespace SRankSentinel;

public sealed partial class Plugin
{
    // Keep the exact original top-level identity so ImGui retains saved position/collapse state.
    internal const string ConfigurationWindowId = "S Rank Sentinel###SRankSentinel";
    private static readonly string[] WindowThemes = ["Classic", "Sentinel Modern"];
    private readonly SentinelModernStyleScope modernStyle = new();
    private readonly SentinelModernAppShellState modernShell = new();
    private readonly Func<ImFontPtr> modernIconFont = static () => UiBuilder.IconFont;
    private SentinelModernNavItem[]? modernNavigation;
    private Action<string>? selectModernPage;
    private Action? drawModernContent;
    private Vector2 modernFrameWindowSize;
    private Vector2? pendingModernSize;
    private bool modernUiFrame;

    private void DrawUi()
    {
        if (!configOpen)
            return;

        modernUiFrame = config.WindowTheme == (int)SentinelThemeKind.Modern;
        var scale = ImGuiHelpers.GlobalScale;
        if (modernUiFrame)
            modernStyle.PushAppShell(scale);
        try
        {
            if (modernUiFrame)
            {
                var headerHeight = SentinelModernAppLayoutOptions.Default.HeaderHeight;
                ImGui.SetNextWindowSizeConstraints(new Vector2(620f,
                        config.ModernWindowCollapsed ? headerHeight : 520f) * scale,
                    new Vector2(float.MaxValue, config.ModernWindowCollapsed ? headerHeight * scale : float.MaxValue));
            }
            if (pendingModernSize is { } requestedSize)
            {
                ImGui.SetNextWindowSize(requestedSize, ImGuiCond.Always);
                pendingModernSize = null;
            }
            else
                ImGui.SetNextWindowSize(new Vector2(680, 720), ImGuiCond.FirstUseEver);
            var flags = modernUiFrame
                ? SentinelModernWindowChrome.UseCustomHeader(ImGuiWindowFlags.None)
                : ImGuiWindowFlags.None;
            var visible = ImGui.Begin(ConfigurationWindowId, ref configOpen, flags);
            try
            {
                if (!visible || ImGui.IsWindowCollapsed())
                    return;
                if (modernUiFrame)
                {
                    modernFrameWindowSize = ImGui.GetWindowSize();
                    modernNavigation ??= CreateModernNavigation();
                    selectModernPage ??= SelectModernPage;
                    drawModernContent ??= DrawModernContent;
                    SentinelModernAppShell.Draw(
                        new SentinelModernAppShellOptions("SRankSentinel", "S Rank Sentinel",
                            ((ConfigurationPage)config.WindowPage).ToString())
                        {
                            Scale = scale,
                            DeltaTime = ImGui.GetIO().DeltaTime,
                            ReducedMotion = pi?.UiBuilder.ShouldUseReducedMotion ?? false,
                            ContextLabel = config.WindowPage == (int)ConfigurationPage.DistanceProfiles ? "Distance Profiles" : "Main",
                            Status = new SentinelModernStatusPillOptions(config.Enabled ? "ENABLED" : "DISABLED",
                                config.Enabled ? SentinelModernPillTone.Enabled : SentinelModernPillTone.Neutral),
                            DrawPluginIcon = context => DrawModernIcon(FontAwesomeIcon.Crosshairs, context.DrawList,
                                context.Minimum, context.Maximum, ImGui.GetColorU32(ImGuiCol.Text)),
                            SurfaceStyle = SentinelModernAppSurfaceStyle.Unified,
                            AmbientIntensity = 0.9f,
                            EnableWindowDragging = true,
                            RequestClose = () => configOpen = false,
                            RequestCollapse = ToggleModernCollapse,
                            CollapseTooltip = config.ModernWindowCollapsed ? "Expand" : "Minimize",
                        }, modernShell, modernNavigation, selectModernPage, drawModernContent);
                }
                else
                {
                    DrawClassicUi();
                }
            }
            finally
            {
                ImGui.End();
            }
        }
        finally
        {
            if (modernUiFrame)
                modernStyle.Pop();
        }
    }

    private void DrawClassicUi()
    {
        DrawEnabledControl();
        ImGui.Separator();
        DrawHuntStatus();
        ImGui.Separator();
        ImGui.TextUnformatted("EXPANSION HUNTING");
        DrawExpansionControls();
        ImGui.Separator();
        ImGui.TextUnformatted("DISTANCE PROFILES");
        DrawDistanceControls();
        DrawRecoveryControls();
        ImGui.Separator();
        DrawAppearanceControls();
    }

    private SentinelModernNavItem[] CreateModernNavigation() =>
    [
        CreateModernNavItem(ConfigurationPage.Main.ToString(), "Main", FontAwesomeIcon.Crosshairs),
        CreateModernNavItem(ConfigurationPage.DistanceProfiles.ToString(), "Distance Profiles", FontAwesomeIcon.RulerHorizontal),
        CreateModernNavItem("Classic", "Switch to Classic", FontAwesomeIcon.Palette),
    ];

    private SentinelModernNavItem CreateModernNavItem(string id, string label, FontAwesomeIcon icon) =>
        new(id, null, label)
        {
            DrawIcon = context =>
            {
                DrawModernIcon(icon, context.DrawList, context.Minimum, context.Maximum,
                    ImGui.ColorConvertFloat4ToU32(context.Colour));
                if (ImGui.IsItemFocused() && !ImGui.IsItemHovered())
                    ImGui.SetTooltip(label);
            },
        };

    private void DrawModernIcon(FontAwesomeIcon icon, ImDrawListPtr drawList, Vector2 minimum, Vector2 maximum, uint colour)
    {
        ImGui.PushFont(modernIconFont());
        try
        {
            var glyph = icon.ToIconString();
            drawList.AddText(minimum + ((maximum - minimum - ImGui.CalcTextSize(glyph)) * 0.5f), colour, glyph);
        }
        finally { ImGui.PopFont(); }
    }

    private void SelectModernPage(string id)
    {
        if (id == "Classic")
            SetWindowTheme((int)SentinelThemeKind.Classic);
        else if (Enum.TryParse<ConfigurationPage>(id, out var page) && Enum.IsDefined(page))
        {
            config.WindowPage = (int)page;
            config.Save();
        }
    }

    private void DrawModernContent()
    {
        if (config.ModernWindowCollapsed)
            return;
        var page = (ConfigurationPage)config.WindowPage;

        switch (page)
        {
            case ConfigurationPage.DistanceProfiles:
                DrawDistanceControls();
                if (DrawActionButton("Save settings"))
                    config.Save();
                break;
            default:
                DrawEnabledControl();
                ImGui.Spacing();
                DrawHuntStatus();
                SentinelModernUi.SectionHeader("EXPANSION HUNTING");
                DrawExpansionControls();
                SentinelModernUi.SectionHeader("RECOVERY");
                DrawRecoveryControls();
                break;
        }
    }

    private bool DrawBoolean(string label, ref bool value) => modernUiFrame
        ? SentinelModernSwitch.Draw(label, label, ref value, modernShell.Motion, ImGuiHelpers.GlobalScale)
        : ImGui.Checkbox(label, ref value);

    private bool DrawActionButton(string label, bool danger = false) => modernUiFrame
        ? danger ? SentinelModernActionDock.DangerButton(label, label, default, ImGuiHelpers.GlobalScale)
                 : SentinelModernActionDock.PrimaryButton(label, label, default, ImGuiHelpers.GlobalScale)
        : ImGui.Button(label);

    private void ToggleModernCollapse()
    {
        var scale = ImGuiHelpers.GlobalScale;
        if (config.ModernWindowCollapsed)
            pendingModernSize = new Vector2(modernFrameWindowSize.X, MathF.Max(520f, config.ModernExpandedHeight) * scale);
        else
        {
            config.ModernExpandedWidth = modernFrameWindowSize.X / scale;
            config.ModernExpandedHeight = modernFrameWindowSize.Y / scale;
            pendingModernSize = new Vector2(modernFrameWindowSize.X,
                SentinelModernAppLayoutOptions.Default.HeaderHeight * scale);
        }
        config.ModernWindowCollapsed = !config.ModernWindowCollapsed;
        config.Save();
    }

    private void DrawEnabledControl()
    {
        var enabled = config.Enabled;
        if (DrawBoolean("Enabled", ref enabled))
        {
            config.Enabled = enabled;
            if (!enabled)
            {
                vnav.StopSafe();
                faloop.Stop("Sentinel disabled");
            }
            config.Save();
        }
    }

    private void DrawHuntStatus()
    {
        ImGui.TextUnformatted($"State: {state}");
        ImGui.TextWrapped($"Status: {status}");
        if (current is not null)
            ImGui.TextWrapped($"Current: {current.CreatureName} | {current.World} | territory {current.TerritoryId} | instance {current.Instance}");
        if (pendingAlerts.Count > 0)
            ImGui.TextWrapped($"Queued: {pendingAlerts.Count} | Next: {pendingAlerts.Peek().CreatureName}");
    }

    private void DrawExpansionControls()
    {
        var expansionChanged = false;
        var centurio = config.EnableCenturio;
        if (DrawBoolean("Centurio (ARR / HW / SB)", ref centurio))
        {
            config.EnableCenturio = centurio;
            expansionChanged = true;
        }
        var shadowbringers = config.EnableShadowbringers;
        if (DrawBoolean("Shadowbringers", ref shadowbringers))
        {
            config.EnableShadowbringers = shadowbringers;
            expansionChanged = true;
        }
        var endwalker = config.EnableEndwalker;
        if (DrawBoolean("Endwalker", ref endwalker))
        {
            config.EnableEndwalker = endwalker;
            expansionChanged = true;
        }
        var dawntrail = config.EnableDawntrail;
        if (DrawBoolean("Dawntrail", ref dawntrail))
        {
            config.EnableDawntrail = dawntrail;
            expansionChanged = true;
        }
        var evercold = false;
        ImGui.BeginDisabled();
        DrawBoolean("Evercold", ref evercold);
        ImGui.EndDisabled();
        if (modernUiFrame)
            ImGui.TextWrapped("Future placeholder — hunt data not available");
        else
        {
            ImGui.SameLine();
            ImGui.TextUnformatted("Future placeholder — hunt data not available");
        }
        if (expansionChanged)
        {
            RemoveDisabledQueuedAlerts();
            config.Save();
        }
    }

    private void DrawDistanceControls()
    {
        if (!modernUiFrame)
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
    }

    private void DrawRecoveryControls()
    {
        var freshnessMinutes = config.AlertFreshnessMinutes;
        const string freshnessLabel = "Queued-alert freshness (minutes)";
        var freshnessChanged = false;
        if (modernUiFrame)
        {
            SentinelModernSettingsRow.Draw("Freshness", freshnessLabel, null,
                () => freshnessChanged = ImGui.InputInt("##Value", ref freshnessMinutes), scale: ImGuiHelpers.GlobalScale);
        }
        else
            freshnessChanged = ImGui.InputInt(freshnessLabel, ref freshnessMinutes);
        if (freshnessChanged)
            config.AlertFreshnessMinutes = Math.Clamp(freshnessMinutes, 10, 180);

        if (DrawActionButton("Save settings"))
            config.Save();
        if (!modernUiFrame)
            ImGui.SameLine();
        ImGui.BeginDisabled(current is null);
        if (DrawActionButton("SKIP CURRENT + KEEP QUEUE", danger: true))
            FailCurrent("Current hunt skipped manually", HuntExitRequestSource.ManualSkip);
        ImGui.EndDisabled();
        if (!modernUiFrame)
            ImGui.SameLine();
        if (DrawActionButton("STOP + RESET THROUGH UL'DAH", danger: true))
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
    }

    private void DrawAppearanceControls()
    {
        var selected = config.WindowTheme;
        if (ImGui.Combo("Window theme", ref selected, WindowThemes, WindowThemes.Length))
            SetWindowTheme(selected);
        ImGui.TextWrapped("Classic uses the compact one-page layout. Sentinel Modern groups the same settings into pages.");
    }

    private void SetWindowTheme(int selected)
    {
        config.WindowTheme = (int)SentinelThemeState<ConfigurationPage>.NormalizeTheme(selected);
        if (modernUiFrame && config.WindowTheme == (int)SentinelThemeKind.Classic && config.ModernWindowCollapsed)
            pendingModernSize = new Vector2(modernFrameWindowSize.X, MathF.Max(520f, config.ModernExpandedHeight) * ImGuiHelpers.GlobalScale);
        config.Save();
    }

    private float DrawFloat(string label, float value, float min, float max, string format = "%.0f y")
    {
        if (modernUiFrame)
        {
            var changed = false;
            SentinelModernSettingsRow.Draw(label, label.Split("##", 2)[0], null,
                () => changed = ImGui.SliderFloat("##Value", ref value, min, max, format),
                scale: ImGuiHelpers.GlobalScale);
            return changed ? MathF.Round(value) : value;
        }
        return ImGui.SliderFloat(label, ref value, min, max, format)
            ? MathF.Round(value)
            : value;
    }

    private void DrawDistanceProfile(
        string heading,
        string id,
        HuntDistanceProfile profile,
        float flagMinimum,
        float safeMinimum,
        float emergencyMinimum)
    {
        if (modernUiFrame)
        {
            var scale = ImGuiHelpers.GlobalScale;
            var width = ImGui.GetContentRegionAvail().X - (28f * scale);
            var row = SentinelModernSettingsRowLayout.Resolve(width, ImGui.GetTextLineHeight(), 0f,
                ImGui.GetFrameHeight(), false, scale);
            var headingSize = ImGui.CalcTextSize(heading, false, width);
            var height = headingSize.Y + (4f * row.Size.Y) + (5f * ImGui.GetStyle().ItemSpacing.Y) + (24f * scale);
            using var card = SentinelModernGlassCard.Begin(id + "-profile",
                new SentinelModernGlassCardOptions { Size = new Vector2(0f, height / scale) }, scale);
            if (card.IsVisible)
            {
                ImGui.TextWrapped(heading);
                DrawDistanceProfileValues(id, profile, flagMinimum, safeMinimum, emergencyMinimum);
            }
            return;
        }
        ImGui.Spacing();
        ImGui.TextUnformatted(heading);
        DrawDistanceProfileValues(id, profile, flagMinimum, safeMinimum, emergencyMinimum);
    }

    private void DrawDistanceProfileValues(string id, HuntDistanceProfile profile,
        float flagMinimum, float safeMinimum, float emergencyMinimum)
    {
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
}

internal enum ConfigurationPage
{
    Main = 0,
    // Keep the persisted Distance Profiles ID. Former Hunting (1), Recovery Controls (3),
    // and Appearance (4) selections normalize to Main through the existing config migration.
    DistanceProfiles = 2,
}
