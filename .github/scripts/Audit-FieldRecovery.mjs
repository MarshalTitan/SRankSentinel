import { readFileSync } from "node:fs";

const [pluginPath, combatPath, policyPath, projectPath] = process.argv.slice(2);
if (!pluginPath || !combatPath || !policyPath || !projectPath) {
  throw new Error("Usage: node Audit-FieldRecovery.mjs <Plugin.cs> <CombatController.cs> <HuntProgressPolicy.cs> <project>");
}

const plugin = readFileSync(pluginPath, "utf8");
const combat = readFileSync(combatPath, "utf8");
const policy = readFileSync(policyPath, "utf8");
const project = readFileSync(projectPath, "utf8");

const required = [
  [project, "<Version>0.7.42.0</Version>"],
  [plugin, "tagRequired = true"],
  [plugin, "BeginTagRequiredRecovery"],
  [plugin, "parking is suspended until one ranged tag is confirmed"],
  [plugin, "TickRaiseAcceptance"],
  [plugin, "TickCompletedRaiseResolution"],
  [combat, "RaiseInteractionState.Submitted"],
  [combat, "TryDeclineRaise"],
  [plugin, "TickIncidentalCombatFallback"],
  [combat, "TryBasicWarTrashAction"],
  [plugin, "remained unresolved—not confirmed dead"],
  [plugin, "TryPrepareApproximateAlertFlight"],
  [plugin, "Parking deviates from configured preference"],
  [policy, "LocateRecoveryAction.AbandonUnresolved"],
  [policy, "ProjectionRecoveryAction.BeginApproximateFlight"],
  [policy, "HuntExitDecision.BlockForVisibleLiveEntity"],
  [plugin, "TryGetExactVisibleLiveCurrentMark"],
  [plugin, "TryBlockAutomaticHuntExit"],
  [plugin, "Blocked automatic hunt exit: source={Source}, state={State}"],
  [plugin, "HuntExitRequestSource.FrameworkException"],
  [plugin, "HuntExitRequestSource.ManualSkip"],
  [plugin, "CompanyChocoboMountId = 1"],
  [plugin, "EnsureLongApproachMovementReady"],
  [plugin, "LongApproachStartupAction.AbandonWithoutGroundFallback"],
  [plugin, "LongApproachStartupAction.ResetForNextCycle"],
  [plugin, "LongApproachFlightStartupCycleBudgetSeconds = 10"],
  [plugin, "Flight-startup recovery/reset complete"],
  [plugin, "Final abandonment after second flight-startup failure"],
  [plugin, "UseGeneralAction(2)"],
  [plugin, "ActionType.Mount, CompanyChocoboMountId"],
  [plugin, "InFlight was lost before or during the long-distance flying route"],
  [policy, "SentinelMountChoice.CompanyChocobo"],
  [policy, "LongApproachStartupAction.BeginFlyingRoute"],
  [plugin, "SentinelState.ParkingSettle"],
  [plugin, "TryBeginParkingFacingSettle"],
  [plugin, "PathfindAvoidSafe(parkingSettleOrigin, parkingSettleOutwardPoint"],
  [plugin, "PathfindAvoidSafe(parkingSettleOutwardPoint, parkingSettleOrigin"],
  [policy, "CanBeginParkingFacingSettle"],
];

for (const [source, contract] of required) {
  if (!source.includes(contract))
    throw new Error(`Missing field-recovery contract: ${contract}`);
}

const forbidden = [
  "Player.Position =",
  "LocalPlayer.Position =",
  "territoryAetheryteCandidates",
  "TeleportToAlternateAetheryte",
  "No territory aetheryte exists in game data",
];
for (const contract of forbidden) {
  if (plugin.includes(contract))
    throw new Error(`Forbidden regression/direct-position contract found: ${contract}`);
}

