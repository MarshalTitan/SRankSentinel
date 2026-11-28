using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using SentinelCore.UI;

namespace SRankSentinel;

public sealed partial class Plugin
{
    private bool UiSection(string id, string label, bool accent = true)
    {
        config.CollapsedUiSections ??= [];
        var expanded = !config.CollapsedUiSections.Contains(id);
        if (SentinelModernDisclosure.Draw(id, label, ref expanded, ImGuiHelpers.GlobalScale, accent))
        {
            if (expanded) config.CollapsedUiSections.Remove(id);
            else config.CollapsedUiSections.Add(id);
            config.Save();
        }
        return expanded;
    }

    private void DrawModernMainPage()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var width = ImGui.GetContentRegionAvail().X;
        var columns = SentinelModernSettingsRowLayout.ResolveColumns(width, scale);
        var freshness = SentinelModernSettingsRowLayout.Resolve(width,
            ImGui.CalcTextSize(WrapSettingsLabel("Queued-alert freshness (minutes)", columns.TextWidth)).Y,
            0f, ImGui.GetFrameHeight(), false, scale).Size.Y;
        var recoveryHeight = ImGui.GetTextLineHeight() + freshness
            + ActionButtonSize("Save settings").Y
            + ActionButtonSize("SKIP CURRENT + KEEP QUEUE").Y
            + ActionButtonSize("STOP + RESET THROUGH UL'DAH").Y
            + 9f * ImGui.GetStyle().ItemSpacing.Y + 8f * scale;
        var settingsHeight = MathF.Max(1f, ImGui.GetContentRegionAvail().Y - recoveryHeight);
        var visible = ImGui.BeginChild("##MainSettings", new Vector2(0f, settingsHeight));
        try
        {
            if (visible)
            {
                DrawEnabledControl();
                ImGui.Spacing();
                DrawHuntStatus();
                if (UiSection("Main.Expansions", "EXPANSION HUNTING")) DrawExpansionControls();
                if (UiSection("Main.DataCenters", "DATA CENTER HUNTING")) DrawDataCenterControls();
                if (UiSection("Main.Mounting", "MOUNTING")) DrawPreferredMount();
                if (UiSection("Main.Tagging", "TAGGING")) DrawTagCheckpointControls();
            }
        }
        finally { ImGui.EndChild(); }
        // This footer has no disclosure and is outside the scrolling settings region.
        SentinelModernUi.SectionHeader("RECOVERY");
        DrawRecoveryControls();
    }

    private static Vector2 ActionButtonSize(string label)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var width = MathF.Min(ImGui.GetContentRegionAvail().X,
            ImGui.CalcTextSize(label).X + 32f * scale);
        var wrapped = WrapSettingsLabel(label, MathF.Max(1f, width - 24f * scale));
        return new Vector2(width, MathF.Max(30f * scale,
            ImGui.CalcTextSize(wrapped).Y + 12f * scale));
    }
}

// Read-only presentation policy. Neither mode selection nor dependency state is changed here.
internal readonly record struct PluginModeReadiness(bool Movement, bool WorldTravel, bool Alerts,
    bool Solo, string? BossModReason, string? RotationReason, string RotationName)
{
    internal string? NormalReason => !Movement ? "vnavmesh navigation is not ready" :
        !WorldTravel ? "Lifestream World travel is not ready" :
        !Alerts ? "Enable HuntAlerts or Sonar announcements" : null;
    internal string? CombatReason => NormalReason ?? (!Solo ? "Available only while solo" :
        BossModReason ?? RotationReason);
    internal string NormalLabel => NormalReason is { } reason ? "Normal Tagging: NOT READY - " + reason : "Normal Tagging: READY";
    internal string CombatLabel => CombatReason is { } reason ? "Full Combat Assist: NOT READY - " + reason :
        "Full Combat Assist: READY - BossMod + " + RotationName;
}
