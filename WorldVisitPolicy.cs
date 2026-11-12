namespace SRankSentinel;

internal sealed record VisitWorld(uint Id, string Name, uint LogicalDcId, string LogicalDc, uint RegionId);

internal enum InstanceArrivalAction { NormalizeSingle, Ready, ChangeInstance }

internal static class WorldVisitPolicy
{
    internal static bool CanVisit(VisitWorld? source, VisitWorld? home, VisitWorld? target) =>
        source is not null && home is not null && target is not null &&
        source.RegionId is >= 1 and <= 4 && source.RegionId == home.RegionId &&
        source.RegionId == target.RegionId;

    internal static InstanceArrivalAction InstanceArrival(int liveInstance, int reportedInstance) =>
        liveInstance == 0 ? InstanceArrivalAction.NormalizeSingle :
        liveInstance == reportedInstance ? InstanceArrivalAction.Ready : InstanceArrivalAction.ChangeInstance;

    internal static HuntAlertSnapshot CanonicalizeInstance(HuntAlertSnapshot alert, bool confirmedSingleInstance) =>
        confirmedSingleInstance && alert.Instance != 1 ? alert with { Instance = 1 } : alert;

    internal static bool MatchesDeath(HuntAlertSnapshot alert, string world, uint territory, int instance,
        DateTime deathAtUtc, bool sameMark, bool confirmedSingleInstance) =>
        alert.World.Equals(world, StringComparison.OrdinalIgnoreCase) && sameMark &&
        (territory == 0 || alert.TerritoryId == territory) &&
        (confirmedSingleInstance || instance <= 0 || alert.Instance == instance) &&
        deathAtUtc >= alert.ReceivedAtUtc.AddMinutes(-1);
}
