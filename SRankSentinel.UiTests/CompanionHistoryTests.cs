using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;
using SentinelCore.UI;
using SRankSentinel;

internal static partial class Program
{
    private static unsafe void TestNavigationTooltips()
    {
        foreach (var page in Enum.GetValues<ConfigurationPage>()) InContext(() =>
        {
            var pi = DispatchProxy.Create<IDalamudPluginInterface, RecordingPluginInterface>();
            var config = new Configuration { WindowTheme = 1 };
            config.Initialize(pi);
            var plugin = CreateConsumer(config);
            var nav = (SentinelModernNavItem[])typeof(Plugin).GetMethod("CreateModernNavigation",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(plugin, null)!;
            var itemIndex = Array.FindIndex(nav, item => item.Id == page.ToString());
            var item = nav[itemIndex];
            Vector2 center = default;
            var focus = false;
            nav[itemIndex] = item with { DrawIcon = context =>
            {
                center = (context.Minimum + context.Maximum) / 2;
                if (focus) ImGui.SetKeyboardFocusHere(-1);
                item.DrawIcon!(context);
            } };
            SetField(plugin, "modernNavigation", nav);
            LoadWindowPlacement(new(620, 520));
            for (var frame = 0; frame < 3; frame++) ConsumerFrame(plugin);
            ClickConsumer(plugin, center);
            ImGui.GetIO().AddMousePosEvent(650, 250);
            for (var frame = 0; frame < 4; frame++) ConsumerFrame(plugin);
            Check(config.WindowPage == (int)page, "Mouse did not select the requested page.");
            Check(!HasTooltip(), "A clicked navigation label followed the mouse into the page.");
            ImGui.GetIO().AddMousePosEvent(-100, -100);
            focus = true;
            ConsumerFrame(plugin);
            focus = false;
            var ctx = ImGui.GetCurrentContext();
            ctx.NavDisableHighlight = false;
            ctx.NavDisableMouseHover = true;
            for (var frame = 0; frame < 3; frame++) ConsumerFrame(plugin);
            Check(HasTooltip(), "Keyboard/controller navigation lost its icon label.");
        });
    }

    private static unsafe bool HasTooltip()
    {
        var windows = ImGui.GetCurrentContext().Windows;
        for (var index = 0; index < windows.Size; index++)
            if (windows[index].Active && (windows[index].Flags & ImGuiWindowFlags.Tooltip) != 0) return true;
        return false;
    }

    private static void TestCompanionPlugins() => InContext(() =>
    {
        Check(CompanionPlugins.All.Select(x => x.Name).SequenceEqual(new[] { "vnavmesh", "Lifestream", "HuntAlerts", "Sonar" }),
            "A requested companion is missing.");
        Check(!CompanionPlugins.IsSupported("SRankSentinel") && !CompanionPlugins.IsSupported("arbitrary-plugin"),
            "Activation allowlist permits unrelated plugins.");
        var installed = CompanionPlugins.All.Select((companion, index) =>
        {
            var plugin = DispatchProxy.Create<IExposedPlugin, ExposedPluginFixture>();
            var fixture = (ExposedPluginFixture)plugin;
            fixture.InternalName = companion.InternalNames[0];
            fixture.IsLoaded = index % 2 == 0;
            return plugin;
        }).ToArray();
        Check(CompanionPlugins.Find(installed, CompanionPlugins.All[3])?.InternalName == "SonarPlugin", "Sonar's real internal ID is not recognized.");
        var pi = DispatchProxy.Create<IDalamudPluginInterface, RecordingPluginInterface>();
        ((RecordingPluginInterface)pi).InstalledPlugins = installed;
        var config = new Configuration { WindowTheme = 1, WindowPage = (int)ConfigurationPage.Plugins };
        var plugin = CreateConsumer(config);
        SetField(plugin, "pi", pi);
        LoadWindowPlacement(new(620, 520));
        for (var frame = 0; frame < 4; frame++) ConsumerFrame(plugin);
        foreach (var companion in CompanionPlugins.All)
            Check(CompanionPlugins.Find(installed, companion) is not null, "Installed companion was not found.");
        Check(CompanionPluginActivation.UnavailableReason(installed[1])!.Contains("unavailable"),
            "Unknown Dalamud wrappers must fail closed and direct the user to the installer.");
        ((ExposedPluginFixture)installed[1]).IsOutdated = true;
        Check(CompanionPluginActivation.UnavailableReason(installed[1])!.StartsWith("Update"), "Outdated plugins must not enable.");
        ((RecordingPluginInterface)pi).InstalledPlugins = [];
        for (var frame = 0; frame < 3; frame++) ConsumerFrame(plugin);
        Check(ImGui.GetDrawData().TotalVtxCount > 0, "Missing-plugin state did not render.");
    });

    private static void TestActivationContract()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var assembly = typeof(IDalamudPluginInterface).Assembly;
        var exposed = assembly.GetType("Dalamud.Plugin.ExposedPlugin", true)!;
        var local = assembly.GetType("Dalamud.Plugin.Internal.Types.LocalPlugin", true)!;
        Check(exposed.GetFields(flags).Count(field => field.FieldType == local) == 1,
            "Dalamud's exposed-plugin backing field changed.");
        foreach (var name in new[] { "IsInDefaultProfile", "State", "Manifest", "EffectiveWorkingPluginId" })
            Check(local.GetProperty(name, flags) is not null, "Missing loader contract: " + name);
        var load = local.GetMethods(flags).Single(method => method.Name == "LoadAsync" && method.GetParameters().Length == 3);
        Check(load.GetParameters()[1].ParameterType == typeof(bool) &&
            load.GetParameters()[2].ParameterType == typeof(CancellationToken) &&
            Enum.GetNames(load.GetParameters()[0].ParameterType).Contains("Installer"), "Loader signature changed.");
        var profile = assembly.GetType("Dalamud.Plugin.Internal.Profiles.Profile", true)!;
        Check(profile.GetMethod("AddOrUpdateAsync", flags, [typeof(Guid), typeof(string), typeof(bool), typeof(bool)]) is not null,
            "Persistent profile enable operation changed.");
        Check(assembly.GetType("Dalamud.Plugin.Internal.PluginManager", true)!.GetProperty("SafeMode", flags) is not null &&
            assembly.GetType("Dalamud.Plugin.Internal.Profiles.ProfileManager", true)!.GetProperty("IsBusy", flags) is not null &&
            assembly.GetType("Dalamud.Plugin.Internal.Profiles.ProfileManager", true)!.GetProperty("DefaultProfile", flags) is not null &&
            local.GetProperty("Manifest", flags)!.PropertyType.GetProperty("ScheduledForDeletion", flags) is not null,
            "Dalamud's safe-mode, collection, or removal guard changed.");
        foreach (var name in new[] { "Dalamud.Plugin.Internal.PluginManager", "Dalamud.Plugin.Internal.Profiles.ProfileManager" })
        {
            var type = assembly.GetType(name, true)!;
            var service = assembly.GetType("Dalamud.Service`1", true)!.MakeGenericType(type);
            Check(service.GetMethod("Get", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes) is not null,
                "Dalamud service accessor changed.");
        }
    }

