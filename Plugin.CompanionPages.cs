using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Plugin;
using SentinelCore.UI;
using Lumina.Excel.Sheets;

namespace SRankSentinel;

public sealed partial class Plugin
{
    private Task? companionEnableTask;
    private string? companionEnableName;
    private string? companionEnableStatus;

    private void DrawPluginsPage()
    {
        if (companionEnableTask is { IsCompleted: true } completed)
        {
            companionEnableStatus = completed.IsCompletedSuccessfully
                ? $"{companionEnableName} enabled."
                : $"Could not enable {companionEnableName}: {CompanionPluginActivation.Detail(completed.Exception!.GetBaseException())}";
            companionEnableTask = null;
        }
        var installed = pi?.InstalledPlugins?.ToArray() ?? [];
        var movementReady = CompanionPlugins.Find(installed, CompanionPlugins.All[0])?.IsLoaded == true;
        var alertsReady = CompanionPlugins.All.Skip(2).Any(companion => CompanionPlugins.Find(installed, companion)?.IsLoaded == true);
        ImGui.TextColored(SentinelModernStatusPill.ResolveColour(new("Readiness",
            movementReady && alertsReady ? SentinelModernPillTone.Enabled : SentinelModernPillTone.Warning)),
            movementReady && alertsReady ? "Movement and S-rank alerts are ready." :
                !movementReady ? "Enable vnavmesh for automatic movement." : "Enable HuntAlerts or Sonar for S-rank alerts.");
        ImGui.Spacing();
        foreach (var companion in CompanionPlugins.All)
        {
            var plugin = CompanionPlugins.Find(installed, companion);
            DrawCompanionCard(companion, plugin);
            ImGui.Spacing();
        }
        if (CompanionButton("Installer", "Open Plugin Installer", 180f))
            pi?.OpenPluginInstallerTo();
        ImGui.TextWrapped("Lifestream is optional. HuntAlerts and Sonar are alternative alert sources.");
        ImGui.TextWrapped("Use Installer to install or update a companion. Enable S-rank announcements and map links for your data center.");
        if (companionEnableStatus is not null) ImGui.TextWrapped(companionEnableStatus);
    }

