import { readFileSync, readdirSync } from "node:fs";
import { createHash } from "node:crypto";
import { join } from "node:path";

const read = path => readFileSync(path, "utf8").replace(/\r/g, "");
const assert = (condition, message) => { if (!condition) throw new Error(message); };
const project = read("SRankSentinel.csproj");
const pin = JSON.parse(read("sentinelcore-packages.json"));
const locked = JSON.parse(read("packages.lock.json")).dependencies["net10.0-windows7.0"];
assert(pin.version === "0.3.1" && pin.release === "v0.3.1.0" &&
  pin.commit === "300703b360a58fb4b73bf7675d31fe8cab4614cd", "Wrong Core release pin.");
const expected = {
  "MarshalTitan.SentinelCore": "acdbde4e83c3fef325f9a134a5697c2ab87fed1e00cabd3ceff904ba407a4d59",
  "MarshalTitan.SentinelCore.UI": "e1a9ce4e1ce36042c0fcd53f4c23874d918640be10eef16c21f1cd436c6ba747"
};
assert(pin.packages.length === 2, "Only Core and Core.UI are required.");
for (const [id, hash] of Object.entries(expected)) {
  const packagePin = pin.packages.find(p => p.id === id);
  assert(packagePin?.sha256 === hash && packagePin.url ===
    `https://github.com/MarshalTitan/SentinelCore/releases/download/v0.3.1.0/${id}.0.3.1.nupkg`,
    `Wrong published package/hash: ${id}`);
  assert(project.includes(`<PackageReference Include="${id}" Version="[0.3.1]" />`),
    `Reference must pin exact published version: ${id}`);
  assert(locked[id]?.resolved === "0.3.1" && locked[id]?.requested === "[0.3.1, 0.3.1]",
    `Dependency lock must pin exact published version: ${id}`);
}
assert(!project.includes("SentinelCore.Dalamud") && !project.includes("<ProjectReference"),
  "Consumer must use published packages only.");
assert(project.includes("<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>"),
  "Shared runtime assemblies must be bundled.");

function sourceFiles(directory) {
  return readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    if (["bin", "obj", ".git", ".sentinelcore-packages"].includes(entry.name)) return [];
    const path = join(directory, entry.name);
    return entry.isDirectory() ? sourceFiles(path) : entry.name.endsWith(".cs") ? [path] : [];
  });
}
const sources = sourceFiles(".").map(p => [p, read(p)]);
for (const [path, source] of sources) {
  assert(!/namespace\s+SentinelCore/.test(source), `Local Core namespace: ${path}`);
  assert(!/(?:class|struct|record)\s+(?:SentinelModern\w+|SentinelPalette|SentinelStyleScope)/.test(source),
    `Local canonical primitive: ${path}`);
}
const ui = read("Plugin.Ui.cs");
for (const api of ["PushAppShell", "SentinelModernWindowChrome.UseCustomHeader", "SentinelModernAppShell",
  "SentinelModernNavItem", "SentinelModernGlassCard", "SentinelModernSettingsRow", "SentinelModernSwitch",
  "SentinelModernStatusPillOptions", "SentinelModernAppSurfaceStyle.Unified", "AmbientIntensity = 0.9f"]) {
  assert(ui.includes(api), `Shared UI component missing: ${api}`);
}
assert(ui.includes('ConfigurationWindowId = "S Rank Sentinel###SRankSentinel"'),
  "Saved window identity changed.");
assert(ui.includes("new Vector2(680, 720), ImGuiCond.FirstUseEver") &&
  !ui.includes("SetNextWindowPos") && !ui.includes("NoNav"), "Classic size/position or standard window controls regressed.");
assert(ui.includes("config.ModernWindowCollapsed ? headerHeight : ModernMinimumWindowSize.Y") &&
  ui.includes("Vector2.Max(new Vector2(480f, 520f), SentinelModernAppLayout.MinimumWindowSize(hasActionDock: true))") &&
  !/HeaderHeight\s*=|Layout\s*=|AllowStackedNavigation|CompactBreakpoint|IsCompact/.test(ui),
  "Use Core 0.3.1's default non-stacking application layout, with a Core-audited 480x520 expanded minimum.");
for (const helper of ["DrawEnabledControl", "DrawExpansionControls", "DrawDistanceControls",
  "DrawRecoveryControls"]) {
  assert(ui.split(helper + "();").length >= 3, `Themes must share ${helper}`);
}
assert(!/AddRect|AddCircle|DrawRings|new Vector4/.test(ui),
  "The consumer must not draw canonical Modern primitives.");
