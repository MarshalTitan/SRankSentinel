namespace SRankSentinel;

internal enum LocateRecoveryAction
{
    ContinueScanning,
    BeginFinalReportedAreaScan,
    AbandonUnresolved,
}

internal enum ProjectionRecoveryAction
{
    RetryProjection,
    BeginApproximateFlight,
    AbandonUnresolved,
}

/// <summary>
/// Pure decisions shared by the live state machine and the no-dependency regression harness.
/// Keeping these boundaries free of Dalamud types makes the latches and bounded-recovery rules
/// testable without substituting game services.
/// </summary>
internal static class HuntProgressPolicy
{
    public static bool ShouldLatchTag(
        bool markAlive,
        bool markInCombat,
        float hpPercent,
        float engageThreshold,
        bool tagConfirmed) =>
        markAlive && markInCombat && hpPercent <= engageThreshold && !tagConfirmed;

    public static bool IsConfirmedPullReset(
        bool markAlive,
        bool markInCombat,
        float hpPercent,
        float resetHpThreshold,
        double stableSeconds,
        double requiredStableSeconds) =>
        markAlive && !markInCombat && hpPercent >= resetHpThreshold &&
        stableSeconds >= requiredStableSeconds;

    public static LocateRecoveryAction DecideLocateRecovery(
        double cumulativeSeconds,
        double searchBudgetSeconds,
        bool finalReportedAreaScanStarted,
        double finalScanSeconds,
        double finalScanBudgetSeconds)
    {
        if (!finalReportedAreaScanStarted)
            return cumulativeSeconds >= searchBudgetSeconds
                ? LocateRecoveryAction.BeginFinalReportedAreaScan
                : LocateRecoveryAction.ContinueScanning;

        return finalScanSeconds >= finalScanBudgetSeconds
            ? LocateRecoveryAction.AbandonUnresolved
            : LocateRecoveryAction.ContinueScanning;
    }

    public static ProjectionRecoveryAction DecideProjectionRecovery(
        int emptyProjectionPasses,
        int passesBeforeApproximateFlight,
        bool approximateFlightAlreadyUsed,
        double recoverySeconds,
        double recoveryBudgetSeconds)
    {
        if (recoverySeconds >= recoveryBudgetSeconds)
            return ProjectionRecoveryAction.AbandonUnresolved;
        if (!approximateFlightAlreadyUsed && emptyProjectionPasses >= passesBeforeApproximateFlight)
            return ProjectionRecoveryAction.BeginApproximateFlight;
        return ProjectionRecoveryAction.RetryProjection;
    }

    public static bool ShouldUseIncidentalCombatFallback(
        bool positivelyIdentifiedNonMarkThreat,
        int escapeAttempts,
        int maximumEscapeAttempts,
        double escapeSeconds,
        double maximumEscapeSeconds) =>
        positivelyIdentifiedNonMarkThreat &&
        (escapeAttempts >= maximumEscapeAttempts || escapeSeconds >= maximumEscapeSeconds);

    public static bool ShouldAttemptRaise(bool playerDead, bool promptRecognized) =>
        playerDead && promptRecognized;

    public static bool MayDeclineStuckRaiseAfterKill(
        bool playerDead,
        bool currentMarkAlive,
        bool killConfirmed,
        double resolutionSeconds,
        double resolutionBudgetSeconds) =>
        playerDead && !currentMarkAlive && killConfirmed &&
        resolutionSeconds >= resolutionBudgetSeconds;

    public static float ParkingClearanceError(float candidateClearance, float preferredClearance) =>
        MathF.Abs(candidateClearance - preferredClearance);

    public static bool IsPreferredParkingClearance(
        float candidateClearance,
        float preferredClearance,
        float tolerance) =>
        ParkingClearanceError(candidateClearance, preferredClearance) <= tolerance;
}
