using SRankSentinel;

internal static class NormalSPresenceTests
{
    private static readonly DateTime Start = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
    private static OrdinarySPresenceGate Arrival(int instance = 1)
    {
        var gate = new OrdinarySPresenceGate();
        gate.Reset("Diabolos", 1189);
        gate.ZoneArrived(1189, instance, Start);
        return gate;
    }

    private static OrdinarySPresenceDecision Read(OrdinarySPresenceGate gate, double seconds,
        bool ready = true, bool entity = false, bool death = false, bool ordinary = true,
        string world = "Diabolos", uint territory = 1189, int instance = 1, int required = 1) =>
        gate.Decide(Start.AddSeconds(seconds), ordinary, ready, world, territory, instance, required, entity, death);

    internal static void ExactEntityWins()
    {
        var gate = Arrival();
        Check(Read(gate, 0, entity: true) == OrdinarySPresenceDecision.ExactEntity, "visible exact actor must pass without text");
        Check(Read(gate, 5, entity: true) == OrdinarySPresenceDecision.ExactEntity, "actor on deadline must win over absence");
        Check(Read(gate, 5, ready: false, entity: true) == OrdinarySPresenceDecision.WaitForReady, "loading cannot expose a valid actor context");
    }

    internal static void PresencePasses()
    {
        var gate = Arrival();
        Check(Read(gate, 0) == OrdinarySPresenceDecision.Observe, "must initially observe");
        Check(gate.ObserveNativeMessage(9331, Start.AddSeconds(4)), "native presence not captured");
        Check(Read(gate, 4) == OrdinarySPresenceDecision.PresenceConfirmed, "should release before five seconds");
    }

    internal static void NoEvidenceExpires()
    {
        var gate = Arrival();
        Read(gate, 2);
        Check(Read(gate, 6.99) == OrdinarySPresenceDecision.Observe, "deadline must use ready time");
        Check(Read(gate, 7) == OrdinarySPresenceDecision.AbandonUnresolved, "absence should release at five ready seconds");
        Check(gate.PresenceUtc == DateTime.MinValue, "absence must not manufacture positive evidence");
    }

    internal static void PositiveDeathWins()
    {
        var gate = Arrival();
        Read(gate, 0);
        Check(Read(gate, 5, death: true) == OrdinarySPresenceDecision.ConfirmedDeath, "external kill must beat timeout");
        gate.ObserveNativeMessage(9331, Start.AddSeconds(4));
        Check(Read(gate, 5, entity: true, death: true) == OrdinarySPresenceDecision.ConfirmedDeath,
            "positively dead exact actor must retain death handling");
    }

    internal static void ConfirmedPresenceKeepsSearch()
    {
        var gate = Arrival();
        gate.ObserveNativeMessage(9331, Start.AddSeconds(1));
        Check(Read(gate, 2) == OrdinarySPresenceDecision.PresenceConfirmed, "presence must release");
        Check(Read(gate, 120) == OrdinarySPresenceDecision.PresenceConfirmed, "later entity absence must not become fast-skip");
    }

    internal static void StaleContextsRejected()
    {
        var gate = Arrival();
        gate.ObserveNativeMessage(9331, Start.AddSeconds(1));
        gate.ZoneArrived(813, 1, Start.AddSeconds(2));
        Check(!gate.ObserveNativeMessage(9331, Start.AddSeconds(3)), "old/unrelated territory cannot collect presence");
        gate.ZoneArrived(1189, 1, Start.AddSeconds(4));
        Check(Read(gate, 5) == OrdinarySPresenceDecision.Observe, "old territory evidence survived re-entry");
        gate.ObserveNativeMessage(9331, Start.AddSeconds(6));
        Check(Read(gate, 7, world: "Faerie") == OrdinarySPresenceDecision.WaitForReady && gate.PresenceUtc == DateTime.MinValue,
            "wrong World must invalidate evidence");
        gate.ZoneArrived(1189, 2, Start.AddSeconds(8));
        gate.ObserveNativeMessage(9331, Start.AddSeconds(9));
        Check(Read(gate, 10, instance: 2) == OrdinarySPresenceDecision.WaitForReady, "wrong instance must not pass");
        gate.ZoneArrived(1189, 1, Start.AddSeconds(11));
        Check(Read(gate, 12) == OrdinarySPresenceDecision.Observe, "instance evidence survived switch");
        gate.ObserveNativeMessage(9331, Start.AddSeconds(13));
        gate.Reset("Diabolos", 1189);
        gate.ZoneArrived(1189, 1, Start.AddSeconds(14));
        Check(Read(gate, 15) == OrdinarySPresenceDecision.Observe, "previous same-context hunt evidence survived Reset");
        Check(!gate.ObserveNativeMessage(9331, Start.AddSeconds(13)), "pre-arrival message must be rejected");
    }