const locateBody = plugin.match(/private void TickLocateMark\(DateTime now\)([\s\S]*?)private void TickMoveToSafePoint/)?.[1];
if (!locateBody)
  throw new Error("Could not locate TickLocateMark");
if (locateBody.includes("stateSinceUtc = now"))
  throw new Error("LocateMark still resets its state timer and can loop forever");
if (!locateBody.includes("CurrentLocateSearchBudgetSeconds"))
  throw new Error("LocateMark has no cumulative search budget");

const tagBody = plugin.match(/private void TickTagApproach\(DateTime now\)([\s\S]*?)private void TickGroundRetreat/)?.[1];
if (!tagBody || tagBody.includes('SetState(SentinelState.SafeWait, "Mark lost during tag approach'))
  throw new Error("TagApproach can still silently clear to ordinary waiting when the entity is temporarily missing");

const failCurrentBody = plugin.match(/private void FailCurrent\([\s\S]*?\r?\n    }\r?\n\r?\n    private void ClearCurrent/)?.[0];
if (!failCurrentBody?.includes("TryBlockAutomaticHuntExit(source, state, reason)"))
  throw new Error("FailCurrent bypasses the exact live-entity exit veto");

const resetBody = plugin.match(/private void TickResetToUldah\(DateTime now\)([\s\S]*?)private void TickReturnLandingRecovery/)?.[1];
if (!resetBody?.includes("TryBlockAutomaticHuntExit"))
  throw new Error("ResetToUldah can submit Teleport/Return without rechecking the exact live entity");

if (!plugin.includes('FailCurrent("Current hunt skipped manually", HuntExitRequestSource.ManualSkip)'))
  throw new Error("Manual Skip is no longer explicitly authorized through the live-entity veto");

const prepareApproachBody = plugin.match(/private void TickPrepareApproachDestination\(DateTime now\)([\s\S]*?)private void TickApproachAlertCoordinates/)?.[1];
if (!prepareApproachBody?.includes("EnsureLongApproachMovementReady(now, out var useFlight)") ||
    !prepareApproachBody.includes("TryStartApproachRoute(now, useFlight)"))
  throw new Error("Initial local approach bypasses the confirmed-flight startup contract");

const longApproachBody = plugin.match(/private bool EnsureLongApproachMovementReady\(DateTime now, out bool useFlight\)([\s\S]*?)private string DescribeConfirmedMount/)?.[1];
if (!longApproachBody?.includes("condition[ConditionFlag.InFlight]") ||
    !longApproachBody.includes("FailCurrent(reason, HuntExitRequestSource.RecoveryBudget)"))
  throw new Error("Long-distance approach does not require confirmed flight with bounded failure");

const startRouteBody = plugin.match(/private bool TryStartApproachRoute\(DateTime now, bool useFlight\)([\s\S]*?)private void PrepareApproachRouteCandidates/)?.[1];
if (!startRouteBody?.includes("PathfindSafe(player, target, useFlight)"))
  throw new Error("Approach pathfinding ignores the confirmed flight/ground mode");

const settleBody = plugin.match(/private void TickParkingSettle\(DateTime now\)([\s\S]*?)private void TickSafeWait/)?.[1];
if (!settleBody?.includes("BeginTagRequiredRecovery") ||
    !settleBody.includes("FinishParkingFacingSettle"))
  throw new Error("Post-landing facing settle does not yield to tagging or fail open to SafeWait");
if (settleBody.includes("Player.Rotation =") ||
    settleBody.includes("LocalPlayer.Rotation =") ||
    settleBody.includes("Player.Position =") ||
    settleBody.includes("LocalPlayer.Position ="))
  throw new Error("Post-landing facing settle contains a direct rotation/position write");

console.log("Field-recovery audit passed: latched tag, live-entity exit veto, robust Raise, bounded recovery, preferred parking, bounded natural facing settle, Company Chocobo fallback, confirmed-flight startup, and v0.7.34 travel guard are present.");
