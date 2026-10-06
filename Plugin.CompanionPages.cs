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
        foreach (var companion in CompanionPlugins.All)
        {
            var plugin = CompanionPlugins.Find(installed, companion);
            SentinelModernUi.SectionHeader(companion.Name);
            ImGui.TextWrapped(companion.Role);
            SentinelModernStatusPill.Draw(new(plugin is null ? "NOT INSTALLED" : plugin.IsLoaded ? "ENABLED" : "DISABLED",
                plugin?.IsLoaded == true ? SentinelModernPillTone.Enabled : SentinelModernPillTone.Neutral),
                modernShell.Motion, ImGuiHelpers.GlobalScale);
            ImGui.TextWrapped(companion.Setup);
            if (plugin is { IsLoaded: false })
            {
                var reason = CompanionPluginActivation.UnavailableReason(plugin);
                ImGui.BeginDisabled(reason is not null || companionEnableTask is not null);
                try
                {
                    if (DrawActionButton($"Enable {companion.Name}"))
                    {
                        companionEnableName = companion.Name;
                        companionEnableStatus = $"Enabling {companion.Name}...";
                        companionEnableTask = Task.Run(() => CompanionPluginActivation.EnableAsync(plugin));
                    }
                }
                finally { ImGui.EndDisabled(); }
                if (reason is not null) ImGui.TextWrapped(reason);
            }
            if (DrawActionButton($"Open installer##{companion.Name}"))
                pi?.OpenPluginInstallerTo(searchText: companion.Name);
            if (plugin is { IsLoaded: true, HasConfigUi: true } && DrawActionButton($"Settings##{companion.Name}"))
            {
                try { plugin.OpenConfigUi(); }
                catch (Exception exception) { companionEnableStatus = $"Could not open {companion.Name} settings: {exception.Message}"; }
            }
            ImGui.Spacing();
        }
        if (companionEnableStatus is not null) ImGui.TextWrapped(companionEnableStatus);
    }

    private void DrawHistoryPage()
    {
        var history = History.Snapshot();
        DrawHistorySection("SPAWN REPORTS", history.Spawns, false);
        ImGui.Spacing();
        DrawHistorySection("TAGGED + CREDIT CONFIRMED", history.Credits, true);
    }

    private void DrawHistorySection(string title, IReadOnlyList<HuntHistoryEntry> entries, bool credited)
    {
        SentinelModernUi.SectionHeader($"{title} ({entries.Count})");
        if (entries.Count == 0)
        {
            ImGui.TextWrapped(credited ? "No credited tags recorded yet. A confirmed tag, kill, and game hunt reward are required."
                : "No spawn reports recorded yet. History starts with alerts received while Sentinel is enabled.");
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
                ImGui.TextWrapped($"{timestamp.ToLocalTime():yyyy-MM-dd HH:mm} | {entry.Rank} | {entry.CreatureName}");
                var zone = data?.GetExcelSheet<TerritoryType>()?.GetRowOrDefault(entry.TerritoryId)?.PlaceName.Value.Name.ExtractText();
                ImGui.TextWrapped($"{entry.World} | {zone ?? $"Territory {entry.TerritoryId}"} | instance {entry.Instance}");
                ImGui.TextWrapped(credited ? $"Reward: {entry.Reward}" :
                    $"{entry.Source} | {(entry.CreditedAtUtc is not null ? "Credit confirmed" : entry.KilledAtUtc is not null
                        ? entry.TaggedAtUtc is null ? "Killed; no tag recorded" : "Tagged; credit unconfirmed"
                        : entry.TaggedAtUtc is not null ? "Tag confirmed" : "Spawn reported")}");
                ImGui.Separator();
            }
        }
        finally { ImGui.EndChild(); }
    }
}
