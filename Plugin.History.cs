using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Lumina.Excel.Sheets;

namespace SRankSentinel;

public sealed partial class Plugin
{
    private HuntHistoryJournal? historyJournal;
    private string[]? historyCurrencyNames;
    private string? historyContributionText;
    private HuntHistoryJournal History => historyJournal ??= new(config, IsHuntDataCenterEnabled);

    private void ObserveHistorySpawn(string? huntType, string? world, string? creature, uint territory,
        int instance, string source, DateTime? occurredAtUtc)
    {
        ObserveHistory(() =>
        {
            if (!config.Enabled || !config.IsExpansionEnabled(HuntCatalog.GetExpansion(territory)) ||
                string.IsNullOrWhiteSpace(creature)) return false;
            var definition = HuntCatalog.ResolveStrict(territory, creature);
            var ss = HuntCatalog.IsAnySsName(creature);
            if (definition is null || !ss && !string.Equals(huntType, "srank", StringComparison.OrdinalIgnoreCase))
                return false;
            var resolvedWorld = string.IsNullOrWhiteSpace(world) ? travel.CurrentWorld : world.Trim();
            var reported = occurredAtUtc ?? DateTime.UtcNow;
            if (!travel.IsWorldVisitCompatible(resolvedWorld) || !IsHuntDataCenterEnabled(resolvedWorld) ||
                !IsWithinFreshnessWindow(reported, DateTime.UtcNow))
                return false;
            bool? multiple = clientState.TerritoryType == territory &&
                travel.CurrentWorld.Equals(resolvedWorld, StringComparison.OrdinalIgnoreCase) &&
                travel.TryReadLiveInstance(out var liveInstance) ? liveInstance > 0 : null;
            return History.Spawn(CanonicalizeObservedInstance(new(ss ? "ssrank" : "srank", resolvedWorld, definition.Name,
                territory, definition.DataId, definition.PreferredAetheryteId, Math.Max(1, instance),
                0, 0, reported)), source, multiple);
        });
    }

    private void ObserveKnownHistoryDeaths(string world, uint territory, int instance, DateTime when,
        Func<string, bool> matchesName, bool requireContextForMissing = false)
    {
        if (string.IsNullOrWhiteSpace(world) || !IsWithinFreshnessWindow(when, DateTime.UtcNow))
            return;
        // Active kills go through ConfirmKill and its visible-live-entity veto.
        ObserveHistory(() => History.MarkKnownDeaths(world, territory, instance, when, matchesName,
            entry => IsObservedSingleInstance(entry.World, entry.TerritoryId), current?.Key,
            entry => !requireContextForMissing || (territory != 0 && instance > 0) ||
                (clientState.TerritoryType == entry.TerritoryId &&
                 travel.CurrentWorld.Equals(entry.World, StringComparison.OrdinalIgnoreCase) &&
                 (travel.CurrentInstance <= 0 || travel.CurrentInstance == entry.Instance))));
    }

    private void ObserveHistoryTag(HuntAlertSnapshot? alert, DateTime now, bool firstTagOfPull) =>
        ObserveHistory(() => alert is not null && History.Tag(alert, now, firstTagOfPull));

    private void ObserveHistoryKill(HuntAlertSnapshot alert, bool taggedFinalPull, DateTime now) =>
        ObserveHistory(() => History.Kill(alert, taggedFinalPull, now));

    private void OnHistoryContribution(ILogMessage message)
    {
        var reward = HuntRewardReceipt.MatchNative(message.LogMessageId);
        if (reward is null) return;
        // A native LogMessage is a game-sheet event, not a player's chat message.
        ObserveHistory(() => RecordHistoryReward(reward, $"native LogMessage {message.LogMessageId} (SystemMessage/57)"));
    }

    private void OnHistoryReward(IHandleableChatMessage message)
    {
        if (!HuntRewardReceipt.IsRewardSource(message.LogKind, message.OriginalSender.IsEmpty)) return;
        ObserveHistory(() =>
        {
            // Also cover server messages printed directly to chat rather than via Dalamud's
            // native log queue. Compare the complete client-language sheet text, never a substring.
            historyContributionText ??= data.GetExcelSheet<LogMessage>()
                .TryGetRow(HuntRewardReceipt.ContributionMessageId, out var row) ? row.Text.ExtractText() : string.Empty;
            historyCurrencyNames ??= GetHistoryCurrencyNames();
            var reward = HuntRewardReceipt.MatchChat(message.LogKind, message.OriginalMessage.ExtractText(),
                historyContributionText, historyCurrencyNames);
            return reward is not null && RecordHistoryReward(reward, $"chat {message.LogKind}/{(int)message.LogKind}");
        });
    }

    private bool RecordHistoryReward(string reward, string source)
    {
        var now = DateTime.UtcNow;
        var world = travel.CurrentWorld;
        var territory = clientState.TerritoryType;
        if (!travel.TryReadLiveInstance(out var instance)) return false;
        var activeHere = current is not null && current.TerritoryId == territory &&
            current.World.Equals(world, StringComparison.OrdinalIgnoreCase) && current.Instance == Math.Max(1, instance);
        var visible = activeHere ? FindMark() : null;
        var live = visible is { IsDead: false, CurrentHp: > 0 };
        var eligiblePull = activeHere && !killConfirmed && pullCycleTagged && markEverIdentified &&
            markCombatObserved && now - lastMarkSeenUtc <= TimeSpan.FromSeconds(15);
        var credited = History.Reward(world, territory, instance, current, eligiblePull, now, reward, live);
        log.Information("History reward: {Reward}, source={Source}, world={World}, territory={Territory}, instance={Instance}, visibleLiveVeto={Live}, creditConfirmedNow={Credited}",
            reward, source, world, territory, instance, live, credited);
        return credited;
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
    // LogMessage sheet: 4442 (LogKind 57). Item/currency acquisitions also use 62 (LootNotice).
    internal const uint ContributionMessageId = 4442;
    internal const string Contribution = "Hunt contribution reward";

    internal static string? MatchNative(uint id) => id == ContributionMessageId ? Contribution : null;

    internal static bool IsRewardSource(XivChatType kind, bool senderEmpty) =>
        senderEmpty && kind is (XivChatType.SystemMessage or XivChatType.LootNotice);

    internal static string? MatchChat(XivChatType kind, string text, string localizedContribution, IEnumerable<string> currencyNames)
    {
        if (kind == XivChatType.SystemMessage && localizedContribution.Length != 0 &&
            text.Equals(localizedContribution, StringComparison.Ordinal)) return Contribution;
        return Match(text, currencyNames);
    }

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
