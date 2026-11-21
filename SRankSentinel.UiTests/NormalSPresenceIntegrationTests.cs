using System.Reflection;
using System.Runtime.CompilerServices;
using Dalamud.Game.Chat;
using Dalamud.Plugin.Services;
using SRankSentinel;

internal static partial class Program
{
    private static HuntAlertSnapshot PresenceFixture(DateTime now, string name = "Neyoozoteel", uint territory = 1189) =>
        new("srank", "Diabolos", name, territory, 12754, 1, 1, 20, 20, now);

    private static void PresenceState(Plugin plugin, string state)
    {
        var field = typeof(Plugin).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(plugin, Enum.Parse(field.FieldType, state));
    }

    private static object? PresenceCall(Plugin plugin, string method, params object?[] args) =>
        typeof(Plugin).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(plugin, args);

    private static Plugin PresenceConsumer(Configuration config, HuntAlertSnapshot alert, OrdinarySPresenceGate gate)
    {
        var plugin = CreateConsumer(config);
        SetField(plugin, "current", alert);
        SetField(plugin, "normalSPresence", gate);
        SetField(plugin, "log", DispatchProxy.Create<IPluginLog, SsWatchNullService>());
        PresenceState(plugin, "CheckNormalSPresence");
        return plugin;
    }

    private static void TestNormalSPresenceCallback()
    {
        var now = DateTime.UtcNow;
        var gate = new OrdinarySPresenceGate();
        gate.Reset("Diabolos", 1189);
        gate.ZoneArrived(1189, 1, now.AddSeconds(-1));
        var config = new Configuration { Enabled = true };
        var plugin = PresenceConsumer(config, PresenceFixture(now), gate);
        var native = DispatchProxy.Create<ILogMessage, PresenceLogProxy>();
        ((PresenceLogProxy)(object)native).Id = 9332;
        PresenceCall(plugin, "OnNormalSPresenceLog", native);
        Check(gate.PresenceUtc == DateTime.MinValue, "SS prey message was treated as ordinary presence.");
        ((PresenceLogProxy)(object)native).Id = 9331;
        config.Enabled = false;
        PresenceCall(plugin, "OnNormalSPresenceLog", native);
        Check(gate.PresenceUtc == DateTime.MinValue, "Disabled automation buffered hunt evidence.");
        config.Enabled = true;
        PresenceCall(plugin, "OnNormalSPresenceLog", native);
        Check(gate.PresenceUtc >= gate.ArrivalUtc && gate.ReadyUtc == DateTime.MinValue,
            "Native message must buffer during loading without starting the clock or reading text.");
        gate.Reset("Diabolos", 1189);
        PresenceCall(plugin, "OnNormalSPresenceLog", native);
        Check(gate.PresenceUtc == DateTime.MinValue, "New hunt inherited previous presence without fresh arrival.");
    }

    private static void TestNormalSPresenceSsExclusions()
    {
        var now = DateTime.UtcNow;
        var plugin = PresenceConsumer(new Configuration { Enabled = true }, PresenceFixture(now), new());
        Check((bool)PresenceCall(plugin, "IsOrdinarySPresenceContext")!, "Known ordinary S should use arrival gate.");
        foreach (var (name, territory) in new (string, uint)[]
        {
            ("Forgiven Gossip", 813), ("Ker Shroud", 959), ("Crystal Incarnation", 1189),
            ("Forgiven Rebellion", 813), ("Ker", 959), ("Arch Aethereater", 1189),
        })
        {
            SetField(plugin, "current", PresenceFixture(now, name, territory));
            Check(!(bool)PresenceCall(plugin, "IsOrdinarySPresenceContext")!, $"{name} entered ordinary negative gate.");
        }
        SetField(plugin, "current", PresenceFixture(now) with { HuntType = "sswatch" });
        Check(!(bool)PresenceCall(plugin, "IsOrdinarySPresenceContext")!, "Reserved watch type entered negative gate.");
        SetField(plugin, "current", PresenceFixture(now));
        foreach (var state in new[] { "SsWatch", "PostKillSsGrace" })
        {
            PresenceState(plugin, state);
            Check(!(bool)PresenceCall(plugin, "IsOrdinarySPresenceContext")!, $"{state} entered negative gate.");
        }
        PresenceState(plugin, "CheckNormalSPresence");
        SetField(plugin, "ssChainObserved", true);
        Check(!(bool)PresenceCall(plugin, "IsOrdinarySPresenceContext")!, "Accepted SS chain entered negative gate.");
        // Centurio ordinary S ranks do not have an SS profile but still need this optimization.
        SetField(plugin, "ssChainObserved", false);
        SetField(plugin, "current", PresenceFixture(now, "Nandi", 139));
        Check((bool)PresenceCall(plugin, "IsOrdinarySPresenceContext")!, "Centurio ordinary S was accidentally excluded.");
    }

    private static void TestNormalSPresenceUnresolvedHistory()
    {
        var now = DateTime.UtcNow;
        var config = new Configuration { Enabled = true };
        var alert = PresenceFixture(now);
        var journal = new HuntHistoryJournal(config);
        journal.Spawn(alert, "Sonar");
        var gate = new OrdinarySPresenceGate();
        gate.Reset(alert.World, alert.TerritoryId);
        gate.ZoneArrived(alert.TerritoryId, 1, now);
        gate.Decide(now, true, true, alert.World, alert.TerritoryId, 1, 1, false, false);
        Check(gate.Decide(now.AddSeconds(5), true, true, alert.World, alert.TerritoryId, 1, 1, false, false) ==
            OrdinarySPresenceDecision.AbandonUnresolved, "Missing arrival did not choose unresolved recovery.");
        var plugin = PresenceConsumer(config, alert, gate);
        SetField(plugin, "vnav", RuntimeHelpers.GetUninitializedObject(typeof(VNavmeshIpc)));
        var next = alert with { World = "Zalera" };
        var queue = new Queue<HuntAlertSnapshot>([next]);
        SetField(plugin, "pendingAlerts", queue);
        PresenceCall(plugin, "AbandonNormalSPresence");
        var entry = config.SpawnHistory.Single();
        Check(entry.KilledAtUtc is null && entry.TaggedAtUtc is null && entry.CreditedAtUtc is null && config.CreditedHistory.Count == 0,
            "Message absence fabricated killed, tagged or personal credit History.");
        Check((bool)typeof(Plugin).GetField("discardAtUldah", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin)! &&
            typeof(Plugin).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin)!.ToString() == "ResetToUldah",
            "Fast absence must use normal unresolved Ul'dah recovery.");
        Check(queue.Count == 1 && queue.Peek() == next && config.KilledAlerts.Count == 0,
            "Fast absence changed unrelated queue entries or manufactured killed-alert cleanup.");
        journal.Kill(alert, false, now.AddSeconds(10));
        Check(entry.KilledAtUtc is not null && entry.TaggedAtUtc is null,
            "Later positive provider death must still update the previously unresolved hunt.");
    }
}

public class PresenceLogProxy : DispatchProxy
{
    public uint Id { get; set; }
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name == "get_LogMessageId"
        ? Id : throw new InvalidOperationException("Presence detection must not inspect localized text or retain native message data.");
}
