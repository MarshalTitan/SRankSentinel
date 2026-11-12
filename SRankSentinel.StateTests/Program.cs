using SRankSentinel;

var tests = new (string Name, Action Run)[]
{
    ("Lifestream owns Zalera to Balmung on Crystal", () => LifestreamRoute("Zalera", "Balmung")),
    ("Lifestream owns Zalera to Midgardsormr across Crystal and Aether", () => LifestreamRoute("Zalera", "Midgardsormr")),
    ("Lifestream owns Midgardsormr to Seraph without returning Home", () => LifestreamRoute("Midgardsormr", "Seraph")),
    ("Lifestream owns Seraph to Leviathan without returning Home", () => LifestreamRoute("Seraph", "Leviathan")),
    ("Lifestream owns Leviathan to Zalera", () => LifestreamRoute("Leviathan", "Zalera")),
    ("same-World hunt never submits a Lifestream travel request", SameWorldSkipsRequest),
    ("Lifestream dependency absence blocks travel even for a same-World hunt", RequiredLifestreamUnavailable),
    ("temporarily busy Lifestream owns movement and is not interrupted", LifestreamBusyWait),
    ("a rejected request waits for idle and retries at most once", LifestreamRejectionBounded),
    ("accepted requests without arrival retry once and become unresolved", LifestreamMissingArrivalBounded),
    ("an accepted visit can return to the current World's hub before retrying", LifestreamRetryThroughHub),
    ("queued NA hunts use Lifestream in order without a Home World detour", KeepRegionalQueue),
    ("cross-DC death removes only the matching World, territory, instance and mark", MatchRegionalDeath),
    ("a non-NA World is ineligible for NA hunts", RejectOutsideRegion),
    ("Heritage Found stale instance 2 normalizes to the live single zone", NormalizeHeritageFound),
    ("a real multi-instance zone still changes to the required instance", KeepRealInstanceChange),
    ("raised Aetheryte plinth projects from above player ground", InstanceRaisedPlinth),
    ("blocked radial Aetheryte sample falls back to walkable perimeter", InstancePerimeterFallback),
    ("Aetheryte candidate search rejects remote, high and invalid projections", InstanceCandidateBounds),
    ("instance change walks locally, interacts, selects and resumes", InstanceRangeRecoverySequence),
    ("instance change in range interacts without mesh movement", InstanceAlreadyInRange),
    ("correct instance and zoning preempt local movement", InstanceArrivalAndZoningPreempt),
    ("instance menu opening preempts recovery movement", InstanceMenuPreemptsMovement),
    ("reloaded Aetheryte cancels pending approach and re-resolves", InstanceObjectReload),
    ("instance retries hold the active hunt after the bounded budget", InstanceRetriesAreBounded),
    ("instance paths reject distant detours and invalid coordinates", InstancePathsStayLocal),
    ("all NA logical DCs are enabled by default policy; subsets narrow reports", DataCenterScope),
    ("disabling DCs filters queued reports while keeping the active hunt eligible for travel", DataCenterQueueScope),
    ("95, 75, 50, 25 each confirm once with a protected retreat between tags", ConfirmEachCheckpoint),
    ("checkpoint requires live exact in-combat context and retries an unhandled action", CheckpointRequiresCombatAndConfirmation),
    ("late arrival at 70 percent consumes 95 and 75", () => LateCheckpoint(70f, 50f)),
    ("late arrival at 48 percent consumes 95, 75 and 50", () => LateCheckpoint(48f, 25f)),
    ("late arrival below 25 percent consumes all passed checkpoints", () => LateCheckpoint(18f, null)),
    ("late checkpoints remain consumed if the mark heals before dispatch", LateCheckpointHealing),
    ("fixed checkpoints still honor the initial threshold and enabled switches", CheckpointSettings),
    ("a completed Raise at 61 percent retags once then waits for 50", () => RaiseRetag(61f, 50f)),
    ("a completed Raise at 42 percent retags once then waits for 25", () => RaiseRetag(42f, 25f)),
    ("Raise waits for usable player and refuses dead or idle marks", RaiseSafety),
    ("a genuine pull reset clears milestones and Raise is not a reset", CheckpointPullReset),
    ("recognized Raise is accepted independently of button focus", RecognizedRaiseIsAccepted),
    ("incidental escape escalates only for a positive non-mark threat", IncidentalEscapeEscalatesSafely),
    ("LocateMark expires into a final scan then unresolved abandonment", LocateBudgetIsBounded),
    ("unprojectable destination escalates to approximate flight then abandonment", ProjectionRecoveryIsBounded),
    ("parking candidates honor the configured preferred clearance", ParkingUsesPreferredClearance),
    ("landing waits for horizontal alignment at the selected safe point", ParkingLandingRejectsShortStop),
    ("landing accepts normal vnavmesh endpoint tolerance once safely aligned", ParkingLandingAcceptsAlignedDescent),
    ("post-landing facing settle accepts a safe tiny outward step", ParkingFacingSettleAcceptsSafeStep),
    ("post-landing facing settle is suppressed for tag or unsafe terrain", ParkingFacingSettleIsOptionalAndSafe),
    ("visible live mark blocks an automatic FailCurrent request", LiveMarkBlocksFailCurrent),
    ("visible live mark blocks recovery-budget abandonment", LiveMarkBlocksRecoveryBudgetExit),
    ("visible live mark survives framework-exception recovery", LiveMarkBlocksFrameworkExceptionExit),
    ("visible live mark rejects conflicting external death evidence", LiveMarkBlocksConflictingDeathEvidence),
    ("visible live mark never enters SafeWait while still mounted", BlockedLiveMarkResumesParkingWhileMounted),
    ("manual Skip remains allowed while the mark is alive", ManualSkipRemainsAllowed),
    ("genuinely missing unresolved mark may still be abandoned", MissingUnresolvedMarkMayBeAbandoned),
    ("positively dead mark permits normal recovery", DeadMarkPermitsRecovery),
    ("Company Chocobo is preferred when available", CompanyChocoboIsPreferred),
    ("unavailable Company Chocobo falls back to Mount Roulette", CompanyChocoboFallsBack),
    ("Heritage Found southwest flag chooses attuned western crystal", HeritageFoundSouthwestCrystal),
    ("Heritage Found eastern and northern flags choose their nearest crystals", HeritageFoundOtherCrystals),
    ("marker fallback chooses nearer crystals in other hunt zones", OtherZonesChooseNearestCrystal),
    ("marker fallback respects attunement and missing map coverage", MapMarkerRespectsAttunement),
    ("distant visible mark retains the flight approach", DistantMarkKeepsFlightApproach),
    ("nearby mark enters parking at the scan boundary", NearbyMarkEntersParking),
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

static void ParkingLandingRejectsShortStop()
{
    False(HuntProgressPolicy.CanEnterParkingLandingHandoff(
        4.5f, 1f, 7.3f, 5f, 8f, 2f, 5f));
    False(HuntProgressPolicy.CanEnterParkingLandingHandoff(
        1.5f, 1f, 5.4f, 5f, 8f, 2f, 5f));
}

static void ParkingLandingAcceptsAlignedDescent()
{
    True(HuntProgressPolicy.CanEnterParkingLandingHandoff(
        1.5f, 4.8f, 7.3f, 5f, 8f, 2f, 5f));
    False(HuntProgressPolicy.CanEnterParkingLandingHandoff(
        1.5f, 5.2f, 7.3f, 5f, 8f, 2f, 5f));
}

static void ParkingFacingSettleAcceptsSafeStep()
{
    True(HuntProgressPolicy.CanBeginParkingFacingSettle(
        true, false, false, 23f, 23.75f, 18f, 23f, 0.75f, 0.1f));
    True(HuntProgressPolicy.CanBeginParkingFacingSettle(
        true, false, false, 7.3f, 8.2f, 5f, 8f, 0.9f, 0.1f));
}

static void ParkingFacingSettleIsOptionalAndSafe()
{
    False(HuntProgressPolicy.CanBeginParkingFacingSettle(
        true, true, false, 23f, 23.75f, 18f, 23f, 0.75f, 0.1f));
    False(HuntProgressPolicy.CanBeginParkingFacingSettle(
        true, false, true, 23f, 23.75f, 18f, 23f, 0.75f, 0.1f));
    False(HuntProgressPolicy.CanBeginParkingFacingSettle(
        true, false, false, 17.9f, 18.8f, 18f, 23f, 0.9f, 0.1f));
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

static void BlockedLiveMarkResumesParkingWhileMounted()
{
    Equal(BlockedLiveEntityRecoveryAction.ResumeParking,
        HuntProgressPolicy.DecideBlockedLiveEntityRecovery(false, true, false));
    Equal(BlockedLiveEntityRecoveryAction.ResumeParking,
        HuntProgressPolicy.DecideBlockedLiveEntityRecovery(false, false, true));
    Equal(BlockedLiveEntityRecoveryAction.ResumeTagRecovery,
        HuntProgressPolicy.DecideBlockedLiveEntityRecovery(true, true, true));
    Equal(BlockedLiveEntityRecoveryAction.SafeWait,
        HuntProgressPolicy.DecideBlockedLiveEntityRecovery(false, false, false));
}

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

static void HeritageFoundSouthwestCrystal() =>
    Equal(212u, HuntProgressPolicy.SelectMapMarkerAetheryte(
        1191u, 8.2f, 33.4f, [210u, 211u, 212u]));

static void HeritageFoundOtherCrystals()
{
    Equal(210u, HuntProgressPolicy.SelectMapMarkerAetheryte(
        1191u, 32f, 25f, [210u, 211u, 212u]));
    Equal(211u, HuntProgressPolicy.SelectMapMarkerAetheryte(
        1191u, 17f, 10f, [210u, 211u, 212u]));
}

static void OtherZonesChooseNearestCrystal()
{
    Equal(208u, HuntProgressPolicy.SelectMapMarkerAetheryte(
        1190u, 14f, 19f, [207u, 208u, 209u]));
    Equal(166u, HuntProgressPolicy.SelectMapMarkerAetheryte(
        956u, 30f, 12f, [166u, 167u, 168u]));
    Equal(77u, HuntProgressPolicy.SelectMapMarkerAetheryte(
        398u, 16f, 22f, [76u, 77u]));
}

static void MapMarkerRespectsAttunement()
{
    Equal(210u, HuntProgressPolicy.SelectMapMarkerAetheryte(
        1191u, 8.2f, 33.4f, [210u, 211u]));
    Equal(211u, HuntProgressPolicy.SelectMapMarkerAetheryte(
        1191u, 8.2f, 33.4f, [211u]));
    Equal(0u, HuntProgressPolicy.SelectMapMarkerAetheryte(
        1191u, 8.2f, 33.4f, []));
    Equal(0u, HuntProgressPolicy.SelectMapMarkerAetheryte(
        399u, 8.2f, 33.4f, [210u]));
}

static void DistantMarkKeepsFlightApproach() =>
    True(HuntProgressPolicy.ShouldDeferVisibleMarkDuringApproach(1200f, 80f, 3f));

static void NearbyMarkEntersParking()
{
    False(HuntProgressPolicy.ShouldDeferVisibleMarkDuringApproach(83f, 80f, 3f));
    True(HuntProgressPolicy.ShouldDeferVisibleMarkDuringApproach(83.1f, 80f, 3f));
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

static InstanceChangeObservation InstanceInput(bool range = false) =>
    new(false, false, false, false, false, true, range, false, true);

static void InstanceRangeRecoverySequence()
{
    var recovery = new InstanceChangeRecovery();
    var now = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
    Equal(InstanceChangeAction.Approach, recovery.Decide(now, InstanceInput()));
    recovery.Submitted(InstanceChangeAction.Approach, now);
    Equal(InstanceChangeAction.WaitForApproach, recovery.Decide(now.AddSeconds(1), InstanceInput()));
    Equal(InstanceChangeAction.Interact, recovery.Decide(now.AddSeconds(2), InstanceInput(true)));
    recovery.Submitted(InstanceChangeAction.Interact, now.AddSeconds(2));
    Equal(InstanceChangeAction.WaitForMenu, recovery.Decide(now.AddSeconds(3), InstanceInput(true)));
    Equal(InstanceChangeAction.OpenInstanceMenu,
        recovery.Decide(now.AddSeconds(4), InstanceInput(true) with { MainMenu = true, AnyMenu = true }));
    recovery.Submitted(InstanceChangeAction.OpenInstanceMenu, now.AddSeconds(4));
    Equal(InstanceChangeAction.SelectInstance,
        recovery.Decide(now.AddSeconds(5), InstanceInput(true) with { InstanceMenu = true, AnyMenu = true }));
    recovery.Submitted(InstanceChangeAction.SelectInstance, now.AddSeconds(5));
    Equal(InstanceChangeAction.WaitForTransition,
        recovery.Decide(now.AddSeconds(6), InstanceInput() with { Busy = true, TargetAvailable = false }));
    Equal(InstanceChangeAction.ResumeHunt,
        recovery.Decide(now.AddSeconds(8), InstanceInput() with { CorrectInstance = true }));
    recovery.Reset();
    Equal(0, recovery.Failures);
}

static void InstanceAlreadyInRange()
{
    var recovery = new InstanceChangeRecovery();
    Equal(InstanceChangeAction.Interact,
        recovery.Decide(DateTime.UtcNow, InstanceInput(true) with { MeshReady = false }));
}

static void InstanceArrivalAndZoningPreempt()
{
    var recovery = new InstanceChangeRecovery();
    var now = DateTime.UtcNow;
    recovery.Submitted(InstanceChangeAction.Approach, now);
    Equal(InstanceChangeAction.WaitForTransition,
        recovery.Decide(now, InstanceInput() with { Busy = true }));
    Equal(InstanceChangeAction.ResumeHunt,
        recovery.Decide(now, InstanceInput() with { CorrectInstance = true, Busy = true, TargetAvailable = false }));
}

static void InstanceMenuPreemptsMovement()
{
    var recovery = new InstanceChangeRecovery();
    var now = DateTime.UtcNow;
    recovery.Submitted(InstanceChangeAction.Approach, now);
    Equal(InstanceChangeAction.OpenInstanceMenu,
        recovery.Decide(now, InstanceInput() with { MainMenu = true, AnyMenu = true }));
    Equal(InstanceChangeAction.SelectInstance,
        recovery.Decide(now, InstanceInput() with { InstanceMenu = true, AnyMenu = true, TargetAvailable = false }));
}

static void InstanceObjectReload()
{
    var recovery = new InstanceChangeRecovery();
    var now = DateTime.UtcNow;
    recovery.Submitted(InstanceChangeAction.Approach, now);
    Equal(InstanceChangeAction.WaitForTarget,
        recovery.Decide(now, InstanceInput() with { TargetAvailable = false }));
    Equal(InstanceChangeAction.Approach, recovery.Decide(now.AddSeconds(1), InstanceInput()));
    Equal(0, recovery.Failures);
}

static void InstanceRetriesAreBounded()
{
    var recovery = new InstanceChangeRecovery();
    var now = DateTime.UtcNow;
    for (var i = 0; i < InstanceChangeRecovery.MaximumFailures; i++)
    {
        Equal(InstanceChangeAction.Interact, recovery.Decide(now, InstanceInput(true)));
        recovery.Submitted(InstanceChangeAction.Interact, now);
        now = now.AddSeconds(5);
        Equal(i < 2 ? InstanceChangeAction.Retry : InstanceChangeAction.Hold,
            recovery.Decide(now, InstanceInput(true)));
    }
    Equal(InstanceChangeAction.Hold, recovery.Decide(now.AddSeconds(60), InstanceInput()));
    Equal(InstanceChangeAction.ResumeHunt,
        recovery.Decide(now.AddSeconds(61), InstanceInput() with { CorrectInstance = true }));
    recovery.Reset();
    Equal(InstanceChangeAction.WaitForTarget,
        recovery.Decide(now, InstanceInput() with { TargetAvailable = false }));
    Equal(InstanceChangeAction.Retry,
        recovery.Decide(now.AddSeconds(11), InstanceInput() with { TargetAvailable = false }));
}

static void InstancePathsStayLocal()
{
    var origin = System.Numerics.Vector3.Zero;
    var target = new System.Numerics.Vector3(12, 0, 0);
    True(InstanceChangeRecovery.IsLocalPath([origin, new(6, 0, 0)], origin, origin, target));
    False(InstanceChangeRecovery.IsLocalPath([origin, new(40, 0, 0), new(6, 0, 0)], origin, origin, target));
    False(InstanceChangeRecovery.IsLocalPath([origin, new(float.NaN, 0, 0)], origin, origin, target));
    False(InstanceChangeRecovery.IsLocalPath([origin], origin, origin, target));
}

static void InstanceRaisedPlinth()
{
    var player = System.Numerics.Vector3.Zero;
    var crystal = new System.Numerics.Vector3(12, 0, 0);
    System.Numerics.Vector3? Project(System.Numerics.Vector3 sample, float extent)
    {
        // Match vnavmesh's documented floor condition: pt.Y must be <= sample.Y.
        const float groundY = 0.4f;
        return sample.Y >= groundY ? new(sample.X, groundY, sample.Z) : null;
    }
    var oldQuery = new System.Numerics.Vector3(6, player.Y, 0);
    True(Project(oldQuery, 2f) is null);
    var plan = InstanceChangeRecovery.PlanLocalApproach(player, player, crystal, 8f, Project);
    True(plan.Points.Count > 0);
    True(plan.Points.All(point => point.Y == 0.4f));
    True(plan.Points.All(point => System.Numerics.Vector3.Distance(point, crystal) <= 6.5f));
}

static void InstancePerimeterFallback()
{
    var player = System.Numerics.Vector3.Zero;
    var crystal = new System.Numerics.Vector3(12, 0, 0);
    System.Numerics.Vector3? Project(System.Numerics.Vector3 sample, float extent) =>
        MathF.Abs(sample.Z) < 1f ? null : new(sample.X, 0, sample.Z);
    var plan = InstanceChangeRecovery.PlanLocalApproach(player, player, crystal, 8f, Project);
    True(plan.ProjectionMisses > 0);
    True(plan.Points.Count > 0);
    True(plan.Points.All(point => MathF.Abs(point.Z) >= 1f));
    True(plan.Points.All(point => System.Numerics.Vector3.Distance(player, point) <= 15f));
}

static void InstanceCandidateBounds()
{
    var origin = System.Numerics.Vector3.Zero;
    var target = new System.Numerics.Vector3(12, 0, 0);
    var remote = InstanceChangeRecovery.PlanLocalApproach(origin, origin, target, 8f,
        (sample, extent) => new(90, 0, 0));
    Equal(0, remote.Points.Count);
    True(remote.LocalityRejections > 0);
    var high = InstanceChangeRecovery.PlanLocalApproach(origin, origin, target, 8f,
        (sample, extent) => new(sample.X, 20, sample.Z));
    Equal(0, high.Points.Count);
    var invalid = InstanceChangeRecovery.PlanLocalApproach(origin, origin, target, 8f,
        (sample, extent) => new(float.NaN, 0, 0));
    Equal(0, invalid.Points.Count);
    var unavailable = InstanceChangeRecovery.PlanLocalApproach(origin, origin, target, 8f,
        (sample, extent) => null);
    Equal(0, unavailable.Points.Count);
    True(unavailable.ProjectionMisses > 0);
}

static VisitWorld World(string name) => name switch
{
    "Zalera" => new(41, name, 8, "Crystal", 2),
    "Balmung" => new(91, name, 8, "Crystal", 2),
    "Midgardsormr" => new(65, name, 4, "Aether", 2),
    "Sargatanas" => new(99, name, 4, "Aether", 2),
    "Siren" => new(57, name, 4, "Aether", 2),
    "Seraph" => new(405, name, 11, "Dynamis", 2),
    "Leviathan" => new(64, name, 5, "Primal", 2),
    "Phoenix" => new(56, name, 6, "Light", 3),
    "Ravana" => new(22, name, 9, "Materia", 4),
    _ => throw new ArgumentOutOfRangeException(nameof(name)),
};

static HuntAlertSnapshot VisitAlert(string world, int instance) =>
    new("srank", world, "Atticus the Primogenitor", 1191, 13444, 0, instance, 16.7f, 9.5f,
        new DateTime(2026, 11, 10, 15, 0, 0, DateTimeKind.Utc));

static void MatchRegionalDeath()
{
    var hunted = VisitAlert("Sargatanas", 2);
    var local = VisitAlert("Balmung", 2);
    var otherInstance = VisitAlert("Sargatanas", 1);
    var otherTerritory = hunted with { TerritoryId = 1190 };
    var otherMark = hunted with { CreatureName = "Different mark", MarkDataId = 1 };
    var queue = new[] { hunted, local, otherInstance, otherTerritory, otherMark };
    var removed = queue.Where(alert => WorldVisitPolicy.MatchesDeath(alert, "Sargatanas", 1191, 2,
        hunted.ReceivedAtUtc.AddSeconds(10), alert.CreatureName == hunted.CreatureName, false)).ToArray();
    Equal(1, removed.Length);
    Equal(hunted.Key, removed[0].Key);
    False(WorldVisitPolicy.MatchesDeath(hunted, hunted.World, 1191, 2,
        hunted.ReceivedAtUtc.AddMinutes(-2), true, false));
}

static void RejectOutsideRegion()
{
    var na = World("Zalera");
    var oceania = World("Ravana");
    False(WorldVisitPolicy.CanVisit(na, na, World("Phoenix")));
    False(WorldVisitPolicy.CanVisit(na, na, oceania));
    False(WorldVisitPolicy.CanVisit(oceania, na, oceania));
}

static void NormalizeHeritageFound()
{
    var stale = VisitAlert("Balmung", 2);
    Equal(InstanceArrivalAction.NormalizeSingle, WorldVisitPolicy.InstanceArrival(0, stale.Instance));
    var normalized = WorldVisitPolicy.CanonicalizeInstance(stale, true);
    Equal(1, normalized.Instance);
    Equal(VisitAlert("Balmung", 1).Key, normalized.Key);
    Equal(stale.Key, WorldVisitPolicy.CanonicalizeInstance(stale, false).Key);
    True(WorldVisitPolicy.MatchesDeath(normalized, "Balmung", 1191, 2, stale.ReceivedAtUtc, true, true));
}

static void KeepRealInstanceChange()
{
    Equal(InstanceArrivalAction.ChangeInstance, WorldVisitPolicy.InstanceArrival(1, 2));
    Equal(InstanceArrivalAction.Ready, WorldVisitPolicy.InstanceArrival(2, 2));
    Equal(InstanceArrivalAction.ChangeInstance, WorldVisitPolicy.InstanceArrival(2, 1));
}

static DateTime TravelStart() => new(2026, 11, 12, 15, 0, 0, DateTimeKind.Utc);

static void LifestreamRoute(string from, string to)
{
    var source = World(from);
    var destination = World(to);
    var now = TravelStart();
    var requests = new List<string>();
    var travel = new LifestreamTravel(destination, now, TimeSpan.FromSeconds(90));
    True(WorldVisitPolicy.CanVisit(source, World("Zalera"), destination));
    bool Request(string world) { requests.Add(world); return true; }

    Equal(LifestreamTravelStep.ReturnToHub,
        travel.Tick(now, LifestreamReadiness.Idle, source.Id, true, false, false, Request));
    Equal(0, requests.Count);
    Equal(LifestreamTravelStep.RequestAccepted,
        travel.Tick(now.AddSeconds(1), LifestreamReadiness.Idle, source.Id, true, false, true, Request));
    Equal(LifestreamTravelStep.WaitForArrival,
        travel.Tick(now.AddSeconds(2), LifestreamReadiness.Idle, source.Id, true, false, true, Request));
    Equal(LifestreamTravelStep.WaitForTravel,
        travel.Tick(now.AddSeconds(3), LifestreamReadiness.Busy, destination.Id, true, false, true, Request));
    Equal(LifestreamTravelStep.WaitForTravel,
        travel.Tick(now.AddSeconds(4), LifestreamReadiness.Idle, destination.Id, true, true, true, Request));
    Equal(LifestreamTravelStep.WaitForPlayer,
        travel.Tick(now.AddSeconds(5), LifestreamReadiness.Idle, destination.Id, false, false, true, Request));
    Equal(LifestreamTravelStep.Arrived,
        travel.Tick(now.AddSeconds(6), LifestreamReadiness.Idle, destination.Id, true, false, true, Request));
    False(travel.AwaitingArrival);
    True(requests.SequenceEqual([to]));
}

static void SameWorldSkipsRequest()
{
    var now = TravelStart();
    var world = World("Seraph");
    var travel = new LifestreamTravel(world, now, TimeSpan.FromSeconds(90));
    Equal(LifestreamTravelStep.Arrived, travel.Tick(now, LifestreamReadiness.Idle, world.Id,
        true, false, true, _ => throw new InvalidOperationException("same-World travel requested")));
    Equal(0, travel.Attempts);
}

static void RequiredLifestreamUnavailable()
{
    var now = TravelStart();
    var source = World("Zalera");
    foreach (var target in new[] { source, World("Midgardsormr") })
    {
        var travel = new LifestreamTravel(target, now, TimeSpan.FromSeconds(90));
        Equal(LifestreamTravelStep.DependencyUnavailable,
            travel.Tick(now, LifestreamReadiness.Unavailable, source.Id, true, false, true,
                _ => throw new InvalidOperationException("unavailable provider was called")));
        Equal(0, travel.Attempts);
    }
}

static void LifestreamBusyWait()
{
    var now = TravelStart();
    var source = World("Zalera");
    var target = World("Midgardsormr");
    var travel = new LifestreamTravel(target, now, TimeSpan.FromSeconds(90));
    var calls = 0;
    bool Request(string _) { calls++; return true; }
    Equal(LifestreamTravelStep.WaitForTravel,
        travel.Tick(now, LifestreamReadiness.Busy, source.Id, true, false, true, Request));
    Equal(0, calls);
    Equal(LifestreamTravelStep.RequestAccepted,
        travel.Tick(now.AddSeconds(10), LifestreamReadiness.Idle, source.Id, true, false, true, Request));
    Equal(LifestreamTravelStep.TravelStillBusyAfterTimeout,
        travel.Tick(now.AddSeconds(130), LifestreamReadiness.Busy, source.Id, false, true, true, Request));
    Equal(LifestreamTravelStep.Arrived,
        travel.Tick(now.AddSeconds(132), LifestreamReadiness.Idle, target.Id, true, false, true, Request));
    Equal(1, calls);
}

static void LifestreamRejectionBounded()
{
    var now = TravelStart();
    var source = World("Zalera");
    var target = World("Midgardsormr");
    var travel = new LifestreamTravel(target, now, TimeSpan.FromSeconds(90));
    var requests = 0;
    bool Reject(string _) { requests++; return false; }
    LifestreamTravelStep At(int seconds, LifestreamReadiness provider) =>
        travel.Tick(now.AddSeconds(seconds), provider, source.Id, true, false, true, Reject);

    Equal(LifestreamTravelStep.RequestRejected, At(0, LifestreamReadiness.Idle));
    Equal(LifestreamTravelStep.WaitForTravel, At(1, LifestreamReadiness.Busy));
    Equal(LifestreamTravelStep.WaitForPlayer, At(2, LifestreamReadiness.Idle));
    Equal(LifestreamTravelStep.RequestRejected, At(3, LifestreamReadiness.Idle));
    Equal(LifestreamTravelStep.WaitForTravel, At(6, LifestreamReadiness.Busy));
    Equal(LifestreamTravelStep.Unresolved, At(7, LifestreamReadiness.Idle));
    Equal(2, requests);
}

static void LifestreamMissingArrivalBounded()
{
    var now = TravelStart();
    var target = World("Midgardsormr");
    var wrong = World("Balmung");
    var travel = new LifestreamTravel(target, now, TimeSpan.FromSeconds(90));
    var requests = 0;
    bool Request(string _) { requests++; return true; }
    LifestreamTravelStep At(int seconds, LifestreamReadiness provider) =>
        travel.Tick(now.AddSeconds(seconds), provider, wrong.Id, true, false, true, Request);

    Equal(LifestreamTravelStep.RequestAccepted, At(0, LifestreamReadiness.Idle));
    Equal(LifestreamTravelStep.TravelStillBusyAfterTimeout, At(91, LifestreamReadiness.Busy));
    Equal(LifestreamTravelStep.RetryScheduled, At(92, LifestreamReadiness.Idle));
    Equal(LifestreamTravelStep.WaitForPlayer, At(94, LifestreamReadiness.Idle));
    Equal(LifestreamTravelStep.RequestAccepted, At(95, LifestreamReadiness.Idle));
    Equal(LifestreamTravelStep.Unresolved, At(186, LifestreamReadiness.Idle));
    Equal(2, requests);
}

static void LifestreamRetryThroughHub()
{
    var now = TravelStart();
    var source = World("Zalera");
    var travel = new LifestreamTravel(World("Midgardsormr"), now, TimeSpan.FromSeconds(90));
    var requests = 0;
    bool Request(string _) { requests++; return true; }
    LifestreamTravelStep At(int seconds, bool hub) =>
        travel.Tick(now.AddSeconds(seconds), LifestreamReadiness.Idle, source.Id, true, false, hub, Request);
    Equal(LifestreamTravelStep.RequestAccepted, At(0, true));
    Equal(LifestreamTravelStep.RetryScheduled, At(91, false));
    Equal(LifestreamTravelStep.ReturnToHub, At(94, false));
    travel.ResumeFromHub(now.AddSeconds(120));
    Equal(LifestreamTravelStep.RequestAccepted, At(123, true));
    Equal(2, requests);
}

static void KeepRegionalQueue()
{
    var home = World("Zalera");
    var current = home;
    var queue = new Queue<HuntAlertSnapshot>(new[] { VisitAlert("Balmung", 1), VisitAlert("Midgardsormr", 1),
        VisitAlert("Seraph", 1), VisitAlert("Leviathan", 1), VisitAlert("Zalera", 1), VisitAlert("Phoenix", 1) });
    var requests = new List<string>();
    var handoffs = new List<string>();
    bool Request(string world) { requests.Add(world); return true; }
    var now = TravelStart();
    while (queue.TryDequeue(out var alert))
    {
        var target = World(alert.World);
        if (!WorldVisitPolicy.CanVisit(current, home, target))
            continue;
        var pending = queue.ToArray();
        var transfer = new LifestreamTravel(target, now, TimeSpan.FromSeconds(90));
        Equal(LifestreamTravelStep.RequestAccepted,
            transfer.Tick(now, LifestreamReadiness.Idle, current.Id, true, false, true, Request));
        var step = transfer.Tick(now.AddSeconds(10), LifestreamReadiness.Idle, target.Id, true, false, true, Request);
        Equal(LifestreamTravelStep.Arrived, step);
        handoffs.Add(target.Name);
        current = target;
        True(pending.SequenceEqual(queue));
    }
    string[] route = ["Balmung", "Midgardsormr", "Seraph", "Leviathan", "Zalera"];
    True(requests.SequenceEqual(route));
    True(handoffs.SequenceEqual(route));
}


static void DataCenterScope()
{
    string[] na = ["Balmung", "Zalera", "Midgardsormr", "Seraph", "Leviathan"];
    True(na.All(world => DataCenterHunting.Allows(World(world), NorthAmericanDataCenters.All)));
    True(na.Where(world => DataCenterHunting.Allows(World(world), NorthAmericanDataCenters.Crystal))
        .SequenceEqual(["Balmung", "Zalera"]));
    True(na.Where(world => DataCenterHunting.Allows(World(world),
        NorthAmericanDataCenters.Crystal | NorthAmericanDataCenters.Dynamis))
        .SequenceEqual(["Balmung", "Zalera", "Seraph"]));
}

static void DataCenterQueueScope()
{
    var active = World("Leviathan");
    var scope = NorthAmericanDataCenters.Crystal | NorthAmericanDataCenters.Dynamis;
    var queued = new[] { VisitAlert("Leviathan", 1), VisitAlert("Balmung", 1), VisitAlert("Seraph", 1) };
    True(queued.Where(alert => DataCenterHunting.Allows(World(alert.World), scope))
        .Select(alert => alert.World).SequenceEqual(["Balmung", "Seraph"]));
    False(DataCenterHunting.Allows(active, scope));
    True(WorldVisitPolicy.CanVisit(World("Zalera"), World("Zalera"), active));
    True(DataCenterHunting.Allows(active, scope | NorthAmericanDataCenters.Primal));
}

static TagMilestone AllCheckpoints() => TagMilestone.At75 | TagMilestone.At50 | TagMilestone.At25;
static bool Latch(TagCheckpointCycle pull, float hp) => pull.TryLatch(true, true, true, hp, 95f, AllCheckpoints());
static void Confirm(TagCheckpointCycle pull, float hp)
{
    var checkpoint = pull.Pending ?? throw new InvalidOperationException("Expected a checkpoint");
    True(pull.ConfirmHandled(checkpoint, TagCheckpointCycle.Crossed(hp, 95f), hp, 95f));
}

static void ConfirmEachCheckpoint()
{
    var pull = new TagCheckpointCycle();
    False(Latch(pull, 95.1f));
    foreach (var (hp, kind) in new[] { (95f, TagMilestone.Initial), (75f, TagMilestone.At75),
        (50f, TagMilestone.At50), (25f, TagMilestone.At25) })
    {
        True(Latch(pull, hp));
        Equal(kind, pull.Pending!.Value.Kind);
        Confirm(pull, hp);
        True(pull.HasConfirmedTag);
        True(pull.RetreatPending);
        False(Latch(pull, hp - 1));
        pull.CompleteRetreat();
        False(Latch(pull, hp));
    }
    False(Latch(pull, 1f));
}

static void CheckpointRequiresCombatAndConfirmation()
{
    var pull = new TagCheckpointCycle();
    False(pull.TryLatch(false, true, true, 90f, 95f, AllCheckpoints()));
    False(pull.TryLatch(true, false, true, 90f, 95f, AllCheckpoints()));
    False(pull.TryLatch(true, true, false, 90f, 95f, AllCheckpoints()));
    False(Latch(pull, 0f));
    True(Latch(pull, 95f));
    Confirm(pull, 95f);
    pull.CompleteRetreat();
    True(Latch(pull, 75f));
    var pending = pull.Pending;
    False(pull.CanDispatch(true, true, false, 74f, 95f));
    False(pull.CanDispatch(false, true, true, 74f, 95f));
    False(pull.CanDispatch(true, true, true, 76f, 95f));
    True(pull.CanDispatch(true, true, true, 74f, 95f));
    // Rejected submission: no handled confirmation is delivered.
    False(Latch(pull, 49f));
    Equal(pending!.Value, pull.Pending!.Value);
    Equal(TagMilestone.Initial, pull.Consumed);
    Confirm(pull, 49f);
    Equal(TagMilestone.Initial | TagMilestone.At75 | TagMilestone.At50, pull.Consumed);
}

static void LateCheckpoint(float hp, float? next)
{
    var pull = new TagCheckpointCycle();
    True(Latch(pull, hp));
    Confirm(pull, hp);
    True(pull.HasConfirmedTag);
    pull.CompleteRetreat();
    False(Latch(pull, hp));
    if (next is { } threshold)
    {
        False(Latch(pull, threshold + 0.1f));
        True(Latch(pull, threshold));
        Equal(threshold, pull.Pending!.Value.Hp);
    }
    else
        False(Latch(pull, 1f));
}

static void LateCheckpointHealing()
{
    var pull = new TagCheckpointCycle();
    True(Latch(pull, 48f));
    Confirm(pull, 61f);
    pull.CompleteRetreat();
    False(Latch(pull, 49f));
    True(Latch(pull, 25f));
    Equal(TagMilestone.At25, pull.Pending!.Value.Kind);
}

static void CheckpointSettings()
{
    var pull = new TagCheckpointCycle();
    False(pull.TryLatch(true, true, true, 72f, 60f, AllCheckpoints()));
    True(pull.TryLatch(true, true, true, 60f, 60f, TagMilestone.At25));
    var first = pull.Pending!.Value;
    True(pull.ConfirmHandled(first, TagCheckpointCycle.Crossed(60f, 60f), 60f, 60f));
    pull.CompleteRetreat();
    False(pull.TryLatch(true, true, true, 49f, 60f, TagMilestone.At25));
    True(pull.TryLatch(true, true, true, 25f, 60f, TagMilestone.At25));
}

static void RaiseRetag(float hp, float next)
{
    var pull = new TagCheckpointCycle();
    True(Latch(pull, 95f));
    Confirm(pull, 95f);
    var accepted = TravelStart();
    True(pull.ObserveRevival(accepted, true, true));
    True(pull.HasConfirmedTag);
    Equal(TagMilestone.Initial, pull.Consumed);
    True(Latch(pull, hp));
    Equal(TagMilestone.Raise, pull.Pending!.Value.Kind);
    Confirm(pull, hp);
    pull.CompleteRetreat();
    False(pull.ObserveRevival(accepted, true, true));
    False(Latch(pull, hp));
    True(Latch(pull, next));
    Equal(next, pull.Pending!.Value.Hp);
}

static void RaiseSafety()
{
    var pull = new TagCheckpointCycle();
    var accepted = TravelStart();
    False(pull.ObserveRevival(accepted, false, true));
    False(pull.RaisePending);
    True(pull.ObserveRevival(accepted, true, true));
    False(pull.TryLatch(true, false, true, 0f, 95f, AllCheckpoints()));
    False(pull.TryLatch(true, true, false, 50f, 95f, AllCheckpoints()));
    False(Latch(pull, 96f));
    True(Latch(pull, 50f));
    var disabled = new TagCheckpointCycle();
    False(disabled.ObserveRevival(accepted, true, false));
    False(disabled.RaisePending);
}

static void CheckpointPullReset()
{
    var pull = new TagCheckpointCycle();
    True(Latch(pull, 18f));
    Confirm(pull, 18f);
    True(pull.ObserveRevival(TravelStart(), true, true));
    True(pull.HasConfirmedTag);
    Equal(TagMilestone.Initial | AllCheckpoints(), pull.Consumed);
    pull.ResetPull(); // Production calls this only after the stable, same-entity reset check.
    False(pull.HasConfirmedTag);
    False(pull.RaisePending);
    Equal(TagMilestone.None, pull.Consumed);
    True(Latch(pull, 95f));
}
