using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Lumina.Excel.Sheets;

namespace SRankSentinel;

public sealed partial class Plugin
{
    private HuntHistoryJournal? historyJournal;
    private string[]? historyCurrencyNames;
    private HuntHistoryJournal History => historyJournal ??= new(config);

    private void ObserveHistorySpawn(string? huntType, string? world, string? creature, uint territory,
        int instance, string source, DateTime? occurredAtUtc)
    {
        ObserveHistory(() =>
        {
            if (string.IsNullOrWhiteSpace(creature)) return false;
            var definition = HuntCatalog.ResolveStrict(territory, creature);
            var ss = HuntCatalog.IsAnySsName(creature);
            if (definition is null || !ss && !string.Equals(huntType, "srank", StringComparison.OrdinalIgnoreCase))
                return false;
            var resolvedWorld = string.IsNullOrWhiteSpace(world) ? travel.CurrentWorld : world.Trim();
            var reported = occurredAtUtc ?? DateTime.UtcNow;
            if (!travel.IsSameDataCenter(resolvedWorld) || !IsWithinFreshnessWindow(reported, DateTime.UtcNow))
                return false;
            return History.Spawn(new(ss ? "ssrank" : "srank", resolvedWorld, definition.Name,
                territory, definition.DataId, definition.PreferredAetheryteId, Math.Max(1, instance),
                0, 0, reported), source);
        });
    }

    private void ObserveHistoryTag(HuntAlertSnapshot? alert, DateTime now) =>
        ObserveHistory(() => alert is not null && History.Tag(alert, now));

    private void ObserveHistoryKill(HuntAlertSnapshot alert, bool taggedFinalPull, DateTime now) =>
        ObserveHistory(() => History.Kill(alert, taggedFinalPull, now));

    private void OnHistoryReward(IHandleableChatMessage message)
    {
        // Chat reports from Sonar, players, echo, or a generic defeat line are not personal credit.
        if (message.LogKind != XivChatType.SystemMessage || !string.IsNullOrWhiteSpace(message.OriginalSender.ExtractText()))
            return;
        ObserveHistory(() =>
        {
            historyCurrencyNames ??= GetHistoryCurrencyNames();
            var reward = HuntRewardReceipt.Match(message.OriginalMessage.ExtractText(), historyCurrencyNames);
            if (reward is null) return false;
            if (current is not null && !killConfirmed)
            {
                if (!pullCycleTagged || !markEverIdentified || !markCombatObserved ||
                    DateTime.UtcNow - lastMarkSeenUtc > TimeSpan.FromSeconds(15)) return false;
                var visible = FindMark();
                if (visible is not null && !visible.IsDead && visible.CurrentHp > 0) return false;
            }
            return History.Reward(travel.CurrentWorld, clientState.TerritoryType, travel.CurrentInstance,
                current, pullCycleTagged, DateTime.UtcNow, reward);
        });
    }

    private string[] GetHistoryCurrencyNames()
    {
        var names = new List<string> { "Allied Seal", "Allied Seals", "Centurio Seal", "Centurio Seals",
            "Sack of Nuts", "Sacks of Nuts" };
        var items = data.GetExcelSheet<Item>();
        foreach (var id in new uint[] { 27, 10307, 26533 })
            if (items.TryGetRow(id, out var item))
            {
                names.Add(item.Name.ExtractText());
                names.Add(item.Plural.ExtractText());
            }
        return names.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void ObserveHistory(Func<bool> observation)
    {
        try
        {
            if (observation()) config.Save();
        }
        catch (Exception exception)
        {
            // A history/config-write failure must never interrupt the existing hunt handler.
            log?.Warning("Could not save passive hunt history: {Error}", exception.Message);
        }
    }
}

internal static class HuntRewardReceipt
{
    internal static string? Match(string text, IEnumerable<string> currencyNames)
    {
        // Only positive acquisition wording, including the supported game-client languages.
        if (!(text.Contains("obtain", StringComparison.OrdinalIgnoreCase) ||
              text.Contains("receive", StringComparison.OrdinalIgnoreCase) ||
              text.Contains("earn", StringComparison.OrdinalIgnoreCase) ||
              text.Contains("erhält", StringComparison.OrdinalIgnoreCase) ||
              text.Contains("erhalten", StringComparison.OrdinalIgnoreCase) ||
              text.Contains("obten", StringComparison.OrdinalIgnoreCase) || text.Contains("入手"))) return null;
        if (text.Contains("cannot", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("unable", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("could not", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("nicht", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ne pouvez", StringComparison.OrdinalIgnoreCase) || text.Contains("入手でき")) return null;
        if (!System.Text.RegularExpressions.Regex.IsMatch(text, @"(?<![0-9０-９])[1-9１-９][0-9０-９]*(?![0-9０-９])")) return null;
        return currencyNames.OrderByDescending(name => name.Length)
            .FirstOrDefault(name => text.Contains(name, StringComparison.OrdinalIgnoreCase));
    }
}