assert(ui.includes('"SRankSentinel.SwitchToClassic", "Use Classic Theme"') &&
  ui.includes('drawActionDock: !config.ModernWindowCollapsed && config.WindowPage == (int)ConfigurationPage.Theme') &&
  !ui.includes('CreateModernNavItem("Classic"') && ui.includes('ImGui.Combo("##WindowTheme"'),
  "Modern must have a separate bottom-left Core dock button; Classic must retain its theme selector.");
assert(ui.includes('ImGui.GetIO().NavVisible && ImGui.IsItemFocused()') &&
  ui.includes('ConfigurationPage.Plugins.ToString()') && ui.includes('ConfigurationPage.History.ToString()'),
  "Navigation needs the new pages and must not retain mouse-click focus tooltips.");
assert(ui.includes('"Plugins", FontAwesomeIcon.Plug') && ui.includes('"History", FontAwesomeIcon.History') &&
  ui.includes('new Vector2(170f, 34f) * ImGuiHelpers.GlobalScale'),
  "Keep the ecosystem Plug icon, existing History icon, and compact Classic button.");
const companionPages = read("Plugin.CompanionPages.cs");
assert(companionPages.includes('SentinelModernGlassCard.Begin("Companion."') &&
  companionPages.includes('loaded ? SentinelModernPillTone.Enabled : SentinelModernPillTone.Error') &&
  companionPages.includes('Accent = colour, AccentStrength = 0.65f') &&
  ui.includes('"Theme", FontAwesomeIcon.Palette') &&
  companionPages.includes('"History.Clear", "Clear history"') && companionPages.includes('ObserveHistory(History.ClearAll)') &&
  companionPages.includes('History.VisibleReports()') && !companionPages.includes('TAGGED + CREDIT CONFIRMED') &&
  companionPages.includes('SentinelModernGlassCard.Begin("History.Metric."') &&
  companionPages.includes('SentinelModernGlassCard.Begin("History.Activity"') &&
  !/AddRect|AddCircle|new Vector4/.test(companionPages),
  "Plugin and history content must consume canonical Core cards and controls.");
