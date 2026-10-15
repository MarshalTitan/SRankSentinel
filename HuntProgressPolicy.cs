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
    // Game Map and MapMarker data: territory maps and aetheryte marker subrows.
    // Used only if the normal linked Level positions yield no usable crystal.
    private static readonly IReadOnlyDictionary<uint, (float Scale, (uint Id, float X, float Y)[] Markers)> AetheryteMapMarkers =
        new Dictionary<uint, (float Scale, (uint Id, float X, float Y)[] Markers)>
        {
            [134] = (100f, [(52u, 1255f, 767f)]),
            [135] = (100f, [(10u, 1180f, 1697f)]),
            [137] = (100f, [(11u, 1514f, 1491f), (12u, 1005f, 1027f)]),
            [138] = (100f, [(13u, 1676f, 1537f), (14u, 1284f, 1242f)]),
            [139] = (100f, [(15u, 1462f, 1118f)]),
            [140] = (100f, [(17u, 1092f, 795f)]),
            [141] = (100f, [(53u, 1004f, 857f)]),
            [145] = (100f, [(18u, 637f, 1166f)]),
            [146] = (100f, [(19u, 865f, 607f), (20u, 697f, 1430f)]),
            [147] = (100f, [(21u, 1045f, 1478f), (22u, 997f, 993f)]),
            [148] = (100f, [(3u, 1037f, 1059f)]),
            [152] = (100f, [(4u, 838f, 1322f)]),
            [153] = (100f, [(6u, 793f, 1380f), (5u, 1203f, 953f)]),
            [154] = (100f, [(7u, 980f, 1252f)]),
            [155] = (100f, [(23u, 1247f, 789f)]),
            [156] = (100f, [(24u, 1064f, 356f)]),
            [180] = (100f, [(16u, 907f, 811f)]),
            [397] = (95f, [(71u, 1474f, 1697f)]),
            [398] = (95f, [(76u, 1530f, 1052f), (77u, 734f, 1056f)]),
            [400] = (95f, [(79u, 466f, 1322f), (78u, 1278f, 1580f)]),
            [401] = (95f, [(73u, 450f, 626f), (72u, 442f, 1548f)]),
            [402] = (95f, [(74u, 338f, 454f)]),
            [612] = (100f, [(99u, 1440f, 1270f), (98u, 395f, 515f)]),
            [613] = (100f, [(105u, 1384f, 762f), (106u, 1112f, 440f)]),
            [614] = (100f, [(107u, 1457f, 933f), (108u, 1269f, 620f)]),
            [620] = (100f, [(100u, 1135f, 275f), (101u, 753f, 1770f)]),
            [621] = (100f, [(102u, 373f, 1005f), (103u, 1640f, 1675f)]),
            [622] = (100f, [(109u, 1580f, 1363f), (110u, 1102f, 1057f), (128u, 269f, 1141f)]),
            [813] = (100f, [(132u, 1778f, 995f), (136u, 290f, 795f)]),
            [814] = (100f, [(137u, 1692f, 1313f), (138u, 780f, 1410f), (139u, 598f, 400f)]),
            [815] = (100f, [(140u, 1270f, 800f), (161u, 1423f, 1332f), (141u, 513f, 811f)]),
            [816] = (100f, [(144u, 680f, 1535f), (145u, 952f, 167f), (146u, 1405f, 337f)]),
            [817] = (100f, [(142u, 921f, 1321f), (143u, 1405f, 830f)]),
            [818] = (100f, [(147u, 1585f, 825f), (148u, 882f, 1242f)]),
            [956] = (100f, [(166u, 1467f, 545f), (167u, 1032f, 975f), (168u, 294f, 1326f)]),
            [957] = (100f, [(169u, 1220f, 1652f), (170u, 498f, 1060f), (171u, 1428f, 777f)]),
            [958] = (100f, [(172u, 616f, 1504f), (173u, 1542f, 845f)]),
            [959] = (100f, [(174u, 457f, 1674f), (175u, 1023f, 511f)]),
            [960] = (100f, [(179u, 480f, 1290f), (180u, 1085f, 365f), (181u, 1515f, 1355f)]),
            [961] = (100f, [(176u, 1182f, 1150f), (177u, 390f, 1566f), (178u, 493f, 802f)]),
            [1187] = (100f, [(200u, 1356f, 607f), (201u, 1490f, 1659f)]),
            [1188] = (100f, [(202u, 854f, 545f), (203u, 1565f, 1228f), (204u, 546f, 1335f), (238u, 1812f, 788f)]),
            [1189] = (100f, [(205u, 626f, 592f), (206u, 1745f, 1550f)]),
            [1190] = (100f, [(207u, 1403f, 1488f), (209u, 1335f, 456f), (208u, 732f, 909f)]),
            [1191] = (100f, [(210u, 1539f, 1231f), (212u, 805f, 1145f), (211u, 802f, 441f)]),
            [1192] = (100f, [(213u, 1024f, 1821f), (214u, 1681f, 741f), (215u, 769f, 627f)]),
        };

    public static uint SelectMapMarkerAetheryte(
        uint territoryId, float mapX, float mapY, IReadOnlyCollection<uint> usableAetheryteIds)
    {
        if (mapX <= 0f || mapY <= 0f ||
            !AetheryteMapMarkers.TryGetValue(territoryId, out var zone))
            return 0;

        var markerX = (mapX - 1f) * zone.Scale / 100f * 2048f / 41f;
        var markerY = (mapY - 1f) * zone.Scale / 100f * 2048f / 41f;
        return zone.Markers
            .Where(marker => usableAetheryteIds.Contains(marker.Id))
            .OrderBy(marker => (marker.X - markerX) * (marker.X - markerX) +
                               (marker.Y - markerY) * (marker.Y - markerY))
            .Select(marker => marker.Id)
            .FirstOrDefault();
    }

    public static bool ShouldDeferVisibleMarkDuringApproach(
        float horizontalDistance, float scanRange, float tolerance) =>
        horizontalDistance > scanRange + tolerance;

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
