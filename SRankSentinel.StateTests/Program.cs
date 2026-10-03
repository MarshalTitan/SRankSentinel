using SRankSentinel;

var tests = new (string Name, Action Run)[]
{
    ("threshold crossed while mounted still latches a tag", ThresholdCrossedWhileMounted),
    ("failed first tag remains required as HP falls", FailedFirstTagRemainsRequired),
    ("confirmed tag suppresses additional attacks", ConfirmedTagSuppressesAdditionalAttacks),
    ("recognized Raise is accepted independently of button focus", RecognizedRaiseIsAccepted),
    ("incidental escape escalates only for a positive non-mark threat", IncidentalEscapeEscalatesSafely),
    ("LocateMark expires into a final scan then unresolved abandonment", LocateBudgetIsBounded),
    ("unprojectable destination escalates to approximate flight then abandonment", ProjectionRecoveryIsBounded),
    ("parking candidates honor the configured preferred clearance", ParkingUsesPreferredClearance),
    ("post-landing facing settle accepts a safe tiny outward step", ParkingFacingSettleAcceptsSafeStep),
    ("post-landing facing settle is suppressed for tag or unsafe terrain", ParkingFacingSettleIsOptionalAndSafe),
    ("visible live mark blocks an automatic FailCurrent request", LiveMarkBlocksFailCurrent),
    ("visible live mark blocks recovery-budget abandonment", LiveMarkBlocksRecoveryBudgetExit),
    ("visible live mark survives framework-exception recovery", LiveMarkBlocksFrameworkExceptionExit),
    ("visible live mark rejects conflicting external death evidence", LiveMarkBlocksConflictingDeathEvidence),
    ("manual Skip remains allowed while the mark is alive", ManualSkipRemainsAllowed),
    ("genuinely missing unresolved mark may still be abandoned", MissingUnresolvedMarkMayBeAbandoned),
    ("positively dead mark permits normal recovery", DeadMarkPermitsRecovery),
    ("Company Chocobo is preferred when available", CompanyChocoboIsPreferred),
    ("unavailable Company Chocobo falls back to Mount Roulette", CompanyChocoboFallsBack),
    ("long approach requests a mount before takeoff", LongApproachMountsFirst),
    ("mounted long approach waits for confirmed flight", LongApproachRequiresConfirmedFlight),
    ("confirmed flight permits the flying route", ConfirmedFlightPermitsRoute),
    ("first takeoff timeout requests an explicit reset", FirstTakeoffTimeoutRequestsReset),
    ("second takeoff timeout abandons without a ground run", SecondTakeoffTimeoutDoesNotGroundRun),
    ("second cycle starts immediately once flight confirms", SecondCycleConfirmedFlightPermitsRoute),
    ("a genuinely non-flyable territory permits an explicit ground route", NonFlyableTerritoryPermitsGroundRoute),
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL  {test.Name}: {exception.Message}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed");
return failures == 0 ? 0 : 1;

static void ThresholdCrossedWhileMounted()
{
    // Mount state is deliberately absent from the decision: it changes recovery mechanics,
    // never whether the current pull requires a tag.
    True(HuntProgressPolicy.ShouldLatchTag(true, true, 94.9f, 95f, false));
}

static void FailedFirstTagRemainsRequired()
{
    True(HuntProgressPolicy.ShouldLatchTag(true, true, 49f, 95f, false));
    True(HuntProgressPolicy.ShouldLatchTag(true, true, 24f, 95f, false));
}

static void ConfirmedTagSuppressesAdditionalAttacks() =>
    False(HuntProgressPolicy.ShouldLatchTag(true, true, 10f, 95f, true));

static void RecognizedRaiseIsAccepted()
{
    True(HuntProgressPolicy.ShouldAttemptRaise(true, true));
    False(HuntProgressPolicy.ShouldAttemptRaise(true, false));
    False(HuntProgressPolicy.MayDeclineStuckRaiseAfterKill(true, true, true, 30, 6));
    True(HuntProgressPolicy.MayDeclineStuckRaiseAfterKill(true, false, true, 6, 6));
}

static void IncidentalEscapeEscalatesSafely()
{
    True(HuntProgressPolicy.ShouldUseIncidentalCombatFallback(true, 4, 4, 10, 24));
    False(HuntProgressPolicy.ShouldUseIncidentalCombatFallback(false, 20, 4, 120, 24));
}

static void LocateBudgetIsBounded()
{
    Equal(LocateRecoveryAction.ContinueScanning,
        HuntProgressPolicy.DecideLocateRecovery(89, 120, false, 0, 20));
    Equal(LocateRecoveryAction.BeginFinalReportedAreaScan,
        HuntProgressPolicy.DecideLocateRecovery(120, 120, false, 0, 20));
    Equal(LocateRecoveryAction.AbandonUnresolved,
        HuntProgressPolicy.DecideLocateRecovery(140, 120, true, 20, 20));
}

static void ProjectionRecoveryIsBounded()
{
    Equal(ProjectionRecoveryAction.RetryProjection,
        HuntProgressPolicy.DecideProjectionRecovery(1, 2, false, 3, 90));
    Equal(ProjectionRecoveryAction.BeginApproximateFlight,
        HuntProgressPolicy.DecideProjectionRecovery(2, 2, false, 6, 90));
    Equal(ProjectionRecoveryAction.AbandonUnresolved,
        HuntProgressPolicy.DecideProjectionRecovery(7, 2, true, 90, 90));
}

static void ParkingUsesPreferredClearance()
{
    True(HuntProgressPolicy.IsPreferredParkingClearance(24.5f, 23f, 3f));
    False(HuntProgressPolicy.IsPreferredParkingClearance(29f, 23f, 3f));
    Equal(6f, HuntProgressPolicy.ParkingClearanceError(29f, 23f));
}

static void ParkingFacingSettleAcceptsSafeStep() =>
    True(HuntProgressPolicy.CanBeginParkingFacingSettle(
        true, false, false, 23f, 23.75f, 18f, 23f, 0.75f, 0.1f));

static void ParkingFacingSettleIsOptionalAndSafe()
{
    False(HuntProgressPolicy.CanBeginParkingFacingSettle(
        true, true, false, 23f, 23.75f, 18f, 23f, 0.75f, 0.1f));
    False(HuntProgressPolicy.CanBeginParkingFacingSettle(
        true, false, true, 23f, 23.75f, 18f, 23f, 0.75f, 0.1f));
    False(HuntProgressPolicy.CanBeginParkingFacingSettle(
        true, false, false, 22.4f, 23.15f, 18f, 23f, 0.75f, 0.1f));
    False(HuntProgressPolicy.CanBeginParkingFacingSettle(
        true, false, false, 23f, 23.2f, 18f, 23f, 0.75f, 0.1f));
    False(HuntProgressPolicy.CanBeginParkingFacingSettle(
        true, false, false, 23f, 23.75f, 18f, 23f, 1.5f, 0.1f));
}

static void LiveMarkBlocksFailCurrent() =>
    Equal(HuntExitDecision.BlockForVisibleLiveEntity,
        HuntProgressPolicy.DecideHuntExit(
            HuntExitRequestSource.FailCurrent, true, true, true));

static void LiveMarkBlocksRecoveryBudgetExit() =>
    Equal(HuntExitDecision.BlockForVisibleLiveEntity,
        HuntProgressPolicy.DecideHuntExit(
            HuntExitRequestSource.RecoveryBudget, true, true, true));

static void LiveMarkBlocksFrameworkExceptionExit() =>
    Equal(HuntExitDecision.BlockForVisibleLiveEntity,
        HuntProgressPolicy.DecideHuntExit(
            HuntExitRequestSource.FrameworkException, true, true, true));

static void LiveMarkBlocksConflictingDeathEvidence() =>
    Equal(HuntExitDecision.BlockForVisibleLiveEntity,
        HuntProgressPolicy.DecideHuntExit(
            HuntExitRequestSource.ExternalDeathEvidence, true, true, true));

static void ManualSkipRemainsAllowed() =>
    Equal(HuntExitDecision.Allow,
        HuntProgressPolicy.DecideHuntExit(
            HuntExitRequestSource.ManualSkip, true, true, true));

static void MissingUnresolvedMarkMayBeAbandoned() =>
    Equal(HuntExitDecision.Allow,
        HuntProgressPolicy.DecideHuntExit(
            HuntExitRequestSource.RecoveryBudget, true, false, false));

static void DeadMarkPermitsRecovery() =>
    Equal(HuntExitDecision.Allow,
        HuntProgressPolicy.DecideHuntExit(
            HuntExitRequestSource.ConfirmedDeath, true, true, false));

static void CompanyChocoboIsPreferred() =>
    Equal(SentinelMountChoice.CompanyChocobo,
        HuntProgressPolicy.SelectMount(true, true, true));

static void CompanyChocoboFallsBack()
{
    Equal(SentinelMountChoice.MountRoulette,
        HuntProgressPolicy.SelectMount(true, false, false));
    Equal(SentinelMountChoice.MountRoulette,
        HuntProgressPolicy.SelectMount(true, true, false));
    Equal(SentinelMountChoice.MountRoulette,
        HuntProgressPolicy.SelectMount(false, true, true));
}

static void LongApproachMountsFirst() =>
    Equal(LongApproachStartupAction.RequestMount,
        HuntProgressPolicy.DecideLongApproachStartup(
            true, true, false, false, 0, 10, 1, 2));

static void LongApproachRequiresConfirmedFlight() =>
    Equal(LongApproachStartupAction.RequestTakeoff,
        HuntProgressPolicy.DecideLongApproachStartup(
            true, true, true, false, 3, 10, 1, 2));

static void ConfirmedFlightPermitsRoute() =>
    Equal(LongApproachStartupAction.BeginFlyingRoute,
        HuntProgressPolicy.DecideLongApproachStartup(
            true, true, true, true, 4, 10, 1, 2));

static void FirstTakeoffTimeoutRequestsReset() =>
    Equal(LongApproachStartupAction.ResetForNextCycle,
        HuntProgressPolicy.DecideLongApproachStartup(
            true, true, true, false, 10, 10, 1, 2));

static void SecondTakeoffTimeoutDoesNotGroundRun() =>
    Equal(LongApproachStartupAction.AbandonWithoutGroundFallback,
        HuntProgressPolicy.DecideLongApproachStartup(
            true, true, true, false, 10, 10, 2, 2));

static void SecondCycleConfirmedFlightPermitsRoute() =>
    Equal(LongApproachStartupAction.BeginFlyingRoute,
        HuntProgressPolicy.DecideLongApproachStartup(
            true, true, true, true, 2, 10, 2, 2));

static void NonFlyableTerritoryPermitsGroundRoute() =>
    Equal(LongApproachStartupAction.BeginGroundRouteBecauseFlightUnavailable,
        HuntProgressPolicy.DecideLongApproachStartup(
            true, false, true, false, 3, 10, 1, 2));

static void True(bool value)
{
    if (!value)
        throw new InvalidOperationException("Expected true.");
}

static void False(bool value) => True(!value);

static void Equal<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}
