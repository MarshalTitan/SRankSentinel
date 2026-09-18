import { readFileSync } from "node:fs";

const [pluginPath, travelPath] = process.argv.slice(2);
if (!pluginPath || !travelPath) {
  throw new Error("Usage: node Audit-TeleportReadiness.mjs <Plugin.cs> <NativeTravel.cs>");
}

const plugin = readFileSync(pluginPath, "utf8");
const travel = readFileSync(travelPath, "utf8");

const requiredPluginContracts = [
  "PostWorldVisitMinimumSettleSeconds",
  "PostWorldVisitTeleportListSettleSeconds",
  "TerritoryAetheryteResolution.NativeListUnready",
  "staticCandidates.Where(row => teleportableAetherytes.Contains(row.RowId))",
  "territoryAetheryteSelectedWithoutNativeList = !nativeListUsableForTerritory",
  "travel.Teleport(territoryAetheryteId, territoryAetheryteSelectedWithoutNativeList)",
  "Destination world confirmed",
];

for (const contract of requiredPluginContracts) {
  if (!plugin.includes(contract))
    throw new Error(`Missing post-World-Visit readiness contract: ${contract}`);
}

if (plugin.includes(".Where(row => travel.CanTeleportTo(row.RowId))")) {
  throw new Error("Territory candidates must not be erased by per-row native-list checks");
}

const requiredTravelContracts = [
  "TryGetTeleportableAetherytes(out HashSet<uint> aetheryteIds)",
  "return aetheryteIds.Count > 0",
  "bool allowUnreadyTeleportList = false",
  "!allowUnreadyTeleportList",
  "return telepo->Teleport(aetheryteId, 0)",
];

for (const contract of requiredTravelContracts) {
  if (!travel.includes(contract))
    throw new Error(`Missing native Teleport readiness contract: ${contract}`);
}

console.log("Post-World-Visit Teleport readiness recovery audit passed (empty/stale native-list fallback preserved).");
