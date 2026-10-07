import { readFileSync, readdirSync } from "node:fs";
import { createHash } from "node:crypto";
import { join } from "node:path";

const read = path => readFileSync(path, "utf8").replace(/\r/g, "");
const assert = (condition, message) => { if (!condition) throw new Error(message); };
const project = read("SRankSentinel.csproj");
const pin = JSON.parse(read("sentinelcore-packages.json"));
const locked = JSON.parse(read("packages.lock.json")).dependencies["net10.0-windows7.0"];
assert(pin.version === "0.4.1" && pin.release === "v0.4.1.0" &&
  pin.commit === "e6527400680cddc39803321792b7c3b02fb0501d", "Wrong Core release pin.");
const expected = {
  "MarshalTitan.SentinelCore": "10b14a37577bd78b528d9bad03848b5cb5c3f6c3f4fa7dff4de966f8e5b34771",
  "MarshalTitan.SentinelCore.UI": "1ec7841311316994d4f71350b499c3f718d36179e1565f0d7316270063c6baea"
};
assert(pin.packages.length === 2, "Only Core and Core.UI are required.");
for (const [id, hash] of Object.entries(expected)) {
  const packagePin = pin.packages.find(p => p.id === id);
  assert(packagePin?.sha256 === hash && packagePin.url ===
    `https://github.com/MarshalTitan/SentinelCore/releases/download/v0.4.1.0/${id}.0.4.1.nupkg`,
    `Wrong published package/hash: ${id}`);
  assert(project.includes(`<PackageReference Include="${id}" Version="[0.4.1]" />`),
    `Reference must pin exact published version: ${id}`);
  assert(locked[id]?.resolved === "0.4.1" && locked[id]?.requested === "[0.4.1, 0.4.1]",
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
assert(ui.includes("config.ModernWindowCollapsed ? headerHeight : 520f") && ui.includes("new Vector2(620f,") &&
  !/HeaderHeight\s*=|Layout\s*=|AllowStackedNavigation|CompactBreakpoint|IsCompact/.test(ui),
  "Use Core 0.4.1's default non-stacking application layout, with a 620x520 expanded minimum.");
for (const helper of ["DrawEnabledControl", "DrawExpansionControls", "DrawDistanceControls",
  "DrawRecoveryControls"]) {
  assert(ui.split(helper + "();").length >= 3, `Themes must share ${helper}`);
}
assert(!/AddRect|AddCircle|DrawRings|new Vector4/.test(ui),
  "The consumer must not draw canonical Modern primitives.");
assert(ui.includes('"SRankSentinel.SwitchToClassic", "Use Classic Theme"') &&
  ui.includes('drawActionDock: !config.ModernWindowCollapsed && config.WindowPage == (int)ConfigurationPage.Theme') &&
  !ui.includes('CreateModernNavItem("Classic"') && ui.includes('ImGui.Combo("Window theme"'),
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

const baseline = JSON.parse(read("docs/sentinel-modern-hunt-baseline.json"));
for (const [path, expectedHash] of Object.entries(baseline.sha256)) {
  let source = read(path);
  if (path === "Plugin.cs") {
    // Phase 3 permits only these exact opt-in hooks over the accepted legacy algorithm.
    // Remove each with an exact occurrence count before the unchanged hunt hash comparison.
    for (const [hook, count] of [
      ["            if (TickNavigationProvingParking(now))\n                return;\n\n", 1],
      ["        if (!CancelNavigationProvingParkingForPolicy())\n            return true; // Proving halted; do not let callers launch another route.\n", 1],
      ["        RegisterNavigationProving();\n", 1],
      ["        DisposeNavigationProving();\n", 1],
      ["            TickNavigationProvingControl();\n", 1],
      ["        CancelNavigationProvingOperation();\n", 1],
      ["        NavigationProvingStateHandoff(next);\n", 1],
      ["        if (TickNavigationProvingApproach())\n            return;\n", 2],
      ["        if (NavigationProvingWaitForMesh())\n            return;\n", 1],
    ]) {
      assert(source.split(hook).length === count + 1, `Missing/duplicated proving hook: ${hook.trim()}`);
      source = source.split(hook).join("");
    }
    const parkingMoveHook = "        var sharedParking = TryStartNavigationProvingParking(target, candidate, path);\n        if (sharedParking is false)\n            return;\n        if (sharedParking is null && !vnav.MovePathSafe(path, parkingPathUsesFlight))";
    assert(source.split(parkingMoveHook).length === 2, "Missing/duplicated approved parking owner hook");
    source = source.replace(parkingMoveHook, "        if (!vnav.MovePathSafe(path, parkingPathUsesFlight))");
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
  assert(actual === expectedHash, `Legacy hunt source changed beyond explicit proving/history hooks: ${path}`);
}
for (const workflow of ["build.yml", "release.yml"]) {
  const content = read(join(".github/workflows", workflow));
  for (const check of ["Audit-SentinelModern.mjs", "Prepare-SentinelCore.ps1",
    "-AssetsPath", "SRankSentinel.UiTests", "Validate-Package.ps1"]) {
    assert(content.includes(check), `${workflow} must enforce ${check}`);
  }
}
console.log("Sentinel Modern 2 audit passed: exact Core 0.4.1 packages, shared shell, Plugins/History pages, separate Classic dock button, navigation tooltip input, migration/placement, and unchanged hunt sources after exact passive observer insertions.");
