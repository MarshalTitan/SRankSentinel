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
    ("visible live mark blocks an automatic FailCurrent request", LiveMarkBlocksFailCurrent),
    ("visible live mark blocks recovery-budget abandonment", LiveMarkBlocksRecoveryBudgetExit),
    ("visible live mark survives framework-exception recovery", LiveMarkBlocksFrameworkExceptionExit),
    ("visible live mark rejects conflicting external death evidence", LiveMarkBlocksConflictingDeathEvidence),
    ("manual Skip remains allowed while the mark is alive", ManualSkipRemainsAllowed),
    ("genuinely missing unresolved mark may still be abandoned", MissingUnresolvedMarkMayBeAbandoned),
    ("positively dead mark permits normal recovery", DeadMarkPermitsRecovery),
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
