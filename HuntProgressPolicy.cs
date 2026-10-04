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

internal enum HuntExitRequestSource
{
    AutomaticStateTransition,
    FailCurrent,
    RecoveryBudget,
    FrameworkException,
    ExternalDeathEvidence,
    ManualSkip,
    ConfirmedDeath,
}

internal enum HuntExitDecision
{
    Allow,
    BlockForVisibleLiveEntity,
}

internal enum BlockedLiveEntityRecoveryAction
{
    ResumeTagRecovery,
    ResumeParking,
    SafeWait,
}

internal enum LongApproachStartupAction
{
    WaitForFlightAvailability,
    RequestMount,
    RequestTakeoff,
    BeginFlyingRoute,
    BeginGroundRouteBecauseFlightUnavailable,
    ResetForNextCycle,
    AbandonWithoutGroundFallback,
}

internal enum SentinelMountChoice
{
    CompanyChocobo,
    MountRoulette,
}

/// <summary>
/// Pure decisions shared by the live state machine and the no-dependency regression harness.
/// Keeping these boundaries free of Dalamud types makes the latches and bounded-recovery rules
/// testable without substituting game services.
/// </summary>
internal static class HuntProgressPolicy
{
    public static SentinelMountChoice SelectMount(
        bool preferredAttemptAllowed,
        bool companyChocoboUnlocked,
        bool companyChocoboActionReady) =>
        preferredAttemptAllowed && companyChocoboUnlocked && companyChocoboActionReady
            ? SentinelMountChoice.CompanyChocobo
            : SentinelMountChoice.MountRoulette;

    public static LongApproachStartupAction DecideLongApproachStartup(
        bool flightAvailabilityKnown,
        bool flightAvailable,
        bool mounted,
        bool inFlight,
        double elapsedSeconds,
        double startupBudgetSeconds,
        int startupCycle,
        int maximumStartupCycles)
    {
        if (inFlight)
            return LongApproachStartupAction.BeginFlyingRoute;
        if (elapsedSeconds >= startupBudgetSeconds)
            return startupCycle < maximumStartupCycles
                ? LongApproachStartupAction.ResetForNextCycle
                : LongApproachStartupAction.AbandonWithoutGroundFallback;
        if (!flightAvailabilityKnown)
            return LongApproachStartupAction.WaitForFlightAvailability;
        if (!mounted)
            return LongApproachStartupAction.RequestMount;
        return flightAvailable
            ? LongApproachStartupAction.RequestTakeoff
            : LongApproachStartupAction.BeginGroundRouteBecauseFlightUnavailable;
    }

    public static HuntExitDecision DecideHuntExit(
        HuntExitRequestSource source,
        bool physicallyInCurrentContext,
        bool exactCurrentEntityVisible,
        bool exactCurrentEntityAlive)
    {
        if (source == HuntExitRequestSource.ManualSkip)
            return HuntExitDecision.Allow;

        return physicallyInCurrentContext && exactCurrentEntityVisible && exactCurrentEntityAlive
            ? HuntExitDecision.BlockForVisibleLiveEntity
            : HuntExitDecision.Allow;
    }

    public static BlockedLiveEntityRecoveryAction DecideBlockedLiveEntityRecovery(
        bool tagRequired,
        bool mountedOrFlying,
        bool parkingRecoveryState)
    {
        if (tagRequired)
            return BlockedLiveEntityRecoveryAction.ResumeTagRecovery;
        if (mountedOrFlying || parkingRecoveryState)
            return BlockedLiveEntityRecoveryAction.ResumeParking;
        return BlockedLiveEntityRecoveryAction.SafeWait;
    }

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

    public static bool CanEnterParkingLandingHandoff(
        float horizontalDistanceToDestination,
        float verticalDistanceToDestination,
        float actualClearance,
        float emergencyClearance,
        float preferredClearance,
        float horizontalTolerance,
        float verticalTolerance)
    {
        // vnavmesh normally completes a submitted route slightly before its final waypoint.
        // Bound that endpoint error by the horizontal envelope instead of demanding sub-yalm
        // precision, while still keeping the player near the configured preference and never
        // inside the emergency clearance.
        var protectedClearance = MathF.Max(
            emergencyClearance,
            preferredClearance - horizontalTolerance - 0.5f);
        return horizontalDistanceToDestination <= horizontalTolerance &&
               verticalDistanceToDestination <= verticalTolerance &&
               actualClearance >= protectedClearance;
    }

    public static bool CanBeginParkingFacingSettle(
        bool exactMarkVisible,
        bool tagRequired,
        bool mountedOrFlying,
        float originClearance,
        float outwardClearance,
        float emergencyClearance,
        float preferredClearance,
        float outwardDistance,
        float verticalSeparation)
    {
        // The settle moves only outward and then returns to the already validated landing origin.
        // Emergency clearance is therefore the safety boundary; requiring near-exact preferred
        // clearance here can suppress the facing movement after normal vnavmesh endpoint error.
        var protectedClearance = emergencyClearance;
        return exactMarkVisible && !tagRequired && !mountedOrFlying &&
               originClearance >= protectedClearance &&
               outwardClearance >= originClearance + 0.25f &&
               outwardDistance is >= 0.45f and <= 1.1f &&
               verticalSeparation <= 0.75f;
    }
}
