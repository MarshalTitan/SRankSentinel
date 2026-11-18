using System.Reflection;
using Dalamud.Plugin.Services;
using SRankSentinel;

internal static partial class Program
{
    private static void TestSsWatchCountdownRepair()
    {
        var start = DateTime.UtcNow.AddMinutes(-2);
        var plugin = CreateSsQueueConsumer(new Configuration());
        var opportunity = SsFixtureAlert(start);
        SetField(plugin, "current", opportunity);
        SetField(plugin, "stateSinceUtc", start);
        var state = typeof(Plugin).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!;
        state.SetValue(plugin, Enum.Parse(state.FieldType, "SsWatch"));
        var remaining = typeof(Plugin).GetMethod("SsWatchRemaining", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var invalid in new[] { DateTime.MinValue, DateTime.MaxValue })
        {
            var clock = new SsWatchLifetime();
            clock.Begin(start, start, 300);
            SetField(plugin, "ssWatchLifetime", clock);
            SetField(plugin, "ssWatchDeadlineUtc", invalid);
            SetField(plugin, "ssSpawnAnnounced", true);
            Check((string)remaining.Invoke(plugin, [start.AddSeconds(116)])! == "184s remaining",
                "An announced SS must always show a finite countdown after repairing a corrupt deadline.");
            Check((DateTime)typeof(Plugin).GetField("ssWatchDeadlineUtc", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(plugin)! == start.AddMinutes(5), "Repair restarted the five-minute reservation.");
        }
    }

    private static void TestSsWatchFortyQueuedHunts()
    {
        var now = DateTime.UtcNow;
        var config = new Configuration();
        var plugin = CreateSsQueueConsumer(config);
        var worldNames = new[] { "Faerie", "Zalera", "Seraph", "Leviathan", "Balmung", "Exodus", "Kraken", "Midgardsormr" };
        var ordinary = new List<HuntAlertSnapshot>();
        foreach (var world in worldNames)
        foreach (var (name, territory) in new (string, uint)[]
        {
            ("Sansheya", 1190), ("Sphatika", 957), ("Tarchia", 815), ("Tyger", 813), ("Atticus the Primogenitor", 1191),
        })
        {
            var definition = HuntCatalog.Resolve(territory, name)!;
            ordinary.Add(new("srank", world, name, territory, definition.DataId, definition.PreferredAetheryteId,
                1, 20, 20, now.AddSeconds(-40 - ordinary.Count)));
        }
        var ss = SsFixtureAlert(now.AddMinutes(-5));
        var queue = new Queue<HuntAlertSnapshot>(new[] { ss, SsWatchLifetime.Announce(ss) }.Concat(ordinary));
        SetField(plugin, "pendingAlerts", queue);
        var remove = typeof(Plugin).GetMethod("RemoveSsOpportunityDuplicates", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Check((int)remove.Invoke(plugin, [ss])! == 2 && queue.Count == 40,
            "SS expiry must remove both reserved/promoted duplicates while retaining all ordinary hunts.");
        Check(config.PendingAlerts.Count == 40 && config.KilledAlerts.Count == 0,
            "Queue cleanup must persist without fabricating an SS kill.");
        var dequeue = typeof(Plugin).GetMethod("TryDequeueNextValid", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] args = [null];
        Check((bool)dequeue.Invoke(plugin, args)!, "The queue did not resume after SS expiry.");
        var next = (HuntAlertSnapshot)args[0]!;
        Check(next.World == "Midgardsormr" && next.CreatureName == "Atticus the Primogenitor" && queue.Count == 39,
            "Replacement must use existing expansion priority and oldest-first ordering, not the newest report.");
    }

    private static void TestSsWatchKillHistoryIsolation()
    {
        var config = new Configuration();
        var journal = new HuntHistoryJournal(config);
        var now = DateTime.UtcNow;
        var normal = new HuntAlertSnapshot("srank", "Faerie", "Neyoozoteel", 1189, 12754, 1, 1, 20, 20, now);
        journal.Spawn(normal, "Sonar");
        journal.Tag(normal, now.AddSeconds(1));
        journal.Kill(normal, true, now.AddSeconds(2));
        var ss = SsFixtureAlert(now.AddSeconds(3));
        journal.Spawn(ss, "HuntAlerts");
        journal.Kill(ss, false, now.AddMinutes(2));
        var entry = config.SpawnHistory.Single(item => item.CreatureName == "Arch Aethereater");
        Check(entry.KilledAtUtc is not null && entry.TaggedAtUtc is null && entry.CreditedAtUtc is null &&
            config.CreditedHistory.Count == 0, "An undetected SS kill inherited the completed normal S's tag or credit.");
    }

    private static HuntAlertSnapshot SsFixtureAlert(DateTime start) =>
        new("sswatch", "Faerie", "Arch Aethereater", 1189, 0, 1, 1, 20, 20, start);

    private static Plugin CreateSsQueueConsumer(Configuration config)
    {
        var plugin = CreateConsumer(config);
        SetField(plugin, "ssWatchLifetime", new SsWatchLifetime());
        SetField(plugin, "log", DispatchProxy.Create<IPluginLog, SsWatchNullService>());
        foreach (var name in new[] { "killedAlerts", "faloopReportIdsByAlertKey", "unresolvedFaloopAlerts", "observedSingleInstances" })
        {
            var field = typeof(Plugin).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
            field.SetValue(plugin, Activator.CreateInstance(field.FieldType));
        }
        // IsWorldVisitCompatible uses the actual cached game-data relation. No client memory,
        // player movement, or native addon calls are needed for these queue regressions.
        var travel = new NativeTravel(null!, DispatchProxy.Create<IObjectTable, SsWatchNullService>(), null!, null!, null!);
        void SetTravel(string name, object value) => typeof(NativeTravel).GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(travel, value);
        var names = new[] { "Faerie", "Zalera", "Seraph", "Leviathan", "Balmung", "Exodus", "Kraken", "Midgardsormr" };
        var worlds = names.Select((name, i) => new VisitWorld((uint)(i + 1), name, 4, "Aether", 2)).ToDictionary(w => w.Id);
        SetTravel("visitWorlds", worlds);
        SetTravel("lastVisitWorld", worlds[1]);
        SetTravel("lastHomeWorld", worlds[1]);
        SetField(plugin, "travel", travel);
        return plugin;
    }
}

public class SsWatchNullService : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) =>
        method?.ReturnType is { IsValueType: true } type && type != typeof(void)
            ? Activator.CreateInstance(type) : null;
}
