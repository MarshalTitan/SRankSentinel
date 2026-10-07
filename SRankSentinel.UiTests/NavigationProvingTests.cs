using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Text.Json;
using Dalamud.Plugin.Services;
using SentinelCore.Diagnostics;
using SentinelCore.Identity;
using SentinelCore.Navigation;
using SRankSentinel;

internal static class NavigationProvingTests
{
    public static void Run()
    {
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        Check((bool)Invoke(plugin, "TickNavigationProvingApproach")! == false, "default travel must stay legacy");
        Check((bool)Invoke(plugin, "NavigationProvingWaitForMesh")! == false, "default readiness must stay legacy");
        var framework = DispatchProxy.Create<IFramework, FrameworkProxy>();
        Set(plugin, "framework", framework);
        var backend = new Backend();
        var diagnostics = new NavigationDiagnostics(new("SRankSentinel", "S Rank Sentinel", "MTitan", new(0, 7, 57, 0)));
        using var core = new NavigationCoordinator(backend, diagnostics,
            new NavigationOptions { ReadinessSettle = TimeSpan.Zero, RetryDelay = TimeSpan.Zero });
        var operation = core.Begin(new(new(100, 0, 0)));
        core.Tick(); core.Tick();
        Check(operation.State == NavigationState.Following, "test route must be following");
        Set(plugin, "provingOperation", operation);
        Handoff(plugin, "ApproachAlertCoordinates");
        Check(operation.Result == NavigationResult.Pending && backend.Stops == 0, "approach retained ownership");
        Handoff(plugin, "LocateMark");
        Check(operation.Result == NavigationResult.Cancelled && backend.Stops == 1, "hunt-policy handoff did not cancel");
        var successor = core.Begin(new(new(100, 0, 0)));
        core.Tick(); core.Tick();
        Invoke(plugin, "CancelNavigationProvingOperation");
        operation.Dispose();
        Check(successor.Result == NavigationResult.Pending && backend.Stops == 1, "retired handoff stopped successor");
        Check(!typeof(Configuration).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(f => f.Name.Contains("navigationProving", StringComparison.OrdinalIgnoreCase)),
            "proving opt-in must not persist in user config");
        TestProbeEligibility();
        TestEvidenceJournal(diagnostics);
        Console.WriteLine("PASS shared-navigation consumer hooks: legacy default, exclusive handoff, stale handle, isolated probe, session-only configuration");
    }
    private enum TestState { Idle, PrepareApproachDestination, LocateMark }
    private static void TestProbeEligibility()
    {
        var context = typeof(Plugin).GetMethod("NavigationProvingProbeContextMatches",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        bool Matches(uint currentTerritory, string currentWorld, int currentInstance,
            uint destinationTerritory, string destinationWorld, int destinationInstance) =>
            (bool)context.Invoke(null, [currentTerritory, currentWorld, currentInstance,
                destinationTerritory, destinationWorld, destinationInstance])!;
        Check(Matches(123, "World", 2, 123, "World", 2), "same probe context rejected");
        Check(!Matches(124, "World", 2, 123, "World", 2) &&
            !Matches(123, "Other", 2, 123, "World", 2) &&
            !Matches(123, "World", 1, 123, "World", 2),
            "probe crossed territory/world/instance boundary");

        var distance = typeof(Plugin).GetMethod("NavigationProvingProbeDistanceIsSufficient",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        bool Sufficient(Vector3 from, Vector3 to) => (bool)distance.Invoke(null, [from, to])!;
        Check(!Sufficient(Vector3.Zero, new(79.9f, 500, 0)) &&
            Sufficient(Vector3.Zero, new(80f, 500, 0)) &&
            !Sufficient(new(float.NaN, 0, 0), new(100, 0, 0)),
            "probe distance/finite guard failed");
        Console.WriteLine("PASS proving probe eligibility: exact zone/world/instance and meaningful finite route");
    }
    private static void TestEvidenceJournal(NavigationDiagnostics diagnostics)
    {
        var journal = new NavigationProvingJournal();
        journal.Record(ProvingEvent.Enabled, TestState.Idle);
        journal.Record(ProvingEvent.DomainState, TestState.PrepareApproachDestination);
        journal.Record(ProvingEvent.DomainState, TestState.LocateMark);
        journal.Record(ProvingEvent.Disabled, TestState.LocateMark);
        var empty = new NavigationDiagnostics(new("SRankSentinel", "private-display", "private-author", new(0, 7, 56, 0)));
        using var bypass = JsonDocument.Parse(journal.Export(empty, false, TestState.LocateMark));
        var session = bypass.RootElement.GetProperty("ProvingSession");
        Check(!session.GetProperty("SharedOperationObserved").GetBoolean() &&
            session.GetProperty("Observations").GetArrayLength() == 4 &&
            bypass.RootElement.GetProperty("Entries").GetArrayLength() == 0,
            "enabled legacy-only run must be explained without fabricated Core movement");
        journal.Record(ProvingEvent.ProbeDestinationSet, TestState.Idle);
        journal.Record(ProvingEvent.ProbeStarted, TestState.Idle, Guid.NewGuid());
        journal.Record(ProvingEvent.ProbeCancelled, TestState.Idle, Guid.NewGuid());
        Check(journal.OperationsStarted == 0, "probe observations fabricated an operation count");
        journal.Record(ProvingEvent.OperationStarted, TestState.PrepareApproachDestination, Guid.NewGuid());
        journal.Record(ProvingEvent.Disabled, TestState.LocateMark);
        using var observed = JsonDocument.Parse(journal.Export(diagnostics, false, TestState.LocateMark));
        Check(observed.RootElement.GetProperty("ProvingSession").GetProperty("OperationsStarted").GetInt32() == 1 &&
            observed.RootElement.GetProperty("Entries").GetArrayLength() > 0, "off lost real movement evidence");
        var replacement = new NavigationProvingJournal();
        Check(replacement.InstanceId != journal.InstanceId && replacement.OperationsStarted == 0,
            "reload provenance must distinguish fresh instances");
        for (var i = 0; i < 300; i++) journal.Record(ProvingEvent.DomainState, TestState.Idle);
        using var bounded = JsonDocument.Parse(journal.Export(empty, false, TestState.Idle));
        Check(bounded.RootElement.GetProperty("ProvingSession").GetProperty("Observations").GetArrayLength() == 256,
            "journal unbounded");
        Check(!journal.Export(empty, false, TestState.Idle).Contains("private-"), "private identity leaked");
        Console.WriteLine("PASS proving evidence: legacy-only session, off retention, instance identity, bounded sanitized export");
    }
    private static void Handoff(Plugin plugin, string state)
    {
        var method = typeof(Plugin).GetMethod("NavigationProvingStateHandoff", BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(plugin, [Enum.Parse(method.GetParameters()[0].ParameterType, state)]);
    }
    private static object? Invoke(Plugin plugin, string name) =>
        typeof(Plugin).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(plugin, null);
    private static void Set(Plugin plugin, string field, object value) =>
        typeof(Plugin).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, value);
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    public class FrameworkProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "RunOnFrameworkThread" && args?[0] is Action action)
            { action(); return Task.CompletedTask; }
            throw new NotSupportedException(targetMethod?.Name);
        }
    }
    private sealed class Backend : INavigationAdapter
    {
        private bool following;
        public int Stops;
        public NavigationSnapshot Read() => new(new(1, 1), new ZoneStamp(1, 1), false, true, -1,
            Vector3.Zero, true, true, FlightAvailability.Available, following, following ? [new(100, 0, 0)] : []);
        public Task<IReadOnlyList<Vector3>> FindPath(Vector3 from, Vector3 to, bool fly, CancellationToken cancellation)
            => Task.FromResult<IReadOnlyList<Vector3>>([from, to]);
        public void Follow(IReadOnlyList<Vector3> path, bool fly) => following = true;
        public void Stop() { following = false; Stops++; }
        public void RequestMount() => throw new InvalidOperationException();
        public void RequestTakeoff() => throw new InvalidOperationException();
        public Vector3? ProjectLanding(Vector3 candidate, float searchRadius) => candidate;
    }
}
