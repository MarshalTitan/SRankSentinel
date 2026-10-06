using System.Numerics;
using Dalamud.Bindings.ImGui;
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
            var requirement = companion.Name == "vnavmesh" ? "REQUIRED" : companion.Name == "Lifestream" ? "OPTIONAL" : "ALERT PROVIDER";
            var controlWidth = plugin?.IsLoaded == true && !plugin.HasConfigUi ? 110f : 182f;
            // Core owns the entire responsive card: wrapped text and the compact action column never overlap.
            SentinelModernSettingsRow.Draw("Companion." + companion.Name, $"{companion.Name} · {requirement}",
                companion.Setup, () =>
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
            }, SentinelModernSettingsRowLayoutOptions.Default with
            {
                PreferredControlWidth = controlWidth, MinimumControlWidth = controlWidth,
                MinimumTextWidth = 160f, MinimumHeight = 68f,
            }, ImGuiHelpers.GlobalScale);
            ImGui.Spacing();
        }
        if (CompanionButton("Installer", "Open Plugin Installer", 180f))
            pi?.OpenPluginInstallerTo();
        ImGui.TextWrapped("Lifestream is optional. HuntAlerts and Sonar are alternative alert sources.");
        ImGui.TextWrapped("Use Installer to install or update a companion. Enable S-rank announcements and map links for your data center.");
        if (companionEnableStatus is not null) ImGui.TextWrapped(companionEnableStatus);
    }

    private static bool CompanionButton(string id, string label, float width) =>
        SentinelModernActionDock.PrimaryButton("Companion." + id, label,
            new Vector2(width, 30f) * ImGuiHelpers.GlobalScale, ImGuiHelpers.GlobalScale);

    private void DrawHistoryPage()
    {
        var history = History.Snapshot();
        var today = DateTime.Now.Date;
        if (ImGui.BeginTable("##HistoryMetrics", 4, ImGuiTableFlags.SizingStretchSame))
        {
            try
            {
                DrawHistoryMetric("Reports", history.Spawns.Length);
                DrawHistoryMetric("Today", history.Spawns.Count(entry => entry.ReportedAtUtc.ToLocalTime().Date == today));
                DrawHistoryMetric("Tagged", history.Spawns.Count(entry => entry.TaggedAtUtc is not null));
                DrawHistoryMetric("Credits", history.Credits.Length);
            }
            finally { ImGui.EndTable(); }
        }
        ImGui.Spacing();
        DrawSpawnActivity(history.Spawns, today);
        ImGui.Spacing();
        DrawHistorySection("SPAWN REPORTS", history.Spawns, false);
        ImGui.Spacing();
        DrawHistorySection("TAGGED + CREDIT CONFIRMED", history.Credits, true);
        ImGui.TextWrapped("Totals cover saved history (up to 500 reports and 500 credits). No earlier hunts are backfilled.");
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
        var days = new float[7];
        foreach (var entry in entries)
        {
            var day = (entry.ReportedAtUtc.ToLocalTime().Date - today.AddDays(-6)).Days;
            if (day is >= 0 and < 7) days[day]++;
        }
        using var card = SentinelModernGlassCard.Begin("History.Activity",
            new SentinelModernGlassCardOptions { Size = new Vector2(0f, 144f) }, ImGuiHelpers.GlobalScale);
        if (!card.IsVisible) return;
        ImGui.TextUnformatted("Spawn reports · last 7 days");
        if (days.Sum() == 0)
        {
            ImGui.TextWrapped("No reports in this period. New S-rank alerts will appear here.");
            return;
        }
        ImGui.PlotHistogram("##SpawnActivity", days, 0, "", 0f,
            MathF.Max(1f, days.Max()), new Vector2(0f, 72f) * ImGuiHelpers.GlobalScale);
        SentinelModernActionDock.Status($"{today.AddDays(-6):MMM d} – {today:MMM d} · {days.Sum():N0} reports received");
    }

    private void DrawHistorySection(string title, IReadOnlyList<HuntHistoryEntry> entries, bool credited)
    {
        SentinelModernUi.SectionHeader($"{title} ({entries.Count})");
        if (entries.Count == 0)
        {
            SentinelModernSettingsRow.Draw(credited ? "History.EmptyCredits" : "History.EmptySpawns", "Waiting for your first " + (credited ? "credit" : "report"),
                credited ? "A confirmed tag, kill, and game hunt reward are required."
                    : "History starts with new alerts received while Sentinel is enabled.",
                () => SentinelModernStatusPill.Draw(new("EMPTY"), modernShell.Motion, ImGuiHelpers.GlobalScale),
                96f, ImGuiHelpers.GlobalScale);
            return;
        }
        // Bounded, independently scrolling sections keep both histories reachable without hundreds of outer rows.
        var height = 180f * ImGuiHelpers.GlobalScale;
        var visible = ImGui.BeginChild(credited ? "##CreditedHistory" : "##SpawnHistory", new(0, height));
        try
        {
            if (!visible) return;
            foreach (var entry in entries)
            {
                var timestamp = credited ? entry.CreditedAtUtc!.Value : entry.ReportedAtUtc;
                var zone = data?.GetExcelSheet<TerritoryType>()?.GetRowOrDefault(entry.TerritoryId)?.PlaceName.Value.Name.ExtractText();
                var outcome = entry.CreditedAtUtc is not null ? "CREDIT" : entry.TaggedAtUtc is not null ? "TAGGED"
                    : entry.KilledAtUtc is not null ? "KILLED" : "REPORTED";
                var detail = credited ? $"Reward: {entry.Reward}" :
                    $"{entry.Source} · {(entry.CreditedAtUtc is not null ? "Credit confirmed" : entry.KilledAtUtc is not null
                        ? entry.TaggedAtUtc is null ? "Killed; no tag recorded" : "Tagged; credit unconfirmed"
                        : entry.TaggedAtUtc is not null ? "Tag confirmed" : "Spawn reported")}";
                SentinelModernSettingsRow.Draw((credited ? "History.Credit." : "History.Spawn.") + entry.Id,
                    $"{entry.Rank} · {entry.CreatureName}",
                    $"{timestamp.ToLocalTime():MMM d, HH:mm} · {entry.World} · {zone ?? $"Territory {entry.TerritoryId}"} · instance {entry.Instance}\n{detail}",
                    () => SentinelModernStatusPill.Draw(new(credited ? "CONFIRMED" : outcome,
                        credited || entry.CreditedAtUtc is not null ? SentinelModernPillTone.Enabled : SentinelModernPillTone.Neutral),
                        modernShell.Motion, ImGuiHelpers.GlobalScale), 128f, ImGuiHelpers.GlobalScale);
                ImGui.Spacing();
            }
        }
        finally { ImGui.EndChild(); }
    }
}
