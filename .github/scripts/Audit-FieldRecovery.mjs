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
  [project, "<Version>0.7.38.0</Version>"],
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

console.log("Field-recovery audit passed: latched tag, robust Raise, bounded aggro/search/projection, preferred parking, and v0.7.34 travel guard are present.");
