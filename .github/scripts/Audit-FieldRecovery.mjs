import { readFileSync } from "node:fs";

const [pluginPath, combatPath, policyPath, projectPath] = process.argv.slice(2);
if (!pluginPath || !combatPath || !policyPath || !projectPath) {
  throw new Error("Usage: node Audit-FieldRecovery.mjs <Plugin.cs> <CombatController.cs> <HuntProgressPolicy.cs> <project>");
}

// Recovery UI and IPC now call the same extracted actions; include that partial in the veto audit.
const plugin = readFileSync(pluginPath, "utf8") + readFileSync("Plugin.Ui.cs", "utf8") + readFileSync("Plugin.RemoteRecovery.cs", "utf8");
const combat = readFileSync(combatPath, "utf8");
const policy = readFileSync(policyPath, "utf8");
const project = readFileSync(projectPath, "utf8");

const required = [
  [project, "<Version>0.7.74.0</Version>"],
  [plugin, "tagCycle.TryLatch"],
  [plugin, "BeginTagRequiredRecovery"],
  [plugin, "parking is suspended until the current checkpoint is confirmed"],
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
  [plugin, "ParkingLandingHorizontalTolerance = 2f"],
  [policy, "CanEnterParkingLandingHandoff"],
  [policy, "DecideBlockedLiveEntityRecovery"],
  [plugin, "resuming dynamic parking rather than entering SafeWait while mounted/flying"],
  [plugin, "TryBeginParkingFacingSettle"],
  [plugin, "PathfindAvoidSafe(parkingSettleOrigin, parkingSettleOutwardPoint"],
  [plugin, "PathfindAvoidSafe(parkingSettleOutwardPoint, parkingSettleOrigin"],
  [policy, "CanBeginParkingFacingSettle"],
  [plugin, "RestorePersistentQueueOnFrameworkThread"],
  [plugin, "persistent queue restoration is deferred to the first main-thread framework tick"],
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

const constructorBody = plugin.match(/public Plugin\([\s\S]*?\r?\n    }\r?\n\r?\n    public void Dispose/)?.[0];
if (!constructorBody)
  throw new Error("Could not locate the Plugin constructor");
if (constructorBody.includes("RestorePersistentQueue();"))
  throw new Error("Plugin construction restores the queue off the main framework thread");

const frameworkBody = plugin.match(/private void OnFrameworkUpdate\(IFramework _\)([\s\S]*?)private void RestorePersistentQueueOnFrameworkThread/)?.[1];
if (!frameworkBody?.includes("RestorePersistentQueueOnFrameworkThread();"))
  throw new Error("Framework update does not perform deferred persistent-queue restoration");

const queueRestoreBody = plugin.match(/private void RestorePersistentQueueOnFrameworkThread\(\)([\s\S]*?)private void Tick\(DateTime now\)/)?.[1];
if (!queueRestoreBody?.includes("RestorePersistentQueue();") ||
    !queueRestoreBody.includes("persistentQueueRestorePending = false;"))
  throw new Error("Deferred persistent-queue restoration is incomplete");

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

console.log("Field-recovery audit passed: main-thread queue restoration, latched tag, live-entity exit veto, robust Raise, bounded recovery, preferred parking, bounded natural facing settle, Company Chocobo fallback, confirmed-flight startup, and v0.7.34 travel guard are present.");

const instance = readFileSync("Plugin.InstanceChange.cs", "utf8");
const instancePolicy = readFileSync("InstanceChangeRecovery.cs", "utf8");
if (/FailCurrent\(|travel\.Teleport\(|EnsureMounted\(|TryStartApproachRoute\(/.test(instance))
  throw new Error("Local instance recovery must not abandon/reroute/teleport/mount for the hunt");
for (const hook of ["StopInstanceRangeMovement();", "InstanceChangeRecovery.IsLocalPath(",
  "travel.SelectionMenuIsOpen()", "travel.InstanceTargetIsSelected(instanceAetheryte)",
  "travel.InteractWithInstanceAetheryte(target, instanceRecovery.InteractionRange)"]) {
  if (!instance.includes(hook)) throw new Error("Missing instance recovery boundary: " + hook);
}
for (const method of ["TickChangeInstance", "TickSelectInstance", "TickWaitForInstance"]) {
  if (!plugin.includes(method + "(DateTime now) => TickInstanceChange(now);"))
    throw new Error("Instance phase bypasses bounded recovery: " + method);
}
if (!instancePolicy.includes("MaximumFailures = 3") ||
    !instancePolicy.includes("return length <= 25f") ||
    !instance.includes("vnav.MovePathSafe(path, false)"))
  throw new Error("Local instance recovery lost its retry/path/ground bounds");
console.log("Local instance interaction recovery audit passed.");

const worldTravel = readFileSync("Plugin.WorldVisit.cs", "utf8");
const worldData = readFileSync("NativeTravel.WorldVisit.cs", "utf8");
const nativeTravel = readFileSync("NativeTravel.cs", "utf8");
const lifestream = readFileSync("LifestreamIpc.cs", "utf8");
if (/WorldTravelSelect|SelectLogicalDataCenter|StepWorldVisitMenu|CloseWorldVisitMenus/.test(
    plugin + worldTravel + worldData + nativeTravel))
  throw new Error("Native World Visit menu parsing is still reachable.");
if (!plugin.includes("!config.Enabled || !CanRunHuntTick(now)") ||
    !worldTravel.includes("vnav.StopOwnedMovementSafe(") ||
    !worldTravel.includes("CompleteWorldVisit(target.Name)") ||
    !lifestream.includes('"Lifestream.ChangeWorld"') || !lifestream.includes('"Lifestream.IsBusy"'))
  throw new Error("Required Lifestream ownership or confirmed-arrival handoff is missing.");
console.log("Lifestream owns World travel; the native World Visit parser is removed.");

if (!tagBody?.includes("ActionSequenceWasHandled") || !tagBody.includes("tagCycle.ConfirmHandled") ||
    !tagBody.includes("tagCycle.CanDispatch") || !tagBody.includes("BeginGroundRetreat(mark, postTag: true)") ||
    !plugin.includes("IsQueuedAlertFresh") || !plugin.includes("tagCycle.ObserveRevival"))
  throw new Error("Checkpoint confirmation/retreat, Raise, or DC queue gates are missing.");

// Ground tag recovery is isolated from travel and from checkpoint completion.
const tagGround = readFileSync('Plugin.TagGroundApproach.cs', 'utf8');
const tagGroundPolicy = readFileSync('TagGroundRouteRecovery.cs', 'utf8');
for (const hook of [
  'TickTagGroundApproach(mark, clearance, now);',
  '!HoldLiveTagGroundRecovery(mark)',
  'tagGroundRoute.Reset();',
]) {
  if (!plugin.includes(hook)) throw new Error('Missing tag ground recovery boundary: ' + hook);
}
for (const hook of ['vnav.PathfindSafe(from, to, false)', 'vnav.MovePathSafe(path, false)',
  'tagCycle.CanDispatch(IsExactCheckpointMark(target)', 'CombatController.IsMarkInCombat(target)']) {
  if (!tagGround.includes(hook)) throw new Error('Unsafe tag ground navigation boundary: ' + hook);
}
if (/ConfirmHandled|ResetPull|\.Consumed\s*=|FailCurrent\(|SetState\(|Lifestream|Teleport\(|EnsureMounted\(/.test(tagGround + tagGroundPolicy))
  throw new Error('Ground tag routing gained checkpoint, hunt lifetime, or travel authority');
if (!tagGroundPolicy.includes('DirectFailureLimit = 2') || !tagGroundPolicy.includes('ValidatePath(') ||
    !tagGroundPolicy.includes('failed.Any(') || !tagGroundPolicy.includes('now.AddSeconds(RetrySeconds)'))
  throw new Error('Ground tag recovery lost its direct-first, path-validation or failure bounds');
console.log('Ground tag approach audit passed: direct-first, ground-only, failed endpoint memory, live checkpoint retention, and no travel authority.');

const ssWatch = readFileSync('Plugin.SsWatch.cs', 'utf8');
const ssLifetime = readFileSync('SsWatchLifetime.cs', 'utf8');
if (/ssWatchDeadlineUtc\s*=\s*DateTime\.MaxValue|timeout disabled|disabling the precursor timeout/.test(plugin + ssWatch))
  throw new Error('An SS announcement can still disable its watchdog');
if (!ssLifetime.includes('MaximumSeconds = 300') || !ssWatch.includes('ssWatchLifetime.Repair(ssWatchDeadlineUtc)'))
  throw new Error('SS watch lacks a hard five-minute bound or invalid deadline repair');
const ssTick = plugin.slice(plugin.indexOf('private void Tick(DateTime now)'), plugin.indexOf('private void TickResetToUldah'));
if (!ssTick.includes('TickSsReservationWatchdog(now)') ||
    ssTick.indexOf('TickSsReservationWatchdog(now)') > ssTick.indexOf('if (combat.IsPlayerDead)'))
  throw new Error('SS watchdog can be bypassed by death/other hunt state waits');
for (const hook of ['RemoveSsOpportunityDuplicates(opportunity)', 'StartSsAlertDirect(opportunity with',
  'StartAlert(opportunity with', 'ConfirmKill(reason)', 'AppendSsWatchCountdown', 'IsInSsOpportunityContext']) {
  if (!ssWatch.includes(hook)) throw new Error('Missing SS watch lifecycle boundary: ' + hook);
}
if (!plugin.includes('SsWatchLifetime.Announce(reserved)') || !plugin.includes('SsWatchLifetime.Announce(alert)') ||
    !plugin.includes('SsWatchLifetime.IsFresh(alert.ReceivedAtUtc, now, config.SsChainTimeoutSeconds)'))
  throw new Error('Promoted or queued SS report can lose the original finite reservation');
console.log('SS watchdog audit passed: five-minute hard maximum, first-evidence clock, finite countdown, exact SS death identity and duplicate cleanup.');

const presence = readFileSync('Plugin.NormalSPresence.cs', 'utf8');
const presencePolicy = readFileSync('OrdinarySPresenceGate.cs', 'utf8');
for (const hook of ['chat.LogMessage += OnNormalSPresenceLog', 'clientState.ZoneInit += OnNormalSZoneInit',
  'chat.LogMessage -= OnNormalSPresenceLog', 'clientState.ZoneInit -= OnNormalSZoneInit',
  'BeginNormalSPresenceCheck(now)', 'TickNormalSPresence(now)', 'MaintainNormalSPresenceReadiness()',
  'normalSPresence.Reset(alert.World, alert.TerritoryId)']) {
  if (!plugin.includes(hook)) throw new Error('Missing ordinary S arrival lifecycle hook: ' + hook);
}
for (const hook of ['message.LogMessageId', 'zone.TerritoryType.RowId, zone.Instance', 'CanActAfterRevival()',
  'contextMatches ? FindMark() : null', '!ssChainObserved', '!IsReservedSsWatch(current)',
  '!HuntCatalog.IsAnySsName', '!HuntCatalog.IsAnyPrecursorName', 'SentinelState.PostKillSsGrace',
  'HuntExitRequestSource.RecoveryBudget', 'AbandonNormalSPresence();']) {
  if (!presence.includes(hook)) throw new Error('Unsafe ordinary S presence boundary: ' + hook);
}
const absence = presence.slice(presence.indexOf('private void AbandonNormalSPresence()'),
  presence.indexOf('private void LogNormalSPresence('));
if (!absence.includes('FailCurrent(') || /ConfirmKill|MarkKilled|\.Kill\(|\.Tag\(|\.Credit\(/.test(absence))
  throw new Error('Presence absence must remain unresolved rather than positive death/credit');
if (!presencePolicy.includes('PowerfulMarkMessageId = 9331') || !presencePolicy.includes('ObservationSeconds = 5') ||
    !presencePolicy.includes('ReadyUtc.AddSeconds(ObservationSeconds)') ||
    !presencePolicy.includes('OrdinarySPresenceDecision.ArrivalUnavailable'))
  throw new Error('Presence gate lost native signal, ready clock, or conservative missed-arrival fallback');
console.log('Ordinary S presence audit passed: native zone-scoped signal, five ready seconds, SS exclusions, unresolved-only absence.');