    private static HuntAlertSnapshot HistoryAlert(DateTime time, string name = "Tyger", int instance = 1) =>
        new("srank", "Coeurl", name, 813, name == "Tyger" ? 8910u : 8911u, 0, instance, 13, 17, time);

    private static void TestHistoryPersistence()
    {
        var config = new Configuration();
        var journal = new HuntHistoryJournal(config);
        var now = DateTime.UtcNow;
        var alert = HistoryAlert(now);
        Check(journal.Spawn(alert, "HuntAlerts"), "First spawn was not recorded.");
        Check(!journal.Spawn(alert with { ReceivedAtUtc = now.AddMinutes(15) }, "Sonar"), "Two sources duplicated the same live spawn.");
        Check(journal.Spawn(alert with { ReceivedAtUtc = now.AddHours(3) }, "Sonar"), "A later spawn was lost.");
        for (var index = 0; index < 600; index++) journal.Spawn(HistoryAlert(now.AddHours(index * 3 + 6)), "HuntAlerts");
        Check(config.SpawnHistory.Count == HuntHistoryJournal.Capacity, "Spawn history is not bounded.");
        var reloaded = JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(config))!;
        Check(new HuntHistoryJournal(reloaded).Snapshot().Spawns.Length == 500 &&
            reloaded.WindowTheme == config.WindowTheme && reloaded.CloseSafeProfile.WaitingDistance == config.CloseSafeProfile.WaitingDistance,
            "History persistence changed theme or hunt values.");
        var view = journal.Snapshot().Spawns;
        view[0].CreatureName = "mutated snapshot";
        Check(config.SpawnHistory[^1].CreatureName == "Tyger", "UI snapshot mutated persistent history.");
        var old = JsonSerializer.Deserialize<Configuration>("{\"Version\":16,\"WindowTheme\":1,\"WindowPage\":2}")!;
        Check(old.SpawnHistory.Count == 0 && old.CreditedHistory.Count == 0 && old.WindowTheme == 1 && old.WindowPage == 2,
            "Existing installs did not start with empty history while retaining their appearance.");
        var creditedConfig = new Configuration();
        var creditedJournal = new HuntHistoryJournal(creditedConfig);
        for (var index = 0; index < 510; index++)
        {
            var time = now.AddHours(index * 3);
            var next = HistoryAlert(time);
            creditedJournal.Tag(next, time);
            creditedJournal.Kill(next, true, time.AddSeconds(1));
            creditedJournal.Reward("Coeurl", 813, 1, next, true, time.AddSeconds(2), "Sack of Nuts");
        }
        Check(creditedConfig.CreditedHistory.Count == 500 && creditedConfig.SpawnHistory.Count == 500,
            "Credited history is not bounded independently.");
    }

    private static void TestHistoryCredit()
    {
        foreach (var rewardFirst in new[] { false, true })
        {
            var config = new Configuration();
            var journal = new HuntHistoryJournal(config);
            var now = DateTime.UtcNow;
            var alert = HistoryAlert(now);
            journal.Spawn(alert, "Sonar");
            journal.Tag(alert, now.AddSeconds(1));
            if (rewardFirst)
                Check(!journal.Reward("Coeurl", 813, 1, alert, true, now.AddSeconds(5), "Sack of Nuts"), "Reward alone became credit.");
            journal.Kill(alert, true, now.AddSeconds(6));
            if (!rewardFirst)
                Check(journal.Reward("Coeurl", 813, 1, alert, true, now.AddSeconds(7), "Sack of Nuts"), "Tagged killed hunt did not receive credit.");
            Check(config.CreditedHistory.Count == 1 && config.SpawnHistory[0].CreditedAtUtc is not null, "Credit was not recorded in both views.");
            Check(!journal.Reward("Coeurl", 813, 1, alert, true, now.AddSeconds(8), "Sack of Nuts") && config.CreditedHistory.Count == 1,
                "Repeated reward lines duplicated credit.");
            var reload = JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(config))!;
            Check(reload.CreditedHistory[0].Reward == "Sack of Nuts" && reload.CreditedHistory[0].TaggedAtUtc is not null,
                "Confirmed tag/credit did not survive reload.");
        }
    }

    private static void TestHistoryCreditRejections()
    {
        var config = new Configuration();
        var journal = new HuntHistoryJournal(config);
        var now = DateTime.UtcNow;
        var alert = HistoryAlert(now);
        journal.Spawn(alert, "HuntAlerts");
        journal.Kill(alert, false, now.AddSeconds(3));
        Check(!journal.Reward("Coeurl", 813, 1, alert, false, now.AddSeconds(4), "Allied Seal"), "Dead-on-arrival mark earned credit.");
        alert = alert with { ReceivedAtUtc = now.AddHours(3) };
        now = alert.ReceivedAtUtc;
        journal.Tag(alert, now);
        journal.Kill(alert, false, now.AddSeconds(3));
        Check(!journal.Reward("Coeurl", 813, 1, alert, false, now.AddSeconds(4), "Allied Seal"), "Reset pull without re-tag earned credit.");
        alert = alert with { ReceivedAtUtc = now.AddHours(3) };
        now = alert.ReceivedAtUtc;
        journal.Tag(alert, now);
        journal.Kill(alert, true, now.AddSeconds(3));
        Check(!journal.Reward("Gilgamesh", 813, 1, alert, true, now.AddSeconds(4), "Allied Seal") &&
            !journal.Reward("Coeurl", 814, 1, alert, true, now.AddSeconds(4), "Allied Seal") &&
            !journal.Reward("Coeurl", 813, 2, alert, true, now.AddSeconds(4), "Allied Seal") &&
            !journal.Reward("Coeurl", 813, 1, alert, true, now.AddMinutes(1), "Allied Seal"), "Reward escaped its hunt context/time window.");
        var reload = new HuntHistoryJournal(JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(config))!);
        Check(!reload.Reward("Coeurl", 813, 1, alert, true, now.AddSeconds(4), "Allied Seal"), "Reload restored an unconfirmed credit candidate.");
        var other = HistoryAlert(now, "Other mark");
        journal.Tag(other, now);
        journal.Kill(other, true, now.AddSeconds(3));
        Check(!journal.Reward("Coeurl", 813, 1, null, false, now.AddSeconds(4), "Allied Seal") && config.CreditedHistory.Count == 0,
            "Ambiguous generic currency was assigned to a hunt.");
    }

    private static void TestRewardReceipts()
    {
        var names = new[] { "Allied Seal", "Centurio Seal", "Sack of Nuts", "Sacks of Nuts", "同盟記章" };
        foreach (var text in new[] { "You obtain 100 Allied Seals.", "You receive 100 Centurio Seals.",
                     "You obtain 100 sacks of Nuts.", "同盟記章を１００入手した！" })
            Check(HuntRewardReceipt.Match(text, names) is not null, "Positive reward was not recognized: " + text);
        foreach (var text in new[] { "Tyger was defeated.", "You obtain 100 gil.", "You spend 100 Allied Seals.",
                     "You obtain 0 Allied Seals.", "You cannot obtain 100 Allied Seals.", "同盟記章を０入手した！" })
            Check(HuntRewardReceipt.Match(text, names) is null, "Non-reward became credit: " + text);
    }

    private static void TestPopulatedHistory()
    {
        foreach (var scale in new[] { 1f, 1.5f, 2f }) InContext(() =>
        {
            ImGui.GetIO().FontGlobalScale = scale;
            var config = new Configuration { WindowTheme = 1, WindowPage = (int)ConfigurationPage.History };
            var journal = new HuntHistoryJournal(config);
            for (var index = 0; index < 30; index++)
            {
                var now = DateTime.UtcNow.AddHours(-index * 3);
                var alert = HistoryAlert(now);
                journal.Tag(alert, now);
                journal.Kill(alert, true, now.AddSeconds(5));
                journal.Reward("Coeurl", 813, 1, alert, true, now.AddSeconds(6), "Sack of Nuts");
            }
            var plugin = CreateConsumer(config);
            foreach (var size in new[] { new Vector2(620, 520), new Vector2(1040, 860) })
            {
                LoadWindowPlacement(size * scale);
                for (var frame = 0; frame < 4; frame++) ConsumerFrame(plugin);
                CheckShellGeometry(Plugin.ConfigurationWindowId, scale);
                var spawns = FindWindow(Plugin.ConfigurationWindowId, "##SpawnHistory");
                var credits = FindWindow(Plugin.ConfigurationWindowId, "##CreditedHistory");
                Check(spawns.ContentSize.Y > spawns.Size.Y && credits.ContentSize.Y > credits.Size.Y &&
                    spawns.ContentSize.X <= spawns.Size.X + 1 && credits.ContentSize.X <= credits.Size.X + 1,
                    "Populated history cannot scroll independently or overflows horizontally.");
            }
        });
    }
}

public class ExposedPluginFixture : DispatchProxy
{
    public string InternalName { get; set; } = string.Empty;
    public bool IsLoaded { get; set; }
    public bool IsOutdated { get; set; }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
    {
        "get_InternalName" => InternalName,
        "get_Name" => InternalName,
        "get_IsLoaded" => IsLoaded,
        "get_IsOutdated" => IsOutdated,
        "get_IsBanned" or "get_IsOrphaned" or "get_IsDecommissioned" or "get_HasConfigUi" => false,
        _ => throw new NotSupportedException(targetMethod?.Name),
    };
}
