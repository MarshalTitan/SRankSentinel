namespace SentinelHunts.Core;

public enum SentinelState
{
    IdleAtUldah,
    EnsureUldah,
    WorldVisit,
    TeleportToTerritory,
    ChangeInstance,
    WaitForPlayerReady,
    ApproachReportedArea,
    LocateMark,
    ParkSafely,
    WaitForPull,
    TagApproach,
    TaggedWait,
    ClearIncidentalAggro,
    RecoverDeath,
    ReturnToUldah,
    PausedError,
}