const historyActivity = read("HuntHistoryActivity.cs");
assert(historyActivity.includes('HuntHistoryActivityDay[] BuildWeek') &&
  historyActivity.includes('SentinelModernPalette.Accent') && historyActivity.includes('SentinelModernPalette.Teal') &&
  historyActivity.includes('day.Untagged') && historyActivity.includes('day.Tagged') &&
  !/new Vector4|AddCircle|GlassCard\(|PushStyle/.test(historyActivity),
  "The consumer data chart may draw segments only, using shared Core colours and enclosing cards.");
const journal = read("HuntHistoryJournal.cs");
const historyObserver = read("Plugin.History.cs");
assert(journal.includes('config.IsExpansionEnabled(HuntCatalog.GetExpansion(alert.TerritoryId))') &&
  journal.includes('config.IsExpansionEnabled(HuntCatalog.GetExpansion(entry.TerritoryId))') &&
  historyObserver.includes('!config.Enabled || !config.IsExpansionEnabled(HuntCatalog.GetExpansion(territory))'),
  "History recording and display must follow the existing expansion filters.");
assert(!ui.includes("SentinelModernConfigurationShell") && !ui.includes("ConsumerToggleActivation") &&
  !ui.includes("SentinelModernUi.PageHeading"), "Legacy shell/bridge or redundant page-top information returned.");
const config = read("Configuration.cs");
assert(config.includes("if (Version < 16)") && config.includes("WindowTheme = 0;") &&
  config.includes("WindowPage = 0;") && config.includes("MigrateWindowAppearance();") &&
  config.includes("NormalizeTheme(WindowTheme)"), "Explicit Classic migration/normalization missing.");


// These exact navigation edits are reviewed independently of the UI migration.
// Undo only their literal source blocks before comparing the original hunt baseline.
const reviewedRoutingPatches = {
  "Plugin.cs": [
    ["        // Heritage Found's linked Level rows are absent in some game-data snapshots.\n        // Its map markers still locate all three crystals; use those when normal\n        // world-position selection cannot produce a candidate.\n        if (territoryAetheryteId == 0 && current.TerritoryId == 1191 &&\n            HasUsableMapCoordinates(current))\n        {\n            territoryAetheryteId = HuntProgressPolicy.SelectHeritageFoundAetheryte(\n                current.MapX, current.MapY, attuned.Select(row => row.RowId).ToArray());\n            if (territoryAetheryteId != 0)\n                log.Information(\"Selected Heritage Found map-marker aetheryte {Aetheryte} for X{MapX:0.0} Y{MapY:0.0}\",\n                    territoryAetheryteId, current.MapX, current.MapY);\n        }\n\n", ""],
    ["        if (visible.IsDead || visible.CurrentHp == 0)\n        {\n            mark = visible;\n            MarkWasIdentified(visible);\n            ConfirmKill($\"{visible.Name.TextValue} became visibly dead during local approach recovery\");\n            return true;\n        }\n\n        // Object-table visibility can extend across the whole zone. A distant mark\n        // must not bypass the long-approach mount/takeoff gate and start parking on foot.\n        var scanRange = Math.Max(ActiveDistanceProfile.FlagApproachDistance,\n            ActiveDistanceProfile.WaitingDistance);\n        if (HuntProgressPolicy.ShouldDeferVisibleMarkDuringApproach(\n                current.TerritoryId, HorizontalDistance(PlayerPosition(), visible.Position),\n                scanRange, ApproachScanTolerance))\n            return false;\n\n        mark = visible;\n        MarkWasIdentified(visible);\n", "        mark = visible;\n        MarkWasIdentified(visible);\n        if (visible.IsDead || visible.CurrentHp == 0)\n        {\n            ConfirmKill($\"{visible.Name.TextValue} became visibly dead during local approach recovery\");\n            return true;\n        }\n\n"],
  ],
  "HuntProgressPolicy.cs": [
    ["    // MapMarker subrows 626.0-626.2 on Heritage Found's map (861), keyed by\n    // Aetheryte IDs 210, 212, and 211. Map coordinates use the game's 41-unit\n    // scale over a 2048-pixel marker texture for this 100% scale map.\n    private static readonly (uint Id, float X, float Y)[] HeritageFoundMarkers =\n    [\n        (210, 1539f, 1231f),\n        (211, 802f, 441f),\n        (212, 805f, 1145f),\n    ];\n\n    public static uint SelectHeritageFoundAetheryte(\n        float mapX, float mapY, IReadOnlyCollection<uint> usableAetheryteIds)\n    {\n        if (mapX <= 0f || mapY <= 0f)\n            return 0;\n\n        var markerX = (mapX - 1f) * 2048f / 41f;\n        var markerY = (mapY - 1f) * 2048f / 41f;\n        return HeritageFoundMarkers\n            .Where(marker => usableAetheryteIds.Contains(marker.Id))\n            .OrderBy(marker => (marker.X - markerX) * (marker.X - markerX) +\n                               (marker.Y - markerY) * (marker.Y - markerY))\n            .Select(marker => marker.Id)\n            .FirstOrDefault();\n    }\n\n    public static bool ShouldDeferVisibleMarkDuringApproach(\n        uint territoryId, float horizontalDistance, float scanRange, float tolerance) =>\n        territoryId == 1191 && horizontalDistance > scanRange + tolerance;\n\n", ""],
  ],
};
const baseline = JSON.parse(read("docs/sentinel-modern-hunt-baseline.json"));
for (const [path, expectedHash] of Object.entries(baseline.sha256)) {
  let source = read(path);
  for (const [reviewedChange, baselineText] of reviewedRoutingPatches[path] ?? []) {
    assert(source.split(reviewedChange).length === 2,
      `Missing or duplicated reviewed routing change: ${path}`);
    source = source.replace(reviewedChange, baselineText);
  }
  if (path === "Plugin.cs") {
    // Only these exact passive observer insertions are allowed over the unchanged hunt baseline.
    for (const hook of [
      "        chat.ChatMessage += OnHistoryReward;\n",
      "        chat.ChatMessage -= OnHistoryReward;\n",
      "        ObserveHistorySpawn(huntType, world, creature, territory, instance, source, occurredAtUtc);\n",
      "                ObserveHistoryTag(current, now);\n",
      "        ObserveHistoryKill(current, pullCycleTagged, now);\n",
    ]) {
      assert(source.split(hook).length === 2, `Missing or duplicated passive history hook: ${hook.trim()}`);
      source = source.replace(hook, "");
    }
  }
  const actual = createHash("sha256").update(source.trim() + "\n").digest("hex");
  assert(actual === expectedHash, `UI-only migration changed hunt source: ${path}`);
}
for (const workflow of ["build.yml", "release.yml"]) {
  const content = read(join(".github/workflows", workflow));
  for (const check of ["Audit-SentinelModern.mjs", "Prepare-SentinelCore.ps1",
    "-AssetsPath", "SRankSentinel.UiTests", "Validate-Package.ps1"]) {
    assert(content.includes(check), `${workflow} must enforce ${check}`);
  }
}
console.log("Sentinel Modern 2 audit passed: exact Core 0.3.1 packages, shared UI, passive history hooks, and baseline hunt sources after removing exact reviewed routing changes.");
