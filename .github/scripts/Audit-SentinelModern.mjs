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
    ["        // Some game-data snapshots omit usable linked Level positions. Prefer\n        // the closest attuned crystal on the territory map before the fixed fallback.\n        if (territoryAetheryteId == 0 && HasUsableMapCoordinates(current) &&\n            !TerritoryAetheryteOverrides.ContainsKey(current.TerritoryId))\n        {\n            territoryAetheryteId = HuntProgressPolicy.SelectMapMarkerAetheryte(\n                current.TerritoryId, current.MapX, current.MapY,\n                attuned.Select(row => row.RowId).ToArray());\n            if (territoryAetheryteId != 0)\n                log.Information(\"Selected map-marker aetheryte {Aetheryte} for territory {Territory}, X{MapX:0.0} Y{MapY:0.0}\",\n                    territoryAetheryteId, current.TerritoryId, current.MapX, current.MapY);\n        }\n\n", ""],
    ["        if (visible.IsDead || visible.CurrentHp == 0)\n        {\n            mark = visible;\n            MarkWasIdentified(visible);\n            ConfirmKill($\"{visible.Name.TextValue} became visibly dead during local approach recovery\");\n            return true;\n        }\n\n        // Object-table visibility can extend across the whole zone. A distant mark\n        // must not bypass the long-approach mount/takeoff gate and start parking on foot.\n        var scanRange = Math.Max(ActiveDistanceProfile.FlagApproachDistance,\n            ActiveDistanceProfile.WaitingDistance);\n        if (HuntProgressPolicy.ShouldDeferVisibleMarkDuringApproach(\n                HorizontalDistance(PlayerPosition(), visible.Position),\n                scanRange, ApproachScanTolerance))\n            return false;\n\n        mark = visible;\n        MarkWasIdentified(visible);\n", "        mark = visible;\n        MarkWasIdentified(visible);\n        if (visible.IsDead || visible.CurrentHp == 0)\n        {\n            ConfirmKill($\"{visible.Name.TextValue} became visibly dead during local approach recovery\");\n            return true;\n        }\n\n"],
  ],
  "HuntProgressPolicy.cs": [
    ["    // Game Map and MapMarker data: territory maps and aetheryte marker subrows.\n    // Used only if the normal linked Level positions yield no usable crystal.\n    private static readonly IReadOnlyDictionary<uint, (float Scale, (uint Id, float X, float Y)[] Markers)> AetheryteMapMarkers =\n        new Dictionary<uint, (float Scale, (uint Id, float X, float Y)[] Markers)>\n        {\n            [134] = (100f, [(52u, 1255f, 767f)]),\n            [135] = (100f, [(10u, 1180f, 1697f)]),\n            [137] = (100f, [(11u, 1514f, 1491f), (12u, 1005f, 1027f)]),\n            [138] = (100f, [(13u, 1676f, 1537f), (14u, 1284f, 1242f)]),\n            [139] = (100f, [(15u, 1462f, 1118f)]),\n            [140] = (100f, [(17u, 1092f, 795f)]),\n            [141] = (100f, [(53u, 1004f, 857f)]),\n            [145] = (100f, [(18u, 637f, 1166f)]),\n            [146] = (100f, [(19u, 865f, 607f), (20u, 697f, 1430f)]),\n            [147] = (100f, [(21u, 1045f, 1478f), (22u, 997f, 993f)]),\n            [148] = (100f, [(3u, 1037f, 1059f)]),\n            [152] = (100f, [(4u, 838f, 1322f)]),\n            [153] = (100f, [(6u, 793f, 1380f), (5u, 1203f, 953f)]),\n            [154] = (100f, [(7u, 980f, 1252f)]),\n            [155] = (100f, [(23u, 1247f, 789f)]),\n            [156] = (100f, [(24u, 1064f, 356f)]),\n            [180] = (100f, [(16u, 907f, 811f)]),\n            [397] = (95f, [(71u, 1474f, 1697f)]),\n            [398] = (95f, [(76u, 1530f, 1052f), (77u, 734f, 1056f)]),\n            [400] = (95f, [(79u, 466f, 1322f), (78u, 1278f, 1580f)]),\n            [401] = (95f, [(73u, 450f, 626f), (72u, 442f, 1548f)]),\n            [402] = (95f, [(74u, 338f, 454f)]),\n            [612] = (100f, [(99u, 1440f, 1270f), (98u, 395f, 515f)]),\n            [613] = (100f, [(105u, 1384f, 762f), (106u, 1112f, 440f)]),\n            [614] = (100f, [(107u, 1457f, 933f), (108u, 1269f, 620f)]),\n            [620] = (100f, [(100u, 1135f, 275f), (101u, 753f, 1770f)]),\n            [621] = (100f, [(102u, 373f, 1005f), (103u, 1640f, 1675f)]),\n            [622] = (100f, [(109u, 1580f, 1363f), (110u, 1102f, 1057f), (128u, 269f, 1141f)]),\n            [813] = (100f, [(132u, 1778f, 995f), (136u, 290f, 795f)]),\n            [814] = (100f, [(137u, 1692f, 1313f), (138u, 780f, 1410f), (139u, 598f, 400f)]),\n            [815] = (100f, [(140u, 1270f, 800f), (161u, 1423f, 1332f), (141u, 513f, 811f)]),\n            [816] = (100f, [(144u, 680f, 1535f), (145u, 952f, 167f), (146u, 1405f, 337f)]),\n            [817] = (100f, [(142u, 921f, 1321f), (143u, 1405f, 830f)]),\n            [818] = (100f, [(147u, 1585f, 825f), (148u, 882f, 1242f)]),\n            [956] = (100f, [(166u, 1467f, 545f), (167u, 1032f, 975f), (168u, 294f, 1326f)]),\n            [957] = (100f, [(169u, 1220f, 1652f), (170u, 498f, 1060f), (171u, 1428f, 777f)]),\n            [958] = (100f, [(172u, 616f, 1504f), (173u, 1542f, 845f)]),\n            [959] = (100f, [(174u, 457f, 1674f), (175u, 1023f, 511f)]),\n            [960] = (100f, [(179u, 480f, 1290f), (180u, 1085f, 365f), (181u, 1515f, 1355f)]),\n            [961] = (100f, [(176u, 1182f, 1150f), (177u, 390f, 1566f), (178u, 493f, 802f)]),\n            [1187] = (100f, [(200u, 1356f, 607f), (201u, 1490f, 1659f)]),\n            [1188] = (100f, [(202u, 854f, 545f), (203u, 1565f, 1228f), (204u, 546f, 1335f), (238u, 1812f, 788f)]),\n            [1189] = (100f, [(205u, 626f, 592f), (206u, 1745f, 1550f)]),\n            [1190] = (100f, [(207u, 1403f, 1488f), (209u, 1335f, 456f), (208u, 732f, 909f)]),\n            [1191] = (100f, [(210u, 1539f, 1231f), (212u, 805f, 1145f), (211u, 802f, 441f)]),\n            [1192] = (100f, [(213u, 1024f, 1821f), (214u, 1681f, 741f), (215u, 769f, 627f)]),\n        };\n\n    public static uint SelectMapMarkerAetheryte(\n        uint territoryId, float mapX, float mapY, IReadOnlyCollection<uint> usableAetheryteIds)\n    {\n        if (mapX <= 0f || mapY <= 0f ||\n            !AetheryteMapMarkers.TryGetValue(territoryId, out var zone))\n            return 0;\n\n        var markerX = (mapX - 1f) * zone.Scale / 100f * 2048f / 41f;\n        var markerY = (mapY - 1f) * zone.Scale / 100f * 2048f / 41f;\n        return zone.Markers\n            .Where(marker => usableAetheryteIds.Contains(marker.Id))\n            .OrderBy(marker => (marker.X - markerX) * (marker.X - markerX) +\n                               (marker.Y - markerY) * (marker.Y - markerY))\n            .Select(marker => marker.Id)\n            .FirstOrDefault();\n    }\n\n    public static bool ShouldDeferVisibleMarkDuringApproach(\n        float horizontalDistance, float scanRange, float tolerance) =>\n        horizontalDistance > scanRange + tolerance;\n\n", ""],
  ],
};
// Reverse only the reviewed local instance-interaction edits before the existing hunt audit.
const reviewedInstancePatches = {
  "Plugin.cs": [
    [
      "    private void TickChangeInstance(DateTime now) => TickInstanceChange(now);\n\n    private void TickSelectInstance(DateTime now) => TickInstanceChange(now);\n\n    private void TickWaitForInstance(DateTime now) => TickInstanceChange(now);\n\n",
      "    private void TickChangeInstance(DateTime now)\n    {\n        if (current is null)\n            return;\n        if (travel.CurrentInstance == current.Instance)\n        {\n            BeginPlayerReadyWait(\"Correct instance reached\");\n            return;\n        }\n        if (TravelTimedOut(now))\n        {\n            FailCurrent($\"Could not change to instance {current.Instance}\");\n            return;\n        }\n        if (travel.SelectInstanceTravelMenu())\n        {\n            SetState(SentinelState.SelectInstance, $\"Selecting instance {current.Instance}\");\n            return;\n        }\n        if (now < nextActionUtc || travel.IsBusy)\n            return;\n        if (condition[ConditionFlag.Mounted])\n            UseGeneralAction(23);\n        else if (!travel.InteractWithNearbyAetheryte(15f) &&\n                 (now - stateSinceUtc).TotalSeconds > 4 &&\n                 territoryAetheryteId != 0 && travel.Teleport(territoryAetheryteId))\n        {\n            status = \"Repositioning normally at the territory aetheryte to change instance\";\n        }\n        nextActionUtc = now.AddSeconds(3);\n    }\n\n    private void TickSelectInstance(DateTime now)\n    {\n        if (current is null)\n            return;\n        if (TravelTimedOut(now))\n        {\n            FailCurrent($\"Instance {current.Instance} was not available\");\n            return;\n        }\n        if (travel.SelectInstance(current.Instance))\n            SetState(SentinelState.WaitForInstance, $\"Traveling to instance {current.Instance}\");\n    }\n\n    private void TickWaitForInstance(DateTime now)\n    {\n        if (current is null)\n            return;\n        if (!travel.IsBusy && clientState.TerritoryType == current.TerritoryId &&\n            travel.CurrentInstance == current.Instance)\n        {\n            BeginPlayerReadyWait(\"Correct instance reached\");\n            return;\n        }\n        if (TravelTimedOut(now))\n            FailCurrent($\"Arrival in instance {current.Instance} timed out\");\n    }\n\n"
    ],
    [
      "    private void BeginInstanceCheck()\n    {\n        ResetInstanceRangeRecovery();\n",
      "    private void BeginInstanceCheck()\n    {\n"
    ],
    [
      "        vnav.StopSafe(\"preparing local instance-change interaction\");\n        log.Information(\"Instance change required: required={Required}, current={Current}, territory={Territory}, preferredAetheryte={Aetheryte}\",\n            current.Instance, instance, current.TerritoryId, territoryAetheryteId);\n        nextActionUtc = DateTime.UtcNow;\n        SetState(SentinelState.ChangeInstance,",
      "        nextActionUtc = DateTime.UtcNow;\n        SetState(SentinelState.ChangeInstance,"
    ],
    [
      "    private void ClearCurrent()\n    {\n        ResetInstanceRangeRecovery();\n",
      "    private void ClearCurrent()\n    {\n"
    ],
    [
      "        if (IsInstanceRecoveryState(previous) && !IsInstanceRecoveryState(next))\n            ResetInstanceRangeRecovery();\n        state = next;\n        stateSinceUtc = DateTime.UtcNow;",
      "        state = next;\n        stateSinceUtc = DateTime.UtcNow;"
    ]
  ],
  "NativeTravel.cs": [
    [
      "    internal sealed record InstanceAetheryteTarget(ulong ObjectId, uint BaseId, string Name, Vector3 Position);\n\n    public InstanceAetheryteTarget? ResolveInstanceAetheryte(InstanceAetheryteTarget? intended, Vector3 arrivalOrigin)\n    {\n        if (objects.LocalPlayer is null || IsBusy)\n            return null;\n        // Pin the nearest local crystal on arrival. A reload may replace its object ID,\n        // but must retain the same data ID and location; never chase another crystal.\n        var local = objects.Where(o => o.ObjectKind == ObjectKind.Aetheryte && o.IsTargetable &&\n            Vector3.Distance(o.Position, arrivalOrigin) <= 25f);\n        var resolved = intended is null\n            ? local.OrderBy(o => Vector3.Distance(o.Position, arrivalOrigin)).FirstOrDefault()\n            : local.FirstOrDefault(o => o.GameObjectId == intended.ObjectId && o.BaseId == intended.BaseId)\n              ?? local.FirstOrDefault(o => o.BaseId == intended.BaseId &&\n                  Vector3.Distance(o.Position, intended.Position) <= 2f);\n        return resolved is null ? null :\n            new(resolved.GameObjectId, resolved.BaseId, resolved.Name.TextValue, resolved.Position);\n    }\n\n    public bool InstanceTargetIsSelected(InstanceAetheryteTarget? intended) =>\n        intended is not null && targets.Target?.GameObjectId == intended.ObjectId;\n\n    public unsafe bool InteractWithInstanceAetheryte(InstanceAetheryteTarget intended, float range)\n    {\n        if (IsBusy || SelectionMenuIsOpen() || condition[ConditionFlag.Mounted] ||\n            condition[ConditionFlag.InFlight] || objects.LocalPlayer is not { } player)\n            return false;\n        var target = objects.FirstOrDefault(o => o.GameObjectId == intended.ObjectId &&\n            o.BaseId == intended.BaseId && o.ObjectKind == ObjectKind.Aetheryte && o.IsTargetable &&\n            Vector3.Distance(o.Position, intended.Position) <= 1f &&\n            Vector3.Distance(player.Position, o.Position) <= range);\n        var system = TargetSystem.Instance();\n        if (target is null || system is null)\n            return false;\n        targets.Target = target;\n        system->InteractWithObject((CSGameObject*)target.Address, false);\n        return true; // Dispatched only: the caller waits for the real selection UI.\n    }\n\n    public unsafe bool SelectionMenuIsOpen() =>\n        IsReady(gameGui.GetAddonByName<AtkUnitBase>(\"SelectString\"));\n\n    public bool InstanceTravelMenuIsOpen() => StringMenuContains(text =>\n        text.Contains(\"Travel to Instanced Area\", StringComparison.OrdinalIgnoreCase) ||\n        text.Contains(\"Change instance\", StringComparison.OrdinalIgnoreCase));\n\n    public bool InstanceSelectionIsOpen(int requested) => StringMenuContains(text =>\n        text.Any(character => character is >= '\\uE0B1' and <= '\\uE0B9'));\n\n    private unsafe bool StringMenuContains(Func<string, bool> predicate)\n    {\n        var addon = gameGui.GetAddonByName<AddonSelectString>(\"SelectString\");\n        if (addon is null || !IsReady((AtkUnitBase*)addon))\n            return false;\n        var menu = &addon->PopupMenu.PopupMenu;\n        if (menu->EntryNames is null || menu->EntryCount <= 0)\n            return false;\n        for (var i = 0; i < menu->EntryCount; i++)\n            if (predicate(menu->EntryNames[i].ToString()))\n                return true;\n        return false;\n    }\n\n",
      ""
    ]
  ]
};
const baseline = JSON.parse(read("docs/sentinel-modern-hunt-baseline.json"));
for (const [path, expectedHash] of Object.entries(baseline.sha256)) {
  let source = read(path);
  for (const [change, original] of reviewedInstancePatches[path] ?? []) {
    assert(source.split(change).length === 2, `Missing/duplicated instance recovery edit: ${path}`);
    source = source.replace(change, original);
  }
  for (const [reviewedChange, baselineText] of reviewedRoutingPatches[path] ?? []) {
    assert(source.split(reviewedChange).length === 2,
      `Missing or duplicated reviewed routing change: ${path}`);
    source = source.replace(reviewedChange, baselineText);
  }
  if (path === "Plugin.cs") {
    // Only constructor/teardown access-boundary hooks; no hunt implementation changes.
    source = source.replace("        IPluginLog pluginLog,\n        IPlayerState playerState)", "        IPluginLog pluginLog)");
    for (const hook of ["        RegisterRemoteRecovery(playerState);\n", "        DisposeRemoteRecovery();\n"]) {
      assert(source.split(hook).length === 2, "Missing/duplicated recovery IPC lifecycle hook");
      source = source.replace(hook, "");
    }
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
