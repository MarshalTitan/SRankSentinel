import { readFileSync } from "node:fs";

const [pluginPath, travelPath, catalogPath, vnavPath] = process.argv.slice(2);
if (!pluginPath || !travelPath || !catalogPath || !vnavPath) {
  throw new Error("Usage: node Audit-V734TravelRestore.mjs <Plugin.cs> <NativeTravel.cs> <HuntCatalog.cs> <VNavmeshIpc.cs>");
}

const plugin = readFileSync(pluginPath, "utf8");
const travel = readFileSync(travelPath, "utf8");
const catalog = readFileSync(catalogPath, "utf8");
const vnav = readFileSync(vnavPath, "utf8");

const required = [
  [plugin, "travel.CanTeleportTo(row.RowId)"],
  [plugin, "current.PreferredAetheryteId"],
  [plugin, "DeadPostKillRewardGraceSeconds = 2"],
  [plugin, "Current hunt confirmed dead — waiting {Seconds:0.0}s"],
  [plugin, "HuntCatalog.IsChernobog(territory, creature)"],
  [plugin, "U'Ghamaro Mines navigation is intentionally unsupported"],
  [plugin, "SetState(SentinelState.TeleportToTerritory"],
  [plugin, "travel.Teleport(territoryAetheryteId)"],
  [plugin, "SetState(SentinelState.WaitForTerritory"],
  [plugin, "SetState(SentinelState.PrepareApproachDestination"],
  [catalog, "public static bool IsChernobog"],
  [catalog, 'new(2965, 138, 14, "Bonnacon")'],
  [travel, "public unsafe bool CanTeleportTo(uint aetheryteId)"],
];

for (const [source, contract] of required) {
  if (!source.includes(contract))
    throw new Error(`Missing restored travel contract: ${contract}`);
}

const removedRedesignContracts = [
  [plugin, "territoryAetheryteCandidates"],
  [plugin, "TeleportToAlternateAetheryte"],
  [plugin, "ApproachProjectionPassesBeforeApproximateFlight"],
  [plugin, "No territory aetheryte exists in game data"],
  [travel, "TryGetTeleportableAetherytes"],
  [catalog, "RequiresGroundTunnelApproach"],
  [vnav, "NearestPointSafe"],
  [vnav, "CancelAllPathfindingSafe"],
];

for (const [source, contract] of removedRedesignContracts) {
  if (source.includes(contract))
    throw new Error(`Post-v0.7.34 travel redesign symbol remains: ${contract}`);
}

const worldVisitHandoff = plugin.match(
  /private void CompleteWorldVisit\(string targetWorld\)([\s\S]*?)private void FailWorldVisit/,
)?.[1];
if (!worldVisitHandoff)
  throw new Error("Could not locate the WorldVisit -> TeleportToTerritory handoff");
if (!worldVisitHandoff.includes("SetState(SentinelState.TeleportToTerritory"))
  throw new Error("World Visit no longer advances to territory Teleport");
if (worldVisitHandoff.includes("territoryAetheryteId = 0"))
  throw new Error("World Visit must not clear the destination resolved before travel");

console.log("Restored v0.7.34 travel contract audit passed; Chernobog blacklist and 2-second reward grace retained.");
