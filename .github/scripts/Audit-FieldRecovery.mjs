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
  [project, "<Version>0.7.39.0</Version>"],
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

console.log("Field-recovery audit passed: latched tag, live-entity exit veto, robust Raise, bounded aggro/search/projection, preferred parking, and v0.7.34 travel guard are present.");
