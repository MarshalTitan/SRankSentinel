namespace SRankSentinel;

internal enum OrdinarySPresenceDecision
{
    Bypass,
    WaitForReady,
    Observe,
    ExactEntity,
    ConfirmedDeath,
    PresenceConfirmed,
    ArrivalUnavailable,
    AbandonUnresolved,
}

/// <summary>
/// A fresh native zone arrival owns the evidence; only settled destination readiness owns
/// the five-second clock. Absence is a pursuit decision, never evidence of a kill.
/// </summary>
internal sealed class OrdinarySPresenceGate
{
    // LogMessage sheet row verified in the Patch 7.58 game data. IChatGui.LogMessage supplies
    // this native ID independently of client language; ChatMessage/player quotations do not.
    internal const uint PowerfulMarkMessageId = 9331;
    internal const int ObservationSeconds = 5;
    private string targetWorld = string.Empty;
    private uint targetTerritory;
    private int arrivalInstance;
    internal DateTime ArrivalUtc { get; private set; }
    internal DateTime ReadyUtc { get; private set; }
    internal DateTime PresenceUtc { get; private set; }

    internal void Reset(string world = "", uint territory = 0)
    {
        targetWorld = world;
        targetTerritory = territory;
        arrivalInstance = 0;
        ArrivalUtc = ReadyUtc = PresenceUtc = DateTime.MinValue;
    }

    internal void ZoneArrived(uint territory, int instance, DateTime now)
    {
        // Also reset on same-territory instance changes and unrelated zone arrivals.
        ArrivalUtc = ReadyUtc = PresenceUtc = DateTime.MinValue;
        arrivalInstance = 0;
        if (targetTerritory == 0 || territory != targetTerritory)
            return;
        ArrivalUtc = now;
        arrivalInstance = Math.Max(1, instance);
    }

    internal bool ObserveNativeMessage(uint messageId, DateTime now)
    {
        if (messageId != PowerfulMarkMessageId || ArrivalUtc == DateTime.MinValue || now < ArrivalUtc)
            return false;
        if (PresenceUtc != DateTime.MinValue)
            return false;
        PresenceUtc = now;
        return true;
    }

    internal void PauseObservation() => ReadyUtc = DateTime.MinValue;

    internal OrdinarySPresenceDecision Decide(DateTime now, bool ordinaryS, bool ready,
        string world, uint territory, int liveInstance, int requiredInstance,
        bool exactEntity, bool confirmedDeath)
    {
        if (confirmedDeath)
            return OrdinarySPresenceDecision.ConfirmedDeath;
        if (!ordinaryS)
            return OrdinarySPresenceDecision.Bypass;
        if (!ready)
        {
            PauseObservation();
            return OrdinarySPresenceDecision.WaitForReady;
        }
        if (!world.Equals(targetWorld, StringComparison.OrdinalIgnoreCase) || territory != targetTerritory ||
            Math.Max(1, liveInstance) != Math.Max(1, requiredInstance))
        {
            ArrivalUtc = ReadyUtc = PresenceUtc = DateTime.MinValue;
            return OrdinarySPresenceDecision.WaitForReady;
        }
        if (ReadyUtc == DateTime.MinValue)
            ReadyUtc = now;
        if (exactEntity)
            return OrdinarySPresenceDecision.ExactEntity;
        // Never infer a negative from an event subscription that missed the arrival, or an
        // instance packet that disagrees with settled live topology. Keep LocateMark recovery.
        if (ArrivalUtc == DateTime.MinValue || arrivalInstance != Math.Max(1, liveInstance))
            return OrdinarySPresenceDecision.ArrivalUnavailable;
        if (PresenceUtc >= ArrivalUtc && PresenceUtc != DateTime.MinValue)
            return OrdinarySPresenceDecision.PresenceConfirmed;
        return now >= ReadyUtc.AddSeconds(ObservationSeconds)
            ? OrdinarySPresenceDecision.AbandonUnresolved : OrdinarySPresenceDecision.Observe;
    }

    internal double RemainingSeconds(DateTime now) => ReadyUtc == DateTime.MinValue
        ? ObservationSeconds : Math.Max(0, ObservationSeconds - (now - ReadyUtc).TotalSeconds);
}