    internal static void LoadingAndReadiness()
    {
        var gate = Arrival();
        for (var s = 0; s < 20; s++)
            Check(Read(gate, s, ready: false) == OrdinarySPresenceDecision.WaitForReady && gate.ReadyUtc == DateTime.MinValue,
                "loading must not spend observation budget");
        gate.ObserveNativeMessage(9331, Start.AddSeconds(10));
        Check(Read(gate, 20) == OrdinarySPresenceDecision.PresenceConfirmed, "loading-time presence must survive readiness");
        gate = Arrival();
        Read(gate, 20);
        Read(gate, 24, ready: false);
        Check(Read(gate, 30) == OrdinarySPresenceDecision.Observe && gate.ReadyUtc == Start.AddSeconds(30), "readiness loss must pause/restart clock");
        Check(Read(gate, 35) == OrdinarySPresenceDecision.AbandonUnresolved, "new ready window must still expire");
        gate.PauseObservation(); // Loading/death can preempt the normal hunt Tick entirely.
        Check(Read(gate, 60) == OrdinarySPresenceDecision.Observe && gate.ReadyUtc == Start.AddSeconds(60),
            "preempted hunt ticks must not spend presence budget while the player is unavailable");
    }

    internal static void SsBypasses()
    {
        // The integration test verifies real SS/prey names and plugin grace/watch states.
        foreach (var seconds in new[] { 0, 5, 360, 21600 })
            Check(Read(Arrival(), seconds, ordinary: false) == OrdinarySPresenceDecision.Bypass, "SS must never enter negative gate");
    }

    internal static void SequentialMissingHunts()
    {
        var gate = Arrival();
        for (var hunt = 0; hunt < 6; hunt++)
        {
            var start = hunt * 20;
            gate.Reset("Diabolos", 1189);
            gate.ZoneArrived(1189, 1, Start.AddSeconds(start));
            Check(Read(gate, start) == OrdinarySPresenceDecision.Observe, "each hunt needs a fresh clock");
            Check(Read(gate, start + 5) == OrdinarySPresenceDecision.AbandonUnresolved, "missing hunt spent old LocateMark budget");
        }
    }

    internal static void NativeSignalOnly()
    {
        var gate = Arrival();
        foreach (uint id in new uint[] { 0, 9332, 9334, 9330 })
            Check(!gate.ObserveNativeMessage(id, Start.AddSeconds(1)), "chat/SS/native unrelated message accepted");
        Check(gate.ObserveNativeMessage(9331, Start.AddSeconds(2)), "stable presence ID rejected");
        Check(!gate.ObserveNativeMessage(9331, Start.AddSeconds(3)) && gate.PresenceUtc == Start.AddSeconds(2),
            "repeat presence should retain first timestamp");
    }

    internal static void SingleInstance()
    {
        var gate = Arrival(0);
        Check(Read(gate, 2, instance: 0, required: 1) == OrdinarySPresenceDecision.Observe, "single-instance zero must canonicalize");
        gate.ObserveNativeMessage(9331, Start.AddSeconds(3));
        Check(Read(gate, 4, instance: 0) == OrdinarySPresenceDecision.PresenceConfirmed, "canonical arrival presence lost");
    }

    internal static void MissedArrivalIsAmbiguous()
    {
        var gate = new OrdinarySPresenceGate();
        gate.Reset("Diabolos", 1189);
        Check(Read(gate, 100) == OrdinarySPresenceDecision.ArrivalUnavailable, "missed native arrival must keep conservative search");
        Check(Read(gate, 100, entity: true) == OrdinarySPresenceDecision.ExactEntity, "exact actor still stronger than missing capture");
        gate.ZoneArrived(1189, 2, Start);
        Check(Read(gate, 100) == OrdinarySPresenceDecision.ArrivalUnavailable, "inconsistent native/live instance is ambiguous");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