    private void DrawCompanionCard(CompanionPlugin companion, IExposedPlugin? plugin)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var loaded = plugin?.IsLoaded == true;
        var colour = SentinelModernStatusPill.ResolveColour(new("Plugin", loaded ? SentinelModernPillTone.Enabled : SentinelModernPillTone.Error));
        var requirement = new SentinelModernStatusPillOptions(
            companion.Name == "vnavmesh" ? "REQUIRED" : companion.Name == "Lifestream" ? "OPTIONAL" : "ALERT PROVIDER",
            companion.Name == "Lifestream" ? SentinelModernPillTone.Neutral : SentinelModernPillTone.Accent);
        var controlWidth = (loaded && !plugin!.HasConfigUi ? 110f : 182f) * scale;
        // Consumer content uses native table columns; Core still owns the card, tint, pills, and controls.
        var textWidth = MathF.Max(1f, ImGui.GetContentRegionAvail().X - (24f + 28f) * scale
            - controlWidth - 6f * ImGui.GetStyle().CellPadding.X);
        var badgeSize = SentinelModernStatusPill.Measure(requirement, scale);
        var inlineBadge = ImGui.CalcTextSize(companion.Name).X + ImGui.GetStyle().ItemSpacing.X + badgeSize.X <= textWidth;
        var titleHeight = inlineBadge ? MathF.Max(ImGui.GetTextLineHeight(), badgeSize.Y)
            : ImGui.GetTextLineHeightWithSpacing() + badgeSize.Y;
        var contentHeight = titleHeight + ImGui.GetStyle().ItemSpacing.Y + ImGui.CalcTextSize(companion.Setup, false, textWidth).Y;
        var height = MathF.Max(30f * scale, contentHeight) + 20f * scale + 2f * ImGui.GetStyle().CellPadding.Y;
        using var card = SentinelModernGlassCard.Begin("Companion." + companion.Name,
            new SentinelModernGlassCardOptions
            {
                Size = new Vector2(0f, height / scale), Padding = new Vector2(12f, 10f),
                Accent = colour, AccentStrength = 0.65f,
            }, scale);
        if (!card.IsVisible || !ImGui.BeginTable("##CompanionContent", 3, ImGuiTableFlags.SizingStretchProp)) return;
        try
        {
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 28f * scale);
            ImGui.TableSetupColumn("Plugin", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, controlWidth);
            ImGui.TableNextColumn();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + MathF.Max(0f, (contentHeight - 26f * scale) / 2f));
            var glyph = (loaded ? FontAwesomeIcon.CheckCircle : FontAwesomeIcon.TimesCircle).ToIconString();
            ImGui.PushFont(modernIconFont());
            try
            {
                ImGui.GetWindowDrawList().AddText(ImGui.GetFont(), 26f * scale, ImGui.GetCursorScreenPos(),
                    ImGui.ColorConvertFloat4ToU32(colour), glyph);
            }
            finally { ImGui.PopFont(); }
            ImGui.Dummy(new Vector2(26f) * scale);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(companion.Name);
            if (inlineBadge) ImGui.SameLine();
            SentinelModernStatusPill.Draw(requirement, modernShell.Motion, scale);
            ImGui.PushTextWrapPos(0f);
            try { SentinelModernActionDock.Status(companion.Setup); }
            finally { ImGui.PopTextWrapPos(); }
            ImGui.TableNextColumn();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + MathF.Max(0f, (contentHeight - 30f * scale) / 2f));
            DrawCompanionActions(companion, plugin);
        }
        finally { ImGui.EndTable(); }
    }

    private void DrawCompanionActions(CompanionPlugin companion, IExposedPlugin? plugin)
    {
        if (plugin?.IsLoaded == true)
        {
            SentinelModernStatusPill.Draw(new("ENABLED", SentinelModernPillTone.Enabled), modernShell.Motion, ImGuiHelpers.GlobalScale);
            if (plugin.HasConfigUi)
            {
                ImGui.SameLine();
                if (CompanionButton(companion.Name + ".Settings", "Settings", 78f))
                {
                    try { plugin.OpenConfigUi(); }
                    catch (Exception exception) { companionEnableStatus = $"Could not open {companion.Name} settings: {exception.Message}"; }
                }
            }
            return;
        }
        if (plugin is null)
        {
            SentinelModernStatusPill.Draw(new("MISSING", SentinelModernPillTone.Warning), modernShell.Motion, ImGuiHelpers.GlobalScale);
            ImGui.SameLine();
        }
        else
        {
            var reason = CompanionPluginActivation.UnavailableReason(plugin);
            ImGui.BeginDisabled(reason is not null || companionEnableTask is not null);
            try
            {
                if (CompanionButton(companion.Name + ".Enable", "Enable", 78f))
                {
                    companionEnableName = companion.Name;
                    companionEnableStatus = $"Enabling {companion.Name}...";
                    companionEnableTask = Task.Run(() => CompanionPluginActivation.EnableAsync(plugin));
                }
            }
            finally { ImGui.EndDisabled(); }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(reason ?? "Enable this installed plugin.");
            ImGui.SameLine();
        }
        if (CompanionButton(companion.Name + ".Installer", "Installer", 78f))
            pi?.OpenPluginInstallerTo(searchText: companion.Name);
    }

    private static bool CompanionButton(string id, string label, float width) =>
        SentinelModernActionDock.PrimaryButton("Companion." + id, label,
            new Vector2(width, 30f) * ImGuiHelpers.GlobalScale, ImGuiHelpers.GlobalScale);

    private void DrawHistoryPage()
    {
        var reports = History.VisibleReports();
        var today = DateTime.Now.Date;
        if (ImGui.BeginTable("##HistoryMetrics", 4, ImGuiTableFlags.SizingStretchSame))
        {
            try
            {
                DrawHistoryMetric("Reports", reports.Length);
                DrawHistoryMetric("Today", reports.Count(entry => entry.ReportedAtUtc.ToLocalTime().Date == today));
                DrawHistoryMetric("Tagged", reports.Count(entry => entry.TaggedAtUtc is not null));
                DrawHistoryMetric("Credits", reports.Count(entry => entry.CreditedAtUtc is not null));
            }
            finally { ImGui.EndTable(); }
        }
        ImGui.Spacing();
        DrawSpawnActivity(reports, today);
        ImGui.Spacing();
        DrawHistorySection(reports);
        var history = History.Snapshot();
        DrawClearHistoryButton(history.Spawns.Length + history.Credits.Length);
        ImGui.TextWrapped("Only enabled hunt expansions are shown and new reports recorded. Totals cover up to 500 saved reports; no earlier hunts are backfilled.");
    }

    private void DrawClearHistoryButton(int count)
    {
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f,
            ImGui.GetContentRegionAvail().X - 180f * ImGuiHelpers.GlobalScale));
        ImGui.BeginDisabled(count == 0);
        try
        {
            if (SentinelModernActionDock.DangerButton("History.Clear", "Clear history", new Vector2(180f, 30f) * ImGuiHelpers.GlobalScale,
                    ImGuiHelpers.GlobalScale))
                ObserveHistory(History.ClearAll);
        }
        finally { ImGui.EndDisabled(); }
    }

    private static void DrawHistoryMetric(string label, int count)
    {
        ImGui.TableNextColumn();
        using var card = SentinelModernGlassCard.Begin("History.Metric." + label,
            new SentinelModernGlassCardOptions { Size = new Vector2(0f, 68f) }, ImGuiHelpers.GlobalScale);
        if (!card.IsVisible) return;
        SentinelModernActionDock.Status(label.ToUpperInvariant());
        ImGui.TextUnformatted(count.ToString("N0"));
    }

    private static void DrawSpawnActivity(IReadOnlyList<HuntHistoryEntry> entries, DateTime today)
    {
        var days = HuntHistoryActivity.BuildWeek(entries, today);
        using var card = SentinelModernGlassCard.Begin("History.Activity",
            new SentinelModernGlassCardOptions { Size = new Vector2(0f, 176f) }, ImGuiHelpers.GlobalScale);
        if (!card.IsVisible) return;
        ImGui.TextUnformatted("Spawned / tagged · last 7 days");
        if (days.Sum(day => day.Reported) == 0)
        {
            ImGui.TextWrapped("No reports in this period for your enabled hunt expansions.");
            return;
        }
        HuntHistoryActivity.Draw(days, ImGuiHelpers.GlobalScale);
        SentinelModernActionDock.Status($"{days.Sum(day => day.Reported):N0} reports · {days.Sum(day => day.Tagged):N0} tagged");
    }

    private void DrawHistorySection(IReadOnlyList<HuntHistoryEntry> entries)
    {
        SentinelModernUi.SectionHeader($"SPAWN REPORTS ({entries.Count})");
        if (entries.Count == 0)
        {
            SentinelModernSettingsRow.Draw("History.EmptySpawns", "Waiting for your first report",
                "New alerts for enabled hunt expansions appear here while Sentinel is enabled.",
                () => SentinelModernStatusPill.Draw(new("EMPTY"), modernShell.Motion, ImGuiHelpers.GlobalScale),
                96f, ImGuiHelpers.GlobalScale);
            return;
        }
        // Keep the bounded activity list scrollable without hundreds of outer rows.
        var height = 180f * ImGuiHelpers.GlobalScale;
        var visible = ImGui.BeginChild("##SpawnHistory", new(0, height));
        try
        {
            if (!visible) return;
            foreach (var entry in entries)
            {
                var timestamp = entry.ReportedAtUtc;
                var zone = data?.GetExcelSheet<TerritoryType>()?.GetRowOrDefault(entry.TerritoryId)?.PlaceName.Value.Name.ExtractText();
                var outcome = entry.CreditedAtUtc is not null ? "CREDIT" : entry.TaggedAtUtc is not null ? "TAGGED"
                    : entry.KilledAtUtc is not null ? "KILLED" : "REPORTED";
                var detail =
                    $"{entry.Source} · {(entry.CreditedAtUtc is not null ? "Credit confirmed" : entry.KilledAtUtc is not null
                        ? entry.TaggedAtUtc is null ? "Killed; no tag recorded" : "Tagged; credit unconfirmed"
                        : entry.TaggedAtUtc is not null ? "Tag confirmed" : "Spawn reported")}";
                if (entry.CreditedAtUtc is not null) detail += $" · Reward: {entry.Reward}";
                SentinelModernSettingsRow.Draw("History.Spawn." + entry.Id,
                    $"{entry.Rank} · {entry.CreatureName}",
                    $"{timestamp.ToLocalTime():MMM d, HH:mm} · {entry.World} · {zone ?? $"Territory {entry.TerritoryId}"} · instance {entry.Instance}\n{detail}",
                    () => SentinelModernStatusPill.Draw(new(outcome,
                        entry.CreditedAtUtc is not null ? SentinelModernPillTone.Enabled : SentinelModernPillTone.Neutral),
                        modernShell.Motion, ImGuiHelpers.GlobalScale), 128f, ImGuiHelpers.GlobalScale);
                ImGui.Spacing();
            }
        }
        finally { ImGui.EndChild(); }
    }
}
