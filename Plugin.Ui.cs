using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using SentinelCore.UI;

namespace SRankSentinel;

public sealed partial class Plugin
{
    // Keep the exact original top-level identity so ImGui retains saved position/collapse state.
    internal const string ConfigurationWindowId = "S Rank Sentinel###SRankSentinel";
    private static readonly string[] WindowThemes = ["Classic", "Sentinel Modern"];
    private readonly SentinelModernStyleScope modernStyle = new();
    private Action? drawModernNavigation;
    private Action? drawModernContent;
    private bool modernUiFrame;

    private void DrawUi()
    {
        if (!configOpen)
            return;

        modernUiFrame = config.WindowTheme == (int)SentinelThemeKind.Modern;
        var scale = ImGuiHelpers.GlobalScale;
        if (modernUiFrame)
            modernStyle.Push(scale);
        try
        {
            // The shared shell keeps navigation on the left at supported window sizes.
            if (modernUiFrame)
                ImGui.SetNextWindowSizeConstraints(new Vector2(620f, 520f) * scale,
                    new Vector2(float.MaxValue, float.MaxValue));
            ImGui.SetNextWindowSize(new Vector2(680, 720), ImGuiCond.FirstUseEver);
            var visible = ImGui.Begin(ConfigurationWindowId, ref configOpen);
            try
            {
                if (!visible || ImGui.IsWindowCollapsed())
                    return;
                if (modernUiFrame)
                {
                    drawModernNavigation ??= DrawModernNavigation;
                    drawModernContent ??= DrawModernContent;
                    SentinelModernConfigurationShell.Draw(
                        new SentinelModernShellOptions(
                            "SRankSentinel", "MARSHALTITAN  /  SENTINEL", "S RANK SENTINEL",
                            "Travel, parking, one ranged tag, and recovery")
                        {
                            Scale = scale,
                            ContextLabel = "Sentinel Modern",
                            Status = new SentinelModernStatus(config.Enabled ? "ENABLED" : "DISABLED",
                                config.Enabled ? SentinelModernStatusTone.Success : SentinelModernStatusTone.Neutral),
                        }, drawModernNavigation, drawModernContent);
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
        DrawOverview();
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

    private void DrawModernNavigation()
    {
        SentinelModernNavigation.GroupLabel("HUNTS");
        DrawNavigationItem(ConfigurationPage.Overview, "Overview");
        DrawNavigationItem(ConfigurationPage.Hunting, "Hunting");
        DrawNavigationItem(ConfigurationPage.DistanceProfiles, "Distance Profiles");
        DrawNavigationItem(ConfigurationPage.RecoveryControls, "Recovery Controls");
        ImGui.Spacing();
        SentinelModernNavigation.GroupLabel("SETTINGS");
        DrawNavigationItem(ConfigurationPage.Appearance, "Appearance");
    }

    private void DrawNavigationItem(ConfigurationPage page, string label)
    {
        if (SentinelModernNavigation.Item(page.ToString(), label,
                config.WindowPage == (int)page, ImGuiHelpers.GlobalScale))
        {
            config.WindowPage = (int)page;
            config.Save();
        }
    }

    private void DrawModernContent()
    {
        var page = (ConfigurationPage)config.WindowPage;
        var (title, description) = page switch
        {
            ConfigurationPage.Hunting => ("Hunting", "Enable Sentinel and choose expansions."),
            ConfigurationPage.DistanceProfiles => ("Distance Profiles", "Clearance and engagement preferences."),
            ConfigurationPage.RecoveryControls => ("Recovery Controls", "Save settings, skip a hunt, or reset through Ul'dah."),
            ConfigurationPage.Appearance => ("Appearance", "Choose the configuration window presentation."),
            _ => ("Overview", "Current hunt, queue, and orchestrator status."),
        };
        SentinelModernUi.PageHeading(title, description);
        ImGui.Spacing();
        using var card = SentinelModernCard.Begin("SRankSentinel-Page");
        if (!card.IsVisible)
            return;

        switch (page)
        {
            case ConfigurationPage.Hunting:
                SentinelModernUi.SectionHeader("HUNTING");
                DrawEnabledControl();
                ImGui.Spacing();
                DrawExpansionControls();
                break;
            case ConfigurationPage.DistanceProfiles:
                SentinelModernUi.SectionHeader("DISTANCE PROFILES");
                DrawDistanceControls();
                if (ImGui.Button("Save settings"))
                    config.Save();
                break;
            case ConfigurationPage.RecoveryControls:
                SentinelModernUi.SectionHeader("RECOVERY");
                DrawRecoveryControls();
                break;
            case ConfigurationPage.Appearance:
                SentinelModernUi.SectionHeader("APPEARANCE");
                DrawAppearanceControls();
                break;
            default:
                DrawEnabledControl();
                ImGui.Spacing();
                DrawOverview();
                break;
        }
    }

    private bool DrawBoolean(string label, ref bool value) => modernUiFrame
        ? ConsumerToggleActivation.Draw(label, label, ref value, ImGuiHelpers.GlobalScale)
        : ImGui.Checkbox(label, ref value);

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

    private void DrawOverview()
    {
        ImGui.TextUnformatted("STANDALONE S-RANK ORCHESTRATOR");
        ImGui.TextWrapped("HuntAlerts and Sonar supply alerts. Sentinel handles World Visit, teleport, safe movement, one gated ranged tag, SS watch, and recovery.");
        ImGui.Spacing();
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
        var freshnessLabel = "Queued-alert freshness (minutes)";
        if (modernUiFrame)
        {
            ImGui.TextWrapped(freshnessLabel);
            ImGui.SetNextItemWidth(-1f);
            freshnessLabel = "##" + freshnessLabel;
        }
        if (ImGui.InputInt(freshnessLabel, ref freshnessMinutes))
            config.AlertFreshnessMinutes = Math.Clamp(freshnessMinutes, 10, 180);

        if (ImGui.Button("Save settings"))
            config.Save();
        if (!modernUiFrame)
            ImGui.SameLine();
        ImGui.BeginDisabled(current is null);
        if (ImGui.Button("SKIP CURRENT + KEEP QUEUE"))
            FailCurrent("Current hunt skipped manually", HuntExitRequestSource.ManualSkip);
        ImGui.EndDisabled();
        if (!modernUiFrame)
            ImGui.SameLine();
        if (ImGui.Button("STOP + RESET THROUGH UL'DAH"))
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
        var themeLabel = "Window theme";
        if (modernUiFrame)
        {
            ImGui.TextWrapped(themeLabel);
            ImGui.SetNextItemWidth(-1f);
            themeLabel = "##" + themeLabel;
        }
        if (ImGui.Combo(themeLabel, ref selected, WindowThemes, WindowThemes.Length))
        {
            config.WindowTheme = (int)SentinelThemeState<ConfigurationPage>.NormalizeTheme(selected);
            config.Save();
        }
        ImGui.TextWrapped("Classic uses the compact one-page layout. Sentinel Modern groups the same settings into pages.");
    }

    private float DrawFloat(string label, float value, float min, float max, string format = "%.0f y")
    {
        if (modernUiFrame)
        {
            ImGui.TextWrapped(label.Split("##", 2)[0]);
            ImGui.SetNextItemWidth(-1f);
            label = "##" + label;
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
        ImGui.Spacing();
        if (modernUiFrame)
            ImGui.TextWrapped(heading);
        else
            ImGui.TextUnformatted(heading);
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
    Overview,
    Hunting,
    DistanceProfiles,
    RecoveryControls,
    Appearance,
}
