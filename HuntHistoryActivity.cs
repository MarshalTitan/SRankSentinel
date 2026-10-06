using System.Numerics;
using Dalamud.Bindings.ImGui;
using SentinelCore.UI;

namespace SRankSentinel;

internal readonly record struct HuntHistoryActivityDay(DateTime Date, int Reported, int Tagged)
{
    internal int Untagged => Reported - Tagged;
}

/// <summary>Consumer hunt-data chart; Core supplies its enclosing card and all colours.</summary>
internal static class HuntHistoryActivity
{
    internal static HuntHistoryActivityDay[] BuildWeek(IEnumerable<HuntHistoryEntry> entries, DateTime today)
    {
        var first = today.Date.AddDays(-6);
        var days = Enumerable.Range(0, 7).Select(index => new HuntHistoryActivityDay(first.AddDays(index), 0, 0)).ToArray();
        foreach (var entry in entries)
        {
            // A later tag belongs to the report's day, as a subset of that day's received spawns.
            var index = (entry.ReportedAtUtc.ToLocalTime().Date - first).Days;
            if (index is < 0 or >= 7) continue;
            var day = days[index];
            days[index] = day with { Reported = day.Reported + 1, Tagged = day.Tagged + (entry.TaggedAtUtc is null ? 0 : 1) };
        }
        return days;
    }

    internal static void Draw(IReadOnlyList<HuntHistoryActivityDay> days, float scale)
    {
        ImGui.TextColored(SentinelModernPalette.Accent, "Untagged");
        ImGui.SameLine();
        ImGui.TextColored(SentinelModernPalette.Teal, "Tagged");
        if (!ImGui.BeginTable("##SpawnActivity", days.Count, ImGuiTableFlags.SizingStretchSame)) return;
        try
        {
            var maximum = Math.Max(1, days.Max(day => day.Reported));
            foreach (var day in days)
            {
                ImGui.TableNextColumn();
                ImGui.PushID(day.Date.ToString("yyyy-MM-dd"));
                try
                {
                    var size = new Vector2(MathF.Max(1f, ImGui.GetContentRegionAvail().X), 68f * scale);
                    ImGui.InvisibleButton("##ActivityDay", size);
                    var minimum = ImGui.GetItemRectMin();
                    var bottom = ImGui.GetItemRectMax();
                    var x = minimum.X + size.X * 0.15f;
                    var right = minimum.X + size.X * 0.85f;
                    var untaggedHeight = size.Y * day.Untagged / maximum;
                    var taggedHeight = size.Y * day.Tagged / maximum;
                    var drawList = ImGui.GetWindowDrawList();
                    // Stacked data segments only; no consumer card, control, or layout primitive.
                    if (day.Untagged > 0)
                        drawList.AddRectFilled(new(x, bottom.Y - untaggedHeight), new(right, bottom.Y),
                            ImGui.ColorConvertFloat4ToU32(SentinelModernPalette.Accent));
                    if (day.Tagged > 0)
                        drawList.AddRectFilled(new(x, bottom.Y - untaggedHeight - taggedHeight), new(right, bottom.Y - untaggedHeight),
                            ImGui.ColorConvertFloat4ToU32(SentinelModernPalette.Teal));
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip($"{day.Date:MMM d}: {day.Reported:N0} spawned / {day.Tagged:N0} tagged\n{day.Untagged:N0} untagged; tags grouped by report date.");
                    var label = day.Date.ToString("MMM d");
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (size.X - ImGui.CalcTextSize(label).X) / 2f));
                    ImGui.TextUnformatted(label);
                }
                finally { ImGui.PopID(); }
            }
        }
        finally { ImGui.EndTable(); }
    }
}
