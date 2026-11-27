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
        var movementReady = CompanionPlugins.Find(installed, CompanionPlugins.All[0])?.IsLoaded == true
            && vnav?.IsReadySafe() == true;
        var worldTravelReady = lifestream?.HasRequiredIpc == true;
        var alertsReady = CompanionPlugins.All.Any(companion => companion.AlertProvider &&
            CompanionPlugins.Find(installed, companion)?.IsLoaded == true &&
            (companion.Name == "HuntAlerts" ? config.EnableHuntAlertsFallback : config.EnableSonarFallback));
        RefreshCombatCardDetails(installed);
        var rotationName = config.RotationProvider == CombatRotationProvider.RotationSolverReborn
            ? "Rotation Solver Reborn" : "Wrath Combo";
        var boss = CompanionPlugins.Find(installed, CompanionPlugins.All[4]);
        var selectedRotation = CompanionPlugins.All.First(c => c.Name == rotationName);
        var solo = false;
        try { solo = SoloAssistAllowed; }
        catch { /* Services may not yet be available; readiness remains conservative. */ }
        var readiness = new PluginModeReadiness(movementReady, worldTravelReady, alertsReady, solo,
            boss?.IsLoaded == true ? combatCardDetails?.GetValueOrDefault("BossMod") switch {
                "Ready for the exact active mark" => null, var detail => detail ?? "Checking BossMod readiness" }
                : "BossMod is not enabled",
            CompanionPlugins.Find(installed, selectedRotation)?.IsLoaded == true
                ? combatCardDetails?.GetValueOrDefault(rotationName) switch {
                    "Rotation ready for this job" => null, var detail => detail ?? "Checking rotation readiness" }
                : rotationName + " is not enabled", rotationName);
        var selectedReason = config.FullCombatAssist ? readiness.CombatReason : readiness.NormalReason;
        ImGui.PushTextWrapPos(0f);
        ImGui.TextColored(SentinelModernStatusPill.ResolveColour(new("Readiness",
            selectedReason is null ? SentinelModernPillTone.Enabled : SentinelModernPillTone.Warning)),
            config.FullCombatAssist ? readiness.CombatLabel : readiness.NormalLabel);
        ImGui.PopTextWrapPos();
        ImGui.Spacing();
        if (UiSection("Plugins.Normal", "NORMAL TAGGING"))
        {
            ImGui.TextWrapped("vnavmesh supplies movement; Lifestream supplies automated World travel. Keep at least one functioning HuntAlerts or Sonar announcement provider enabled. Combat plugins are not required.");
            foreach (var companion in CompanionPlugins.All.Where(c => c.CombatRole is null))
            {
                DrawCompanionCard(companion, CompanionPlugins.Find(installed, companion));
                ImGui.Spacing();
            }
        }
        if (UiSection("Plugins.Combat", "FULL COMBAT ASSIST"))
        {
            ImGui.TextWrapped("Uses the four normal integrations above, BossMod AI and one selected rotation provider: Wrath Combo OR Rotation Solver Reborn. Do not enable both rotations. Expanding this section does not enable combat assist.");
            ImGui.TextWrapped(config.FullCombatAssist ? readiness.CombatLabel : "Optional mode is off. " + readiness.CombatLabel);
            ImGui.TextWrapped("The current RSR handoff remains unavailable until its IPC can verify job readiness and exclusive rotation control. SRank keeps safe tagging when a selected provider cannot pass its checks.");
            foreach (var companion in CompanionPlugins.All.Where(c => c.CombatRole is not null))
            {
                DrawCompanionCard(companion, CompanionPlugins.Find(installed, companion));
                ImGui.Spacing();
            }
        }
        if (CompanionButton("Installer", "Open Plugin Installer", 180f))
            pi?.OpenPluginInstallerTo();
        ImGui.TextWrapped("Use Installer to install or update a companion. Enable S-rank announcements and map links for your data center.");
        if (companionEnableStatus is not null) ImGui.TextWrapped(companionEnableStatus);
    }

    private Dictionary<string, string>? combatCardDetails;
    private DateTime combatCardRefresh;

    // The Plugins page only reads public readiness signals; rendering it cannot acquire a lease.
    private void RefreshCombatCardDetails(IExposedPlugin[] installed)
    {
        if (DateTime.UtcNow < combatCardRefresh) return;
        combatCardRefresh = DateTime.UtcNow.AddSeconds(1);
        combatCardDetails ??= new();
        combatCardDetails.Clear();
        foreach (var companion in CompanionPlugins.All.Where(c => c.CombatRole is not null &&
            CompanionPlugins.Find(installed, c)?.IsLoaded == true))
        {
            try
            {
                combatCardDetails[companion.Name] = companion.Name switch
                {
                    "Wrath Combo" => RotationAdapters.Wrath.RotationUnavailable() ?? "Rotation ready for this job",
                    "Rotation Solver Reborn" => RotationAdapters.Reborn.Unavailable(default),
                    _ => BossModCombatReadiness.Unavailable(pi, ActiveEncounterOid()) ?? "Ready for the exact active mark",
                };
            }
            catch { combatCardDetails[companion.Name] = "Readiness unavailable"; }
        }
    }

    private uint ActiveEncounterOid() => current is not null && !killConfirmed && identifiedMarkGameObjectId != 0 &&
        objects.SearchById(identifiedMarkGameObjectId) is Dalamud.Game.ClientState.Objects.Types.IBattleChara target &&
        IsExactCheckpointMark(target) ? target.BaseId : 0;

    private void DrawCompanionCard(CompanionPlugin companion, IExposedPlugin? plugin)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var loaded = plugin?.IsLoaded == true;
        var optional = companion.CombatRole is not null && !config.FullCombatAssist;
        var colour = SentinelModernStatusPill.ResolveColour(new("Plugin", loaded ? SentinelModernPillTone.Enabled
            : optional || companion.AlertProvider ? SentinelModernPillTone.Neutral : SentinelModernPillTone.Error));
        var requirement = new SentinelModernStatusPillOptions(
            companion.CombatRole is { } role ? "OPTIONAL · " + role : companion.Required ? "REQUIRED" : "ALERT PROVIDER",
            SentinelModernPillTone.Accent);
        var description = companion.Setup;
        if (companion.CombatRole is not null && plugin is not null)
            description += "\n" + (loaded ? combatCardDetails?.GetValueOrDefault(companion.Name) ?? "Checking readiness" : "Installed but disabled");
        var controlWidth = (loaded && !plugin!.HasConfigUi ? 110f : 182f) * scale;
        // Consumer content uses native table columns; Core still owns the card, tint, pills, and controls.
        var available = ImGui.GetContentRegionAvail().X - (24f + 28f) * scale;
        var stackActions = available - controlWidth - 6f * ImGui.GetStyle().CellPadding.X < 180f * scale;
        var textWidth = MathF.Max(1f, available - (stackActions
            ? 4f * ImGui.GetStyle().CellPadding.X : controlWidth + 6f * ImGui.GetStyle().CellPadding.X));
        var badgeSize = SentinelModernStatusPill.Measure(requirement, scale);
        var inlineBadge = ImGui.CalcTextSize(companion.Name).X + ImGui.GetStyle().ItemSpacing.X + badgeSize.X <= textWidth;
        var titleHeight = inlineBadge ? MathF.Max(ImGui.GetTextLineHeight(), badgeSize.Y)
            : ImGui.GetTextLineHeightWithSpacing() + badgeSize.Y;
        var contentHeight = titleHeight + ImGui.GetStyle().ItemSpacing.Y + ImGui.CalcTextSize(description, false, textWidth).Y;
        var height = MathF.Max(30f * scale, contentHeight) + 20f * scale + 2f * ImGui.GetStyle().CellPadding.Y
            + (stackActions ? 30f * scale + ImGui.GetStyle().ItemSpacing.Y : 0f);
        using var card = SentinelModernGlassCard.Begin("Companion." + companion.Name,
            new SentinelModernGlassCardOptions
            {
                Size = new Vector2(0f, height / scale), Padding = new Vector2(12f, 10f),
                Accent = colour, AccentStrength = 0.65f,
            }, scale);
        if (!card.IsVisible || !ImGui.BeginTable("##CompanionContent", stackActions ? 2 : 3, ImGuiTableFlags.SizingStretchProp)) return;
        try
        {
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 28f * scale);
            ImGui.TableSetupColumn("Plugin", ImGuiTableColumnFlags.WidthStretch);
            if (!stackActions) ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, controlWidth);
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
            try { SentinelModernActionDock.Status(description); }
            finally { ImGui.PopTextWrapPos(); }
            if (!stackActions)
            {
                ImGui.TableNextColumn();
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + MathF.Max(0f, (contentHeight - 30f * scale) / 2f));
                DrawCompanionActions(companion, plugin);
            }
        }
        finally { ImGui.EndTable(); }
        if (stackActions) DrawCompanionActions(companion, plugin);
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
        var scale = ImGuiHelpers.GlobalScale;
        // Keep the destructive action outside the scrolling body. Short windows can scroll
        // the summary/chart while tall windows give all remaining body space to reports.
        var bodyHeight = MathF.Max(1f, ImGui.GetContentRegionAvail().Y
            - 30f * scale - ImGui.GetStyle().ItemSpacing.Y);
        var visible = ImGui.BeginChild("##HistoryBody", new Vector2(0f, bodyHeight));
        try
        {
            if (visible)
            {
                var metricColumns = ImGui.GetContentRegionAvail().X < 400f * scale ? 2 : 4;
                if (ImGui.BeginTable("##HistoryMetrics", metricColumns, ImGuiTableFlags.SizingStretchSame))
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
                ImGui.TextWrapped("Only enabled hunt expansions are shown and new reports recorded. Totals cover up to 500 saved reports; no earlier hunts are backfilled.");
                ImGui.Spacing();
                DrawHistorySection(reports);
            }
        }
        finally { ImGui.EndChild(); }
        var history = History.Snapshot();
        DrawClearHistoryButton(history.Spawns.Length + history.Credits.Length);
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
        ImGui.TextWrapped("Spawned / tagged · last 7 days");
        if (days.Sum(day => day.Reported) == 0)
        {
            ImGui.TextWrapped("No reports in this period for your enabled hunt expansions.");
            return;
        }
        var scale = ImGuiHelpers.GlobalScale;
        var labelBottom = HuntHistoryActivity.Draw(days, scale);
        var cursor = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(new(cursor.X, Math.Max(labelBottom + scale, cursor.Y - 10f * scale)));
        SentinelModernActionDock.Status(HuntHistoryActivity.Footer(days));
    }

    private void DrawHistorySection(IReadOnlyList<HuntHistoryEntry> entries)
    {
        if (!UiSection("History.Spawns", $"SPAWN REPORTS ({entries.Count})")) return;
        if (entries.Count == 0)
        {
            DrawSettingsRow("History.EmptySpawns", "Waiting for your first report",
                "New alerts for enabled hunt expansions appear here while Sentinel is enabled.",
                () => SentinelModernStatusPill.Draw(new("EMPTY"), modernShell.Motion, ImGuiHelpers.GlobalScale),
                96f, ImGuiHelpers.GlobalScale);
            return;
        }
        // Fill the viewport after the summary and chart, retaining a scrollable minimum
        // when the short body itself needs scrolling. There is no upper height cap.
        var height = MathF.Max(120f * ImGuiHelpers.GlobalScale,
            ImGui.GetContentRegionAvail().Y - ImGui.GetScrollY());
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
                DrawSettingsRow("History.Spawn." + entry.Id,
                    $"{entry.Rank} · {entry.CreatureName}",
                    $"{timestamp.ToLocalTime():MMM d, HH:mm} · {entry.World} · {zone ?? $"Territory {entry.TerritoryId}"}{entry.InstanceLabel}\n{detail}",
                    () => SentinelModernStatusPill.Draw(new(outcome,
                        entry.CreditedAtUtc is not null ? SentinelModernPillTone.Enabled : SentinelModernPillTone.Neutral),
                        modernShell.Motion, ImGuiHelpers.GlobalScale), 128f, ImGuiHelpers.GlobalScale);
                ImGui.Spacing();
            }
        }
        finally { ImGui.EndChild(); }
    }
}
