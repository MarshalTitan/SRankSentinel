namespace SRankSentinel;

internal sealed record VisitWorld(uint Id, string Name, uint LogicalDcId, string LogicalDc, uint RegionId);

internal enum VisitMenuAction
{
    WaitForSelector, SelectLogicalDataCenter, WaitForWorldList, SelectWorld, WorldSubmitted, AlreadyCurrent, Ineligible,
}

internal enum VisitArrivalAction { Wait, Arrived, AttemptFailed }
internal enum VisitFailureAction { Retry, RecoverUnresolved }
internal enum InstanceArrivalAction { NormalizeSingle, Ready, ChangeInstance }

internal static class WorldVisitPolicy
{
    internal const int MaximumAttempts = 2;

    internal static bool CanVisit(VisitWorld? source, VisitWorld? home, VisitWorld? target) =>
        source is not null && home is not null && target is not null &&
        source.RegionId is >= 1 and <= 4 && source.RegionId == home.RegionId &&
        source.RegionId == target.RegionId;

    internal static VisitMenuAction ChooseMenu(VisitWorld source, VisitWorld home, VisitWorld target,
        bool regionalSelector, string? selectedDc, IReadOnlyList<VisitWorld?> visibleWorlds)
    {
        if (!CanVisit(source, home, target))
            return VisitMenuAction.Ineligible;
        if (source.Id == target.Id)
            return VisitMenuAction.AlreadyCurrent;
        if (!regionalSelector && source.LogicalDcId != target.LogicalDcId)
            return VisitMenuAction.WaitForSelector;
        if (regionalSelector && !string.Equals(selectedDc, target.LogicalDc, StringComparison.OrdinalIgnoreCase))
            return VisitMenuAction.SelectLogicalDataCenter;
        // The dropdown can update before the rows. Require the actual rendered list to
        // belong to the requested logical DC and contain the exact destination.
        if (visibleWorlds.Count == 0 ||
            visibleWorlds.Any(world => world is null || world.LogicalDcId != target.LogicalDcId) ||
            visibleWorlds.Count(world => world?.Id == target.Id) != 1)
            return VisitMenuAction.WaitForWorldList;
        return VisitMenuAction.SelectWorld;
    }

    internal static VisitArrivalAction ObserveArrival(bool exactWorld, bool inUldah, bool busy,
        double secondsSinceConfirmation, double? secondsSinceLastBusy, double timeout)
    {
        if (exactWorld && inUldah && !busy)
            return VisitArrivalAction.Arrived;
        if (secondsSinceConfirmation >= timeout)
            return VisitArrivalAction.AttemptFailed;
        if (busy || exactWorld || secondsSinceConfirmation < 8 ||
            secondsSinceLastBusy is < 3)
            return VisitArrivalAction.Wait;
        return VisitArrivalAction.AttemptFailed;
    }

    internal static VisitFailureAction AfterFailedAttempt(int attempts, bool busy) =>
        !busy && attempts < MaximumAttempts ? VisitFailureAction.Retry : VisitFailureAction.RecoverUnresolved;

    internal static bool IsConfirmationFor(string prompt, string destination) =>
        !string.IsNullOrWhiteSpace(destination) &&
        prompt.Split('\n', 2)[0].Trim().Equals($"Travel to {destination}?", StringComparison.OrdinalIgnoreCase);

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
