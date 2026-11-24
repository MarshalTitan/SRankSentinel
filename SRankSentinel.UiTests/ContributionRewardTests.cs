using Dalamud.Game.Text;
using SRankSentinel;

internal static partial class Program
{
    private static void TestContributionSources()
    {
        const string en = "You have been rewarded for your contribution in slaying the mark.";
        const string localized = "localized sheet text for row 4442";
        string[] currencies = ["Centurio Seals", "Sacks of Nuts"];
        string? Receive(XivChatType kind, string sender, string text, string sheetText = en) =>
            HuntRewardReceipt.IsRewardSource(kind, sender.Length == 0)
                ? HuntRewardReceipt.MatchChat(kind, text, sheetText, currencies) : null;
        Check(HuntRewardReceipt.MatchNative(4442) == HuntRewardReceipt.Contribution &&
              HuntRewardReceipt.MatchNative(9331) is null, "Presence or another native message qualified as a reward.");
        Check(Receive(XivChatType.SystemMessage, "", en) == HuntRewardReceipt.Contribution &&
              Receive(XivChatType.SystemMessage, "", localized, localized) == HuntRewardReceipt.Contribution,
            "An exact localized contribution receipt was missed.");
        Check(Receive(XivChatType.Say, "Player", en) is null && Receive(XivChatType.Party, "", en) is null &&
              Receive(XivChatType.Echo, "", en) is null && Receive(XivChatType.SystemMessage, "Player", en) is null &&
              Receive(XivChatType.SystemMessage, "", "Quote: " + en) is null && Receive(XivChatType.LootNotice, "", en) is null,
            "Chat or a quote was accepted as the game contribution line.");
        foreach (var currency in currencies)
        {
            var cfg = new Configuration();
            var journal = new HuntHistoryJournal(cfg);
            var now = DateTime.UtcNow;
            var alert = HistoryAlert(now);
            journal.Tag(alert, now);
            journal.Kill(alert, true, now.AddSeconds(1));
            var receipt = Receive(XivChatType.LootNotice, "", $"You obtain 10 {currency}.");
            Check(receipt == currency && journal.Reward(alert.World, alert.TerritoryId, 1, null, false, now.AddSeconds(2), receipt),
                "An independently sufficient positive hunt currency receipt was lost.");
        }
    }

    private static void TestContributionOrdering()
    {
        foreach (var rewardFirst in new[] { true, false })
        foreach (var clear in new[] { true, false })
        {
            var cfg = new Configuration();
            var journal = new HuntHistoryJournal(cfg);
            var now = DateTime.UtcNow;
            var alert = HistoryAlert(now);
            journal.Spawn(alert, "Sonar");
            journal.Tag(alert, now);
            Check(cfg.SpawnHistory.Single().CreditedAtUtc is null && cfg.CreditedHistory.Count == 0, "Tag alone became credit.");
            var receiptAt = now.AddSeconds(rewardFirst ? 2 : 4);
            bool Receive() => journal.Reward(alert.World, alert.TerritoryId, 0, rewardFirst ? alert : null,
                rewardFirst, receiptAt, HuntRewardReceipt.MatchNative(4442)!);
            if (rewardFirst)
                Check(!Receive() && cfg.SpawnHistory.Single().KilledAtUtc is null, "Receipt alone declared a kill.");
            if (clear) journal.ClearAll();
            journal.Kill(alert, true, now.AddSeconds(3));
            if (!rewardFirst) Check(Receive(), "Contribution after confirmed kill did not credit it.");
            var credit = cfg.CreditedHistory.Single();
            Check(credit.CreditedAtUtc == receiptAt && credit.Source == "Sonar" && credit.Reward == "Hunt contribution reward" &&
                  (clear ? cfg.SpawnHistory.Count == 0 : cfg.SpawnHistory.Single().CreditedAtUtc == receiptAt),
                "Credit detail, visible History metric, or clear retention was incorrect.");
            Check(!journal.Reward(alert.World, alert.TerritoryId, 1, null, false, now.AddSeconds(5), "Sacks of Nuts") &&
                  cfg.CreditedHistory.Count == 1, "Associated currency credited the same receipt twice.");
        }
    }

    private static void TestContributionProtection()
    {
        var cfg = new Configuration();
        var journal = new HuntHistoryJournal(cfg);
        var now = DateTime.UtcNow;
        var alert = HistoryAlert(now, instance: 2);
        var other = HistoryAlert(now, "Other", instance: 2);
        var reward = HuntRewardReceipt.MatchNative(4442)!;
        journal.Tag(alert, now);
        Check(!journal.Reward(alert.World, 813, 2, alert, true, now, reward, currentVisiblyAlive: true),
            "Visibly live mark accepted an unrelated receipt.");
        journal.Kill(alert, true, now.AddSeconds(1));
        Check(cfg.CreditedHistory.Count == 0, "Live veto left a pending receipt to credit later.");
        Check(!journal.Reward("Diabolos", 813, 2, null, false, now.AddSeconds(2), reward) &&
              !journal.Reward(alert.World, 814, 2, null, false, now.AddSeconds(2), reward) &&
              !journal.Reward(alert.World, 813, 1, null, false, now.AddSeconds(2), reward) &&
              !journal.Reward(alert.World, 813, 2, null, false, now.AddSeconds(22), reward), "Wrong context/time granted credit.");
        journal.Tag(other, now);
        journal.Kill(other, true, now.AddSeconds(2));
        Check(!journal.Reward(alert.World, 813, 2, null, false, now.AddSeconds(3), reward) && cfg.CreditedHistory.Count == 0,
            "Two same-context candidates received guessed credit.");
        var elsewhere = other with { World = "Diabolos" };
        journal.Tag(elsewhere, now);
        journal.Kill(elsewhere, true, now.AddSeconds(2));
        Check(journal.Reward("Diabolos", 813, 2, null, false, now.AddSeconds(3), reward) &&
              cfg.CreditedHistory.Single().AlertKey == elsewhere.Key, "Unique contextual candidate did not receive credit.");
    }
}
