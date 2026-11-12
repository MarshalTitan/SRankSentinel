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
// Reverse the exact reviewed 7.58 World Visit/instance entry changes, then enforce the same hunt baseline.
const reviewedWorldVisitPatches = {
  "NativeTravel.cs": [
    [
      "using CSGameObject",
      "using System.Runtime.InteropServices;\nusing CSGameObject"
    ],
    [
      "    private unsafe bool CanUseTravelAction(uint actionId)\n    {\n        if (IsBusy || condition[ConditionFlag.InCombat] || condition[ConditionFlag.Unconscious] ||\n            objects.LocalPlayer is not { IsTargetable: true })\n            return false;\n\n        var manager = FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance();\n        return manager is not null &&\n               manager->GetActionStatus(FFXIVClientStructs.FFXIV.Client.Game.ActionType.Action, actionId) == 0;\n    }\n\n    private static unsafe bool IsReady(",
      "    private static unsafe List<string> GetAvailableWorlds()\n    {\n        var result = new List<string>();\n        var module = RaptureAtkModule.Instance();\n        if (module is null)\n            return result;\n\n        var strings = module->AtkArrayDataHolder.StringArrays[(int)StringArrayType.WorldTranslate];\n        if (strings is null)\n            return result;\n\n        for (var i = 3; i <= 10; i++)\n        {\n            var value = strings->StringArray[i];\n            if (!value.HasValue)\n                break;\n\n            var name = (Marshal.PtrToStringUTF8((nint)value.Value) ?? string.Empty).Trim();\n            if (string.IsNullOrEmpty(name))\n                break;\n            result.Add(name);\n        }\n\n        return result;\n    }\n\n    private unsafe bool CanUseTravelAction(uint actionId)\n    {\n        if (IsBusy || condition[ConditionFlag.InCombat] || condition[ConditionFlag.Unconscious] ||\n            objects.LocalPlayer is not { IsTargetable: true })\n            return false;\n\n        var manager = FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance();\n        return manager is not null &&\n               manager->GetActionStatus(FFXIVClientStructs.FFXIV.Client.Game.ActionType.Action, actionId) == 0;\n    }\n\n    private static unsafe bool IsReady("
    ],
    [
      "    public unsafe bool ReturnConfirmationIsOpen(out string prompt)",
      "    public unsafe bool SelectWorld(string world)\n    {\n        var addon = gameGui.GetAddonByName<AtkUnitBase>(\"WorldTravelSelect\");\n        if (!IsReady(addon))\n            return false;\n\n        var availableWorlds = GetAvailableWorlds();\n        var index = availableWorlds.FindIndex(value => value.Equals(world, StringComparison.OrdinalIgnoreCase));\n        if (index < 0)\n            return false;\n\n        FireCallback(addon, 0, index + 2);\n        return true;\n    }\n\n    public unsafe bool ConfirmWorldVisit(string world)\n    {\n        var addon = gameGui.GetAddonByName<AddonSelectYesno>(\"SelectYesno\");\n        if (addon is null || !IsReady((AtkUnitBase*)addon) || addon->YesButton is null || !addon->YesButton->IsEnabled)\n            return false;\n\n        var prompt = addon->PromptText is null ? string.Empty : addon->PromptText->NodeText.ToString();\n        if (!prompt.Contains(world, StringComparison.OrdinalIgnoreCase) &&\n            !prompt.Contains(\"World\", StringComparison.OrdinalIgnoreCase))\n            return false;\n\n        FireCallback((AtkUnitBase*)addon, 0);\n        return true;\n    }\n\n    public unsafe bool ReturnConfirmationIsOpen(out string prompt)"
    ],
    [
      "    public unsafe bool CanTeleportTo(uint aetheryteId)",
      "    public bool IsSameDataCenter(string world)\n    {\n        return TryGetDataCenterRelationship(world, out _, out _, out var isSameDataCenter) &&\n               isSameDataCenter;\n    }\n\n    /// <summary>\n    /// Refreshes the session data center from the character's current visited World. The cached\n    /// value survives short zoning transitions where LocalPlayer is temporarily unavailable.\n    /// </summary>\n    public void RefreshCurrentDataCenter()\n    {\n        if (objects.LocalPlayer is not { } player || player.CurrentWorld.Value.DataCenter.RowId == 0)\n            return;\n        lastKnownDataCenterId = player.CurrentWorld.Value.DataCenter.RowId;\n        lastKnownDataCenterName = player.CurrentWorld.Value.DataCenter.Value.Name.ToString();\n    }\n\n    public bool TryGetDataCenterRelationship(\n        string world,\n        out string currentDataCenter,\n        out string eventDataCenter,\n        out bool isSameDataCenter)\n    {\n        currentDataCenter = string.Empty;\n        eventDataCenter = string.Empty;\n        isSameDataCenter = false;\n        RefreshCurrentDataCenter();\n        if (lastKnownDataCenterId == 0 || string.IsNullOrWhiteSpace(world))\n            return false;\n\n        var target = data.GetExcelSheet<World>()\n            .FirstOrDefault(row => row.Name.ToString().Equals(world.Trim(), StringComparison.OrdinalIgnoreCase));\n        if (target.RowId == 0 || target.DataCenter.RowId == 0)\n            return false;\n\n        currentDataCenter = lastKnownDataCenterName;\n        eventDataCenter = target.DataCenter.Value.Name.ToString();\n        isSameDataCenter = target.DataCenter.RowId == lastKnownDataCenterId;\n        return true;\n    }\n\n    public string GetDataCenterSlug(string world)\n    {\n        if (string.IsNullOrWhiteSpace(world))\n            return string.Empty;\n        var target = data.GetExcelSheet<World>()\n            .FirstOrDefault(row => row.Name.ToString().Equals(world.Trim(), StringComparison.OrdinalIgnoreCase));\n        if (target.RowId == 0 || target.DataCenter.RowId == 0)\n            return string.Empty;\n        var name = target.DataCenter.Value.Name.ToString();\n        return new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());\n    }\n\n    public unsafe bool CanTeleportTo(uint aetheryteId)"
    ],
    [
      "    public bool IsBetweenAreas =>",
      "    private uint lastKnownDataCenterId;\n    private string lastKnownDataCenterName = string.Empty;\n\n    public bool IsBetweenAreas =>"
    ],
    [
      "internal sealed partial class NativeTravel(",
      "internal sealed class NativeTravel("
    ]
  ],
  "Plugin.cs": [
    [
      "        WorldVisit,\n        SelectLogicalDataCenter,\n        SelectWorld,",
      "        WorldVisit,\n        SelectWorld,"
    ],
    [
      "        if (IsInstanceRecoveryState(previous) && !IsInstanceRecoveryState(next))\n            ResetInstanceRangeRecovery();\n        if (IsWorldVisitState(previous) && !IsWorldVisitState(next))\n            ReleaseWorldVisitMenus();\n        state = next;",
      "        if (IsInstanceRecoveryState(previous) && !IsInstanceRecoveryState(next))\n            ResetInstanceRangeRecovery();\n        state = next;"
    ],
    [
      "        var eligible = travel.IsWorldVisitCompatible(world);\n        LogProviderRegion(source, \"death\", world, eligible);\n        if (!eligible)\n            return;\n        bool Matches(HuntAlertSnapshot alert) =>\n            WorldVisitPolicy.MatchesDeath(alert, world, territory, instance, deathAtUtc,\n                HuntCatalog.NamesMatch(alert.CreatureName, creature),\n                IsObservedSingleInstance(alert.World, alert.TerritoryId));",
      "        bool Matches(HuntAlertSnapshot alert) =>\n            alert.World.Equals(world, StringComparison.OrdinalIgnoreCase) &&\n            HuntCatalog.NamesMatch(alert.CreatureName, creature) &&\n            (territory == 0 || alert.TerritoryId == territory) &&\n            (instance <= 0 || alert.Instance == instance) &&\n            deathAtUtc >= alert.ReceivedAtUtc.AddMinutes(-1);"
    ],
    [
      "        !killedAlerts.ContainsKey(alert.Key) &&\n        !killedAlerts.ContainsKey(CanonicalizeObservedInstance(alert).Key) &&\n        travel.IsWorldVisitCompatible(alert.World) &&",
      "        !killedAlerts.ContainsKey(alert.Key) &&\n        travel.IsSameDataCenter(alert.World) &&"
    ],
    [
      "            instance, mapX, mapY, occurredAtUtc ?? DateTime.UtcNow);\n        incoming = CanonicalizeObservedInstance(incoming);\n        if (isSs && HuntCatalog.TryGetSsStagingLocation(territory, out var ssLocation))",
      "            instance, mapX, mapY, occurredAtUtc ?? DateTime.UtcNow);\n        if (isSs && HuntCatalog.TryGetSsStagingLocation(territory, out var ssLocation))"
    ],
    [
      "        var eligible = travel.IsWorldVisitCompatible(world);\n        LogProviderRegion(source, \"spawn\", world, eligible);\n        if (!eligible)\n        {\n            status = $\"Ignored {source} alert for {creature.Trim()} on {world}: outside the current World Visit region\";\n            return;\n        }",
      "        if (!travel.IsSameDataCenter(world))\n        {\n            status = $\"Ignored {source} alert for {creature.Trim()} on {world}: normal World Visit cannot cross data centers\";\n            return;\n        }"
    ],
    [
      "        if (clientState.TerritoryType != current.TerritoryId ||\n            !travel.CurrentWorld.Equals(current.World, StringComparison.OrdinalIgnoreCase) ||\n            !travel.TryReadLiveInstance(out var instance))\n        {\n            playerReadySinceUtc = DateTime.MinValue;\n            playerReadyInstance = -1;\n            status = \"Waiting at the aetheryte for zoning and the live instance state\";\n            return;\n        }\n\n        if (playerReadySinceUtc == DateTime.MinValue || playerReadyInstance != instance)\n        {\n            playerReadySinceUtc = now;\n            playerReadyInstance = instance;\n            status = \"Zoning complete; allowing the live instance state to settle at the aetheryte\";\n            return;\n        }\n\n        if ((now - playerReadySinceUtc).TotalSeconds < 2)\n            return;\n\n        // InstanceId == 0 means no public instance on the live, settled client.\n        RecordLiveInstanceTopology(instance);\n        if (WorldVisitPolicy.InstanceArrival(instance, current.Instance) == InstanceArrivalAction.ChangeInstance)\n        {\n            RequireInstanceChange(instance);\n            return;\n        }\n",
      "        var instance = travel.CurrentInstance;\n        var correctInstance = instance == current.Instance || (current.Instance == 1 && instance == 0);\n        if (travel.IsBusy || objects.LocalPlayer is null || clientState.TerritoryType != current.TerritoryId ||\n            !correctInstance)\n        {\n            playerReadySinceUtc = DateTime.MinValue;\n            status = \"Waiting at the aetheryte for zoning, the player object, and the requested instance to become ready\";\n            return;\n        }\n\n        if (playerReadySinceUtc == DateTime.MinValue)\n        {\n            playerReadySinceUtc = now;\n            status = \"Zoning complete; allowing the local player state to settle at the aetheryte\";\n            return;\n        }\n\n        if ((now - playerReadySinceUtc).TotalSeconds < 2)\n            return;\n"
    ],
    [
      "    private void BeginInstanceCheck()\n    {\n        ResetInstanceRangeRecovery();\n        if (current is not null)\n            BeginPlayerReadyWait(\"Territory reached; checking the live instance after zoning settles\");\n    }\n\n    private void RequireInstanceChange(int instance)\n    {\n        if (current is null)\n            return;\n        vnav.StopSafe(\"preparing local instance-change interaction\");\n        log.Information(\"Instance change required: required={Required}, current={Current}, territory={Territory}, preferredAetheryte={Aetheryte}\",\n            current.Instance, instance, current.TerritoryId, territoryAetheryteId);\n        nextActionUtc = DateTime.UtcNow;\n        SetState(SentinelState.ChangeInstance, $\"Changing normally to instance {current.Instance}\");\n    }\n\n",
      "    private void BeginInstanceCheck()\n    {\n        ResetInstanceRangeRecovery();\n        if (current is null)\n            return;\n        var instance = travel.CurrentInstance;\n        if (instance == current.Instance || (current.Instance == 1 && instance == 0))\n        {\n            BeginPlayerReadyWait(\"Territory and instance selected\");\n            return;\n        }\n        vnav.StopSafe(\"preparing local instance-change interaction\");\n        log.Information(\"Instance change required: required={Required}, current={Current}, territory={Territory}, preferredAetheryte={Aetheryte}\",\n            current.Instance, instance, current.TerritoryId, territoryAetheryteId);\n        nextActionUtc = DateTime.UtcNow;\n        SetState(SentinelState.ChangeInstance, $\"Changing normally to instance {current.Instance}\");\n    }\n\n"
    ],
    [
      "    private void CompleteWorldVisit(string targetWorld)\n    {\n        if (current is not null && !IsAlertFresh(current, DateTime.UtcNow))\n        {\n            FailWorldVisit(\"hunt report expired or was invalidated during World Visit\");\n            return;\n        }\n        vnav.StopSafe();",
      "    private void CompleteWorldVisit(string targetWorld)\n    {\n        vnav.StopSafe();"
    ],
    [
      "    private void CompleteWorldVisit(string targetWorld)",
      "    private void TickWorldVisit(DateTime now)\n    {\n        var targetWorld = WorldVisitTarget();\n        if (string.IsNullOrWhiteSpace(targetWorld))\n        {\n            FailWorldVisit(\"World Visit had no valid destination\");\n            return;\n        }\n        if (travel.CurrentWorld.Equals(targetWorld, StringComparison.OrdinalIgnoreCase))\n        {\n            CompleteWorldVisit(targetWorld);\n            return;\n        }\n        if (TravelTimedOut(now))\n        {\n            FailWorldVisit($\"World Visit to {targetWorld} timed out\");\n            return;\n        }\n\n        if (travel.SelectWorldVisitMenu())\n        {\n            vnav.StopSafe();\n            SetState(SentinelState.SelectWorld, $\"Selecting {targetWorld} from World Visit\");\n            return;\n        }\n\n        if (now < nextActionUtc || travel.IsBusy)\n            return;\n        if (condition[ConditionFlag.Mounted])\n            UseGeneralAction(23);\n        else if (!travel.InteractWithNearbyAetheryte() && vnav.IsReadySafe() &&\n                 !vnav.IsPathRunningSafe() && !vnav.IsPathfindInProgressSafe())\n        {\n            vnav.MoveToSafe(NativeTravel.UldahAetherytePosition, false);\n            status = \"Walking normally to the Ul'dah World Visit aetheryte\";\n        }\n        nextActionUtc = now.AddSeconds(2);\n    }\n\n    private void TickSelectWorld(DateTime now)\n    {\n        var targetWorld = WorldVisitTarget();\n        if (string.IsNullOrWhiteSpace(targetWorld))\n            return;\n        if (TravelTimedOut(now))\n        {\n            FailWorldVisit($\"Could not select {targetWorld} in World Visit\");\n            return;\n        }\n        if (travel.SelectWorld(targetWorld))\n            SetState(SentinelState.ConfirmWorldVisit, $\"Confirming normal World Visit to {targetWorld}\");\n    }\n\n    private void TickConfirmWorldVisit(DateTime now)\n    {\n        var targetWorld = WorldVisitTarget();\n        if (string.IsNullOrWhiteSpace(targetWorld))\n            return;\n        if (travel.CurrentWorld.Equals(targetWorld, StringComparison.OrdinalIgnoreCase))\n        {\n            SetState(SentinelState.WaitForWorld, $\"Arriving on {targetWorld}\");\n            return;\n        }\n        if (TravelTimedOut(now))\n        {\n            FailWorldVisit($\"World Visit confirmation for {targetWorld} timed out\");\n            return;\n        }\n        if (travel.ConfirmWorldVisit(targetWorld))\n            SetState(SentinelState.WaitForWorld, $\"Queued for World Visit to {targetWorld}\");\n    }\n\n    private void TickWaitForWorld(DateTime now)\n    {\n        var targetWorld = WorldVisitTarget();\n        if (string.IsNullOrWhiteSpace(targetWorld))\n            return;\n        if (!travel.IsBusy && travel.CurrentWorld.Equals(targetWorld, StringComparison.OrdinalIgnoreCase) &&\n            travel.IsInUldah(clientState.TerritoryType))\n        {\n            CompleteWorldVisit(targetWorld);\n            return;\n        }\n        if (TravelTimedOut(now))\n            FailWorldVisit($\"Arrival on {targetWorld} timed out\");\n    }\n\n    private string WorldVisitTarget() => current?.World ?? string.Empty;\n\n    private void CompleteWorldVisit(string targetWorld)"
    ],
    [
      "                BeginRegionalWorldVisit(now);",
      "                nextActionUtc = now;\n                SetState(SentinelState.WorldVisit, $\"Starting normal World Visit to {current.World}\");"
    ],
    [
      "            case SentinelState.SelectLogicalDataCenter:\n                TickSelectLogicalDataCenter(now);\n                return;\n            case SentinelState.SelectWorld:\n                TickSelectWorld(now);",
      "            case SentinelState.SelectWorld:\n                TickSelectWorld(now);"
    ],
    [
      "        if (!persistentQueueRestorePending || !travel.HasWorldVisitContext)\n            return;",
      "        if (!persistentQueueRestorePending)\n            return;"
    ]
  ]
};
// Reverse only the reviewed Lifestream handoff and ownership changes before checking the hunt baseline.
const reviewedLifestreamPatches = {
  "Plugin.cs": [
    [
      "        WorldVisit,\n        TeleportToTerritory,",
      "        WorldVisit,\n        SelectLogicalDataCenter,\n        SelectWorld,\n        ConfirmWorldVisit,\n        WaitForWorld,\n        TeleportToTerritory,"
    ],
    [
      "        state = next;",
      "        if (IsWorldVisitState(previous) && !IsWorldVisitState(next))\n            ReleaseWorldVisitMenus();\n        state = next;"
    ],
    [
      "                BeginLifestreamWorldVisit(now);\n                return;\n            }\n\n            worldTravel = null;\n            log.Information(\"Travel state entered for {Mark}: {World} -> territory",
      "                BeginRegionalWorldVisit(now);\n                return;\n            }\n\n            log.Information(\"Travel state entered for {Mark}: {World} -> territory"
    ],
    [
      "            case SentinelState.TeleportToTerritory:",
      "            case SentinelState.WorldVisit:\n                TickWorldVisit(now);\n                return;\n            case SentinelState.SelectLogicalDataCenter:\n                TickSelectLogicalDataCenter(now);\n                return;\n            case SentinelState.SelectWorld:\n                TickSelectWorld(now);\n                return;\n            case SentinelState.ConfirmWorldVisit:\n                TickConfirmWorldVisit(now);\n                return;\n            case SentinelState.WaitForWorld:\n                TickWaitForWorld(now);\n                return;\n            case SentinelState.TeleportToTerritory:"
    ],
    [
      "    private void ClearCurrent()\n    {\n        worldTravel = null;\n        ResetInstanceRangeRecovery();",
      "    private void ClearCurrent()\n    {\n        ResetInstanceRangeRecovery();"
    ],
    [
      "    private void StartAlert(HuntAlertSnapshot alert, string source)\n    {\n        worldTravel = null;\n        if",
      "    private void StartAlert(HuntAlertSnapshot alert, string source)\n    {\n        if"
    ],
    [
      "        var readiness = lifestream.ReadReadiness();\n        if (current is not null || state != SentinelState.Idle || pendingAlerts.Count > 0 ||\n            readiness != LifestreamReadiness.Idle)\n        {\n            EnqueuePersistent(incoming);\n            status = readiness == LifestreamReadiness.Unavailable ? LifestreamRequiredMessage :\n                $\"Queued {incoming.CreatureName}; {pendingAlerts.Count} S rank(s) waiting\";",
      "        if (current is not null || state != SentinelState.Idle)\n        {\n            EnqueuePersistent(incoming);\n            status = $\"Queued {incoming.CreatureName}; {pendingAlerts.Count} S rank(s) waiting\";"
    ],
    [
      "            RestorePersistentQueue();\n            log.Information(",
      "            RestorePersistentQueue();\n            if (current is null && TryDequeueNextValid(out var restored))\n                StartAlert(restored, \"restored persistent queue on the main framework thread\");\n            log.Information("
    ],
    [
      "            if (current is null && state == SentinelState.Idle)\n            {\n                if (pendingAlerts.Count != 0 && TryDequeueNextValid(out var pending))\n                    StartAlert(pending, \"next eligible queued alert\");\n                return;\n            }\n            Tick(now);",
      "            if (current is null && state == SentinelState.Idle)\n                return;\n            Tick(now);"
    ],
    [
      "            if (!config.Enabled || !CanRunHuntTick(now))\n                return;\n            travel.RefreshCurrentDataCenter();",
      "            if (!config.Enabled)\n                return;\n            travel.RefreshCurrentDataCenter();"
    ],
    [
      "    private readonly LifestreamIpc lifestream;\n    private readonly VNavmeshIpc vnav;",
      "    private readonly VNavmeshIpc vnav;"
    ],
    [
      "        lifestream = new LifestreamIpc(pi, pluginLog);\n        vnav = new VNavmeshIpc(pi, pluginLog, CanStopSentinelMovement);",
      "        vnav = new VNavmeshIpc(pi, pluginLog);"
    ]
  ],
  "NativeTravel.cs": [
    [
      "    // World travel is owned by Lifestream; only local interaction menus are handled here.",
      "    public unsafe bool WorldSelectionIsOpen() => IsReady(gameGui.GetAddonByName<AtkUnitBase>(\"WorldTravelSelect\"));"
    ],
    [
      "    public unsafe bool SelectInstanceTravelMenu() =>",
      "    public unsafe bool SelectWorldVisitMenu() =>\n        TrySelectStringEntry(text => text.Contains(\"Visit Another World\", StringComparison.OrdinalIgnoreCase));\n\n    public unsafe bool SelectInstanceTravelMenu() =>"
    ],
    [
      "    public const uint UldahAetheryteId = 9;\n    public bool IsBetweenAreas =>",
      "    public const uint UldahAetheryteId = 9;\n    public static readonly Vector3 UldahAetherytePosition = new(-144.52f, -1.36f, -169.67f);\n    public bool IsBetweenAreas =>"
    ]
  ],
  "VNavmeshIpc.cs": [
    [
      "            stop.InvokeAction();\n            issuedMovement = false;\n        }",
      "            stop.InvokeAction();\n        }"
    ],
    [
      "    public void StopOwnedMovementSafe(string reason)\n    {\n        if (issuedMovement)\n            StopSafe(reason);\n    }\n\n    public void StopSafe(string reason = \"unspecified\", [CallerMemberName] string caller = \"unknown\")\n    {\n        try\n        {\n            if (!canStopMovement())\n                return;\n            var diagnostic",
      "    public void StopSafe(string reason = \"unspecified\", [CallerMemberName] string caller = \"unknown\")\n    {\n        try\n        {\n            var diagnostic"
    ],
    [
      "            movePath.InvokeAction(waypoints, fly);\n            issuedMovement = true;\n            return true;",
      "            movePath.InvokeAction(waypoints, fly);\n            return true;"
    ],
    [
      "        try\n        {\n            var accepted = moveTo.InvokeFunc(destination, fly);\n            issuedMovement |= accepted;\n            return accepted;\n        }",
      "        try { return moveTo.InvokeFunc(destination, fly); }"
    ],
    [
      "        try\n        {\n            var accepted = moveCloseTo.InvokeFunc(destination, fly, range);\n            issuedMovement |= accepted;\n            return accepted;\n        }",
      "        try { return moveCloseTo.InvokeFunc(destination, fly, range); }"
    ],
    [
      "    public VNavmeshIpc(IDalamudPluginInterface pi, IPluginLog log, Func<bool> canStopMovement)\n    {\n        this.log = log;\n        this.canStopMovement = canStopMovement;",
      "    public VNavmeshIpc(IDalamudPluginInterface pi, IPluginLog log)\n    {\n        this.log = log;"
    ],
    [
      "    private readonly IPluginLog log;\n    private readonly Func<bool> canStopMovement;\n    private bool issuedMovement;\n    private string lastStopDiagnostic",
      "    private readonly IPluginLog log;\n    private string lastStopDiagnostic"
    ]
  ],
  "LifestreamIpc.cs": [
    [
      "using Dalamud.Plugin;\nusing Dalamud.Plugin.Ipc;\nusing Dalamud.Plugin.Services;\n\nnamespace SRankSentinel;\n\ninternal sealed class LifestreamIpc(IDalamudPluginInterface pi, IPluginLog log)\n{\n    private readonly ICallGateSubscriber<bool> isBusy = pi.GetIpcSubscriber<bool>(\"Lifestream.IsBusy\");\n    private readonly ICallGateSubscriber<string, bool> changeWorld =\n        pi.GetIpcSubscriber<string, bool>(\"Lifestream.ChangeWorld\");\n\n    public bool HasRequiredIpc => isBusy.HasFunction && changeWorld.HasFunction;\n\n    public LifestreamReadiness ReadReadiness()\n    {\n        if (!HasRequiredIpc)\n            return LifestreamReadiness.Unavailable;\n        try { return isBusy.InvokeFunc() ? LifestreamReadiness.Busy : LifestreamReadiness.Idle; }\n        catch { return LifestreamReadiness.Unavailable; }\n    }\n\n    public bool TryChangeWorld(string world)\n    {\n        try { return changeWorld.InvokeFunc(world); }\n        catch (Exception ex)\n        {\n            log.Warning(ex, \"Lifestream.ChangeWorld IPC failed for {World}\", world);\n            return false;\n        }\n    }\n}\n",
      "using Dalamud.Plugin;\nusing Dalamud.Plugin.Ipc;\n\nnamespace SRankSentinel;\n\ninternal sealed class LifestreamIpc\n{\n    private const uint UldahAetheryteId = 9;\n\n    private readonly ICallGateSubscriber<bool> isBusy;\n    private readonly ICallGateSubscriber<string, bool> changeWorld;\n    private readonly ICallGateSubscriber<uint, byte, bool> teleport;\n\n    public LifestreamIpc(IDalamudPluginInterface pi)\n    {\n        isBusy = pi.GetIpcSubscriber<bool>(\"Lifestream.IsBusy\");\n        changeWorld = pi.GetIpcSubscriber<string, bool>(\"Lifestream.ChangeWorld\");\n        teleport = pi.GetIpcSubscriber<uint, byte, bool>(\"Lifestream.Teleport\");\n    }\n\n    public bool IsBusySafe()\n    {\n        try { return isBusy.InvokeFunc(); }\n        catch { return false; }\n    }\n\n    public bool TeleportToUldahSafe()\n        => TeleportSafe(UldahAetheryteId);\n\n    public bool ChangeWorldSafe(string world)\n    {\n        try { return changeWorld.InvokeFunc(world); }\n        catch { return false; }\n    }\n\n    public bool TeleportSafe(uint aetheryteId)\n    {\n        try { return teleport.InvokeFunc(aetheryteId, 0); }\n        catch { return false; }\n    }\n}\n"
    ]
  ]
};
// Reverse the reviewed World Visit death handoff before checking the unchanged hunt baseline.
const reviewedWorldVisitDeathPatches = {
  "LifestreamIpc.cs": [
    [
      "    private readonly ICallGateSubscriber<object> abort = pi.GetIpcSubscriber<object>(\"Lifestream.Abort\");\n\n    public bool HasRequiredIpc",
      "    public bool HasRequiredIpc"
    ],
    [
      "    public bool TryAbort()\n    {\n        if (!abort.HasAction)\n            return false;\n        try\n        {\n            abort.InvokeAction();\n            return true;\n        }\n        catch (Exception ex)\n        {\n            log.Warning(ex, \"Lifestream.Abort IPC failed\");\n            return false;\n        }\n    }\n\n    public bool TryChangeWorld",
      "    public bool TryChangeWorld"
    ]
  ],
  "LifestreamTravel.cs": [
    [
      "    internal bool AwaitingArrival { get; private set; }\n    internal uint AcceptedSourceId { get; private set; }\n    internal WorldVisitDeathCancellation? DeathCancellation { get; private set; }\n\n    internal void BeginDeathCancellation(uint actualWorldId, DateTime now) =>\n        DeathCancellation ??= new(AwaitingArrival ? AcceptedSourceId : actualWorldId,\n            Destination.Id, AwaitingArrival, now);",
      "    internal bool AwaitingArrival { get; private set; }"
    ],
    [
      "            AwaitingArrival = true;\n            AcceptedSourceId = actualWorldId;",
      "            AwaitingArrival = true;"
    ]
  ],
  "Plugin.cs": [
    [
      "            current.CreatureName, current.World, reason);\n        if (BeginWorldVisitDeathCancellation(current, now))\n            return;\n        if (HuntCatalog.IsSupportedNormalS(current.TerritoryId, current.CreatureName))",
      "            current.CreatureName, current.World, reason);\n        if (HuntCatalog.IsSupportedNormalS(current.TerritoryId, current.CreatureName))"
    ]
  ],
  "Plugin.WorldVisit.cs": [
    [
      "        if (state == SentinelState.WorldVisit && worldTravel?.DeathCancellation is not null)\n        {\n            TickLifestreamWorldVisit(now, provider);\n            return false;\n        }\n\n        if (provider == LifestreamReadiness.Unavailable)\n",
      "        if (provider == LifestreamReadiness.Unavailable)\n"
    ],
    [
      "        if (transfer.DeathCancellation is { } cancellation)\n        {\n            TickWorldVisitDeathCancellation(now, transfer, cancellation, provider, actualWorldId, interactable, atHub);\n            return;\n        }\n        if (provider == LifestreamReadiness.Idle && !gameBusy && !IsAlertFresh(current, now))",
      "        if (provider == LifestreamReadiness.Idle && !gameBusy && !IsAlertFresh(current, now))"
    ]
  ]
};

// Reverse the reviewed DC intake and checkpoint migration before the unchanged hunt baseline.
const reviewedCheckpointPatches = {
  "Plugin.cs": [
    [
      "escalating landing/range recovery without advancing the checkpoint",
      "escalating landing/range recovery without issuing any extra attack after confirmation"
    ],
    [
      "                    \"Tag confirmed by handled action sequence: {Mark}, pull={Pull}, checkpoint={Checkpoint}, consumed={Consumed}, action={ActionId}, target={TargetId}, sequence={Sequence}\",\n                    mark.Name.TextValue, pullCycle, pending.Checkpoint.Label, tagCycle.Consumed, activeTagActionId, mark.GameObjectId, sequence);",
      "                    \"Tag confirmed by handled action sequence: {Mark}, pull cycle={PullCycle}, action={ActionId}, target={TargetId}, sequence={Sequence}\",\n                    mark.Name.TextValue, pullCycle, activeTagActionId, mark.GameObjectId, sequence);"
    ],
    [
      "        if (feedEvent.Action == FaloopEventAction.Death)\n        {\n            ObserveKnownHistoryDeaths(world, hasTerritory ? territory : 0, feedEvent.Instance,\n                feedEvent.OccurredAtUtc, name => HuntCatalog.NamesMatch(name, creature));\n            if (!TryMatchTrackedFaloopDeath",
      "        if (feedEvent.Action == FaloopEventAction.Death)\n        {\n            if (!TryMatchTrackedFaloopDeath"
    ],
    [
      "name => HuntCatalog.TextMentionsMark(text, name), requireContextForMissing: true);\n                RemoveKilledQueuedAlerts",
      "name => HuntCatalog.TextMentionsMark(text, name));\n                RemoveKilledQueuedAlerts"
    ],
    [
      "                ObserveHistoryTag(current, now, firstTagOfPull);\n                pendingTagDispatch = null;",
      "                ObserveHistoryTag(current, now);\n                pendingTagDispatch = null;"
    ],
    [
      "            var firstTagOfPull = !pullCycleTagged;\n            if (pending.Sequence is ushort sequence &&\n                ActionSequenceWasHandled",
      "            if (pending.Sequence is ushort sequence &&\n                ActionSequenceWasHandled"
    ],
    [
      "        if (current is null || killConfirmed || !markEverIdentified || !target.IsTargetable ||\n            target.ObjectKind != ObjectKind.BattleNpc || target.GameObjectId != identifiedMarkGameObjectId ||\n            !((current.MarkDataId != 0 && target.BaseId == current.MarkDataId) ||\n              target.Name.TextValue.Equals(current.CreatureName, StringComparison.OrdinalIgnoreCase)) ||",
      "        if (current is null || killConfirmed || !markEverIdentified || !target.IsTargetable ||\n            target.GameObjectId != identifiedMarkGameObjectId || !IsCurrentMarkActor(target) ||"
    ],
    [
      "        if (tagCycle.RetreatPending &&\n            clearance < ActiveDistanceProfile.WaitingDistance - ParkingPreferredClearanceTolerance)\n        {\n            if (now >= nextActionUtc)\n            {\n                BeginSafeParking(mark, fly: false);\n                nextActionUtc = now.AddSeconds(2);\n            }\n            return;\n        }\n\n        var preferredError = HuntProgressPolicy.ParkingClearanceError(\n            clearance, ActiveDistanceProfile.WaitingDistance);\n        if (clearance > MaximumParkingClearance",
      "        var preferredError = HuntProgressPolicy.ParkingClearanceError(\n            clearance, ActiveDistanceProfile.WaitingDistance);\n        if (clearance > MaximumParkingClearance"
    ],
    [
      "parking is suspended until the current checkpoint is confirmed",
      "parking is suspended until one ranged tag is confirmed"
    ],
    [
      "    private bool IsExactCheckpointMark(IBattleChara target)\n    {\n        if (current is null || killConfirmed || !markEverIdentified || !target.IsTargetable ||\n            target.GameObjectId != identifiedMarkGameObjectId || !IsCurrentMarkActor(target) ||\n            HuntCatalog.IsAnyPrecursorName(target.Name.TextValue) || HuntCatalog.IsAnyPrecursorName(current.CreatureName) ||\n            clientState.TerritoryType != current.TerritoryId ||\n            !travel.CurrentWorld.Equals(current.World, StringComparison.OrdinalIgnoreCase))\n            return false;\n        return travel.TryReadLiveInstance(out var instance) && Math.Max(1, instance) == current.Instance;\n    }\n\n    private bool CanActAfterRevival() =>\n        !combat.IsPlayerDead && !travel.IsBusy && objects.LocalPlayer is { IsTargetable: true } &&\n        !condition[ConditionFlag.Occupied] && !condition[ConditionFlag.OccupiedInEvent] &&\n        !condition[ConditionFlag.OccupiedInQuestEvent] && !condition[ConditionFlag.OccupiedInCutSceneEvent];\n\n    private void LatchTagRequirement(IBattleChara target, DateTime now)\n    {\n        var exact = IsExactCheckpointMark(target);\n        if (exact && tagCycle.RetreatPending && state == SentinelState.SafeWait &&\n            ClearanceFromMark(target) >= ActiveDistanceProfile.WaitingDistance - ParkingPreferredClearanceTolerance)\n            tagCycle.CompleteRetreat();\n\n        var hp = CombatController.HpPercent(target);\n        var enabled = (config.TagAt75Percent ? TagMilestone.At75 : TagMilestone.None) |\n                      (config.TagAt50Percent ? TagMilestone.At50 : TagMilestone.None) |\n                      (config.TagAt25Percent ? TagMilestone.At25 : TagMilestone.None);\n        if (tagCycle.TryLatch(exact, !target.IsDead && target.CurrentHp > 0,\n                CombatController.IsMarkInCombat(target), hp, ActiveDistanceProfile.EngageHpPercent, enabled))\n        {\n            tagRequiredSinceUtc = now;\n            tagLastProgressUtc = now;\n            tagLastProgressPosition = PlayerPosition();\n            tagBestClearance = ClearanceFromMark(target);\n            tagRecoveryFailures = 0;\n            tagHpEscalationMask = 0;\n            activeTagActionId = combat.ResolveTagActionId();\n            log.Information(\"Tag checkpoint latched: {Checkpoint}, mark={Mark}, pull={Pull}, HP={Hp:0.0}%\",\n                tagCycle.Pending!.Value.Label, target.Name.TextValue, pullCycle, hp);\n        }\n\n        ReportTagRecoveryEscalation(target);\n    }\n\n",
      "    private void LatchTagRequirement(IBattleChara target, DateTime now)\n    {\n        var hp = CombatController.HpPercent(target);\n        if (!HuntProgressPolicy.ShouldLatchTag(\n                !target.IsDead && target.CurrentHp > 0,\n                CombatController.IsMarkInCombat(target),\n                hp,\n                ActiveDistanceProfile.EngageHpPercent,\n                pullCycleTagged || tagAttempted))\n            return;\n\n        if (!tagRequired)\n        {\n            tagRequired = true;\n            tagRequiredSinceUtc = now;\n            tagLastProgressUtc = now;\n            tagLastProgressPosition = PlayerPosition();\n            tagBestClearance = ClearanceFromMark(target);\n            tagRecoveryFailures = 0;\n            tagHpEscalationMask = 0;\n            activeTagActionId = combat.ResolveTagActionId();\n            log.Warning(\n                \"Tag requirement latched: {Mark}, pull cycle={PullCycle}, HP={Hp:0.0}%, mounted={Mounted}, flying={Flying}; parking/mounting cannot clear this obligation\",\n                target.Name.TextValue, pullCycle, hp,\n                condition[ConditionFlag.Mounted], condition[ConditionFlag.InFlight]);\n        }\n\n        ReportTagRecoveryEscalation(target);\n    }\n\n"
    ],
    [
      "        if (tagRequired)\n        {\n            // A tag budget",
      "        if (tagRequired && !pullCycleTagged && !tagAttempted)\n        {\n            // A tag budget"
    ],
    [
      "            (!tagRequired && !pullCycleTagged) ||",
      "            (!tagRequired && (!pullCycleTagged || !tagAttempted)) ||"
    ],
    [
      "    private void ResetTagRecoveryTracking()\n    {",
      "    private void ResetTagRecoveryTracking(bool clearRequirement = true)\n    {\n        if (clearRequirement)\n            tagRequired = false;"
    ],
    [
      "        if (!tagRequired)\n            return;\n        var hp = CombatController.HpPercent(target);",
      "        if (!tagRequired || pullCycleTagged || tagAttempted)\n            return;\n        var hp = CombatController.HpPercent(target);"
    ],
    [
      "            pullCycleTagged ||\n            (now - parkingRecoveryStartedUtc)",
      "            pullCycleTagged && tagAttempted ||\n            (now - parkingRecoveryStartedUtc)"
    ],
    [
      "        if (pullCycleTagged)\n            return false;",
      "        if (pullCycleTagged && tagAttempted)\n            return false;"
    ],
    [
      "if (tagRequired)\n        {\n            BeginTagRequiredRecovery(target, now,\n                $\"Parking/landing recovery failed while a tag is required: {reason}\");",
      "if (tagRequired && !pullCycleTagged && !tagAttempted)\n        {\n            BeginTagRequiredRecovery(target, now,\n                $\"Parking/landing recovery failed while a tag is required: {reason}\");"
    ],
    [
      "if (tagRequired)\n        {\n            BeginTagRequiredRecovery(target, DateTime.UtcNow,\n                \"Tag remains required; ordinary retreat cannot replace the pending ranged tag\");",
      "if (tagRequired && !pullCycleTagged && !tagAttempted)\n        {\n            BeginTagRequiredRecovery(target, DateTime.UtcNow,\n                \"Tag remains required; ordinary retreat cannot replace the pending ranged tag\");"
    ],
    [
      "pullCycleTagged ? \"Confirmed tag completed; safe radius restored\"",
      "tagAttempted ? \"Confirmed tag completed; safe radius restored\""
    ],
    [
      "    DateTime SubmittedAtUtc,\n    TagCheckpoint Checkpoint,\n    TagMilestone CrossedOnSubmit);\n\ninternal sealed record TerritoryAetheryteOverride",
      "    DateTime SubmittedAtUtc);\n\ninternal sealed record TerritoryAetheryteOverride"
    ],
    [
      "waiting for server handling before completing {checkpoint.Label}",
      "waiting for server handling before closing the tag gate"
    ],
    [
      "                        attempt.DeferredForTarget,\n                        now, checkpoint, crossed);",
      "                        attempt.DeferredForTarget,\n                        now);"
    ],
    [
      "            if (now >= nextActionUtc && CanActAfterRevival() &&\n                tagCycle.Pending is { } checkpoint && tagCycle.CanDispatch(IsExactCheckpointMark(mark),\n                    !mark.IsDead && mark.CurrentHp > 0, CombatController.IsMarkInCombat(mark),\n                    CombatController.HpPercent(mark), ActiveDistanceProfile.EngageHpPercent))\n            {\n                var crossed = TagCheckpointCycle.Crossed(CombatController.HpPercent(mark), ActiveDistanceProfile.EngageHpPercent);\n                var attempt = combat.TrySingleTag(activeTagActionId, mark);",
      "            if (now >= nextActionUtc)\n            {\n                var attempt = combat.TrySingleTag(activeTagActionId, mark);"
    ],
    [
      "        if (!tagCycle.CanDispatch(IsExactCheckpointMark(mark), !mark.IsDead && mark.CurrentHp > 0,\n                CombatController.IsMarkInCombat(mark), CombatController.HpPercent(mark), ActiveDistanceProfile.EngageHpPercent))\n        {\n            vnav.StopSafe();\n            status = $\"Waiting for the exact active mark to meet the {tagCycle.Pending?.Label}\";",
      "        if (!CombatController.IsMarkInCombat(mark) ||\n            CombatController.HpPercent(mark) > ActiveDistanceProfile.EngageHpPercent)\n        {\n            vnav.StopSafe();\n            status = $\"Tag requirement remains latched for pull cycle {pullCycle}; waiting for the full-HP/out-of-combat reset confirmation\";"
    ],
    [
      "(action {activeTagActionId}, sequence {sequence}); retreating before the next checkpoint",
      "(action {activeTagActionId}, sequence {sequence}); attack cutoff is active"
    ],
    [
      "                ActionSequenceWasHandled(pending.BaselineSequence, sequence, actionState.LastHandledSequence) &&\n                tagCycle.ConfirmHandled(pending.Checkpoint, pending.CrossedOnSubmit,\n                    CombatController.HpPercent(mark), ActiveDistanceProfile.EngageHpPercent))\n            {\n                ObserveHistoryTag(current, now);\n                pendingTagDispatch = null;\n                ResetTagRecoveryTracking();",
      "                ActionSequenceWasHandled(pending.BaselineSequence, sequence, actionState.LastHandledSequence))\n            {\n                tagAttempted = true;\n                pullCycleTagged = true;\n                ObserveHistoryTag(current, now);\n                tagRequired = false;\n                pendingTagDispatch = null;\n                ResetTagRecoveryTracking(clearRequirement: false);"
    ],
    [
      "        if (pendingTagDispatch is { } dispatched &&\n            (dispatched.TargetId != mark.GameObjectId || !IsExactCheckpointMark(mark) ||\n             tagCycle.Pending != dispatched.Checkpoint))\n            ResetPendingTagDispatch();\n\n        if (pendingTagDispatch is not null)\n        {\n            var pending = pendingTagDispatch;",
      "        if (pendingTagDispatch is not null)\n        {\n            var pending = pendingTagDispatch;"
    ],
    [
      "            if (tagCycle.RetreatPending)\n            {\n                if (now >= nextActionUtc)",
      "            if (tagAttempted)\n            {\n                if (now >= nextActionUtc)"
    ],
    [
      "        if (raiseDialogFirstSeenUtc == DateTime.MinValue &&\n            raiseAcceptanceSubmittedUtc == DateTime.MinValue)\n            return;\n        if (!CanActAfterRevival())\n            return;\n        if (current is not null && !killConfirmed &&\n            tagCycle.ObserveRevival(raiseAcceptanceSubmittedUtc, true, config.RetagAfterRaise))\n        {\n            ResetPendingTagDispatch();\n            ResetTagRecoveryTracking();\n            log.Information(\"Accepted Raise completed; one retag is eligible for {Mark} in pull {Pull}\",\n                current.CreatureName, pullCycle);\n        }\n        log.Information(\n            \"Raise recovery observed: player is conscious;",
      "        if (raiseDialogFirstSeenUtc == DateTime.MinValue &&\n            raiseAcceptanceSubmittedUtc == DateTime.MinValue)\n            return;\n        log.Information(\n            \"Raise recovery observed: player is conscious;"
    ],
    [
      "if (!combat.IsPlayerDead || !pullCycleTagged)",
      "if (!combat.IsPlayerDead || !tagAttempted || !pullCycleTagged)"
    ],
    [
      "if (!killConfirmed || !pullCycleTagged)",
      "if (!killConfirmed || !tagAttempted || !pullCycleTagged)"
    ],
    [
      "has a ranged tag checkpoint awaiting confirmation",
      "crossed the engage threshold before a tag was confirmed"
    ],
    [
      "            if (tagRequired && state != SentinelState.TagApproach &&",
      "            ObserveRaiseRecoveryCompletion(now);\n            if (tagRequired && state != SentinelState.TagApproach &&"
    ],
    [
      "            if (!combat.IsPlayerDead)\n                ObserveRaiseRecoveryCompletion(now);\n            IBattleChara? visibleMark = null;\n            if (CanResolveMarkInState())",
      "            IBattleChara? visibleMark = null;\n            if (CanResolveMarkInState())"
    ],
    [
      "private bool ObservePullCycleReset(IBattleChara target, DateTime now)\n    {\n        if (current is null || !markEverIdentified || !pullCycleCombatObserved ||\n            (!tagRequired && (!pullCycleTagged || !tagAttempted)) ||\n            killConfirmed || target.IsDead || target.CurrentHp == 0 ||\n            clientState.TerritoryType != current.TerritoryId ||\n            !travel.CurrentWorld.Equals(current.World, StringComparison.OrdinalIgnoreCase) ||\n            identifiedMarkGameObjectId == 0 || target.GameObjectId != identifiedMarkGameObjectId)\n        {\n            pullResetCandidateSinceUtc = DateTime.MinValue;\n            return false;\n        }\n\n        var outOfCombatAtFullHealth = !CombatController.IsMarkInCombat(target) &&\n                                      CombatController.HpPercent(target) >= PullResetMinimumHpPercent;\n        if (!outOfCombatAtFullHealth)\n        {\n            pullResetCandidateSinceUtc = DateTime.MinValue;\n            return false;\n        }\n\n        if (pullResetCandidateSinceUtc == DateTime.MinValue)\n        {\n            pullResetCandidateSinceUtc = now;\n            log.Information(\n                \"Reset candidate started: same {Mark} entity, previous combat={PreviousCombat}, previous tag={PreviousTag}, combat=false, HP={Hp:0.0}%, requiring {Seconds:0.0}s stable confirmation\",\n                target.Name.TextValue, pullCycleCombatObserved, pullCycleTagged,\n                CombatController.HpPercent(target), PullResetConfirmationSeconds);\n            return false;\n        }\n\n        if ((now - pullResetCandidateSinceUtc).TotalSeconds < PullResetConfirmationSeconds)\n            return false;\n\n        var completedCycle = pullCycle;\n        var completedCycleTagged = pullCycleTagged;\n        var stableSeconds = (now - pullResetCandidateSinceUtc).TotalSeconds;\n        pullCycle++;\n        pullCycleCombatObserved = false;\n        tagCycle.ResetPull();\n        ResetTagRecoveryTracking();\n        activeTagActionId = 0;\n        ResetPendingTagDispatch();\n        postTagRetreatActive = false;\n        pullResetCandidateSinceUtc = DateTime.MinValue;\n        vnav.StopSafe();\n        log.Information(\n            \"Reset confirmed: same {Mark} entity {ObjectId}, previous combat=true, previous tag={PreviousTag}, combat=false, HP={Hp:0.0}%, stable={Stable:0.0}s; completed pull cycle {PullCycle}\",\n            target.Name.TextValue, target.GameObjectId, completedCycleTagged,\n            CombatController.HpPercent(target), stableSeconds, completedCycle);\n        log.Information(\"Tag gate re-armed for pull cycle {PullCycle}\", pullCycle);\n\n        var resetClearance = ClearanceFromMark(target);\n        if (resetClearance < ActiveDistanceProfile.WaitingDistance ||\n            resetClearance > MaximumParkingClearance + 0.5f ||\n            state != SentinelState.SafeWait)\n        {\n            BeginSafeParking(target, fly: false);\n            status = $\"Reset detected: HP restored and combat ended; tag gate re-armed for pull cycle {pullCycle} and returning to safe parking\";\n        }\n        else\n        {\n            SetState(SentinelState.SafeWait,\n                $\"Reset detected: HP restored and combat ended; tag gate re-armed for pull cycle {pullCycle}\");\n        }\n        return true;\n    }\n",
      "private bool ObservePullCycleReset(IBattleChara target, DateTime now)\n    {\n        if (current is null || !markEverIdentified || !pullCycleCombatObserved ||\n            (!tagRequired && (!pullCycleTagged || !tagAttempted)) ||\n            killConfirmed || target.IsDead || target.CurrentHp == 0 ||\n            clientState.TerritoryType != current.TerritoryId ||\n            !travel.CurrentWorld.Equals(current.World, StringComparison.OrdinalIgnoreCase) ||\n            identifiedMarkGameObjectId == 0 || target.GameObjectId != identifiedMarkGameObjectId)\n        {\n            pullResetCandidateSinceUtc = DateTime.MinValue;\n            return false;\n        }\n\n        var outOfCombatAtFullHealth = !CombatController.IsMarkInCombat(target) &&\n                                      CombatController.HpPercent(target) >= PullResetMinimumHpPercent;\n        if (!outOfCombatAtFullHealth)\n        {\n            pullResetCandidateSinceUtc = DateTime.MinValue;\n            return false;\n        }\n\n        if (pullResetCandidateSinceUtc == DateTime.MinValue)\n        {\n            pullResetCandidateSinceUtc = now;\n            log.Information(\n                \"Reset candidate started: same {Mark} entity, previous combat={PreviousCombat}, previous tag={PreviousTag}, combat=false, HP={Hp:0.0}%, requiring {Seconds:0.0}s stable confirmation\",\n                target.Name.TextValue, pullCycleCombatObserved, pullCycleTagged,\n                CombatController.HpPercent(target), PullResetConfirmationSeconds);\n            return false;\n        }\n\n        if ((now - pullResetCandidateSinceUtc).TotalSeconds < PullResetConfirmationSeconds)\n            return false;\n\n        var completedCycle = pullCycle;\n        var completedCycleTagged = pullCycleTagged;\n        var stableSeconds = (now - pullResetCandidateSinceUtc).TotalSeconds;\n        pullCycle++;\n        tagAttempted = false;\n        pullCycleCombatObserved = false;\n        pullCycleTagged = false;\n        ResetTagRecoveryTracking();\n        activeTagActionId = 0;\n        ResetPendingTagDispatch();\n        postTagRetreatActive = false;\n        pullResetCandidateSinceUtc = DateTime.MinValue;\n        vnav.StopSafe();\n        log.Information(\n            \"Reset confirmed: same {Mark} entity {ObjectId}, previous combat=true, previous tag={PreviousTag}, combat=false, HP={Hp:0.0}%, stable={Stable:0.0}s; completed pull cycle {PullCycle}\",\n            target.Name.TextValue, target.GameObjectId, completedCycleTagged,\n            CombatController.HpPercent(target), stableSeconds, completedCycle);\n        log.Information(\"Tag gate re-armed for pull cycle {PullCycle}\", pullCycle);\n\n        var resetClearance = ClearanceFromMark(target);\n        if (resetClearance < ActiveDistanceProfile.WaitingDistance ||\n            resetClearance > MaximumParkingClearance + 0.5f ||\n            state != SentinelState.SafeWait)\n        {\n            BeginSafeParking(target, fly: false);\n            status = $\"Reset detected: HP restored and combat ended; tag gate re-armed for pull cycle {pullCycle} and returning to safe parking\";\n        }\n        else\n        {\n            SetState(SentinelState.SafeWait,\n                $\"Reset detected: HP restored and combat ended; tag gate re-armed for pull cycle {pullCycle}\");\n        }\n        return true;\n    }\n"
    ],
    [
      "private void ClearCurrent()\n    {\n        worldTravel = null;\n        ResetInstanceRangeRecovery();\n        vnav.StopSafe();\n        ResetParkingFacingSettleTracking();\n        if (current is not null)\n            faloopReportIdsByAlertKey.Remove(current.Key);\n        current = null;\n        mark = null;\n        alertPoint = null;\n        approachPoint = null;\n        safePoint = null;\n        selectedParkingCandidate = null;\n        parkingPathTask = null;\n        parkingGroundPathTask = null;\n        parkingPathStartedUtc = DateTime.MinValue;\n        pendingParkingFlightPath = null;\n        parkingGroundPathGoal = default;\n        selectedParkingPath = null;\n        crowdFallbackAnnounced = false;\n        territoryAetheryteId = 0;\n        killConfirmed = false;\n        postTagRetreatActive = false;\n        markEverIdentified = false;\n        markCombatObserved = false;\n        pullCycleCombatObserved = false;\n        tagCycle.ResetPull();\n        ResetTagRecoveryTracking();\n        identifiedMarkGameObjectId = 0;\n        pullCycle = 1;\n        pullResetCandidateSinceUtc = DateTime.MinValue;\n        activeTagActionId = 0;\n        ResetPendingTagDispatch();\n        discardAtUldah = false;\n        discardReason = string.Empty;\n        ssChainObserved = false;\n        ssSpawnAnnounced = false;\n        activeSsProfile = null;\n        postKillSsGraceDeadlineUtc = DateTime.MinValue;\n        ssWatchDeadlineUtc = DateTime.MinValue;\n        ClearPendingSsChainEvidence();\n        ResetSsStagingTracking();\n        playerReadySinceUtc = DateTime.MinValue;\n        lastMarkSeenUtc = DateTime.MinValue;\n        ResetReturnRecoveryTracking();\n        resetToUldahAllowsLiveEntityExit = false;\n        resetToUldahRequestSource = HuntExitRequestSource.AutomaticStateTransition;\n        resetToUldahRequestedFromState = SentinelState.Idle;\n        resetToUldahRequestReason = string.Empty;\n        ResetRaiseTracking();\n        ResetReturnLandingRecovery();\n        ResetIncidentalAggroTracking();\n        ResetParkingRecoveryTracking();\n        parkingCandidates.Clear();\n        ResetApproachRouteTracking(clearProjectionCandidates: true);\n        ResetLocalApproachRecovery();\n        ResetLocateSearchTracking();\n    }\n",
      "private void ClearCurrent()\n    {\n        worldTravel = null;\n        ResetInstanceRangeRecovery();\n        vnav.StopSafe();\n        ResetParkingFacingSettleTracking();\n        if (current is not null)\n            faloopReportIdsByAlertKey.Remove(current.Key);\n        current = null;\n        mark = null;\n        alertPoint = null;\n        approachPoint = null;\n        safePoint = null;\n        selectedParkingCandidate = null;\n        parkingPathTask = null;\n        parkingGroundPathTask = null;\n        parkingPathStartedUtc = DateTime.MinValue;\n        pendingParkingFlightPath = null;\n        parkingGroundPathGoal = default;\n        selectedParkingPath = null;\n        crowdFallbackAnnounced = false;\n        territoryAetheryteId = 0;\n        killConfirmed = false;\n        tagAttempted = false;\n        postTagRetreatActive = false;\n        markEverIdentified = false;\n        markCombatObserved = false;\n        pullCycleCombatObserved = false;\n        pullCycleTagged = false;\n        ResetTagRecoveryTracking();\n        identifiedMarkGameObjectId = 0;\n        pullCycle = 1;\n        pullResetCandidateSinceUtc = DateTime.MinValue;\n        activeTagActionId = 0;\n        ResetPendingTagDispatch();\n        discardAtUldah = false;\n        discardReason = string.Empty;\n        ssChainObserved = false;\n        ssSpawnAnnounced = false;\n        activeSsProfile = null;\n        postKillSsGraceDeadlineUtc = DateTime.MinValue;\n        ssWatchDeadlineUtc = DateTime.MinValue;\n        ClearPendingSsChainEvidence();\n        ResetSsStagingTracking();\n        playerReadySinceUtc = DateTime.MinValue;\n        lastMarkSeenUtc = DateTime.MinValue;\n        ResetReturnRecoveryTracking();\n        resetToUldahAllowsLiveEntityExit = false;\n        resetToUldahRequestSource = HuntExitRequestSource.AutomaticStateTransition;\n        resetToUldahRequestedFromState = SentinelState.Idle;\n        resetToUldahRequestReason = string.Empty;\n        ResetRaiseTracking();\n        ResetReturnLandingRecovery();\n        ResetIncidentalAggroTracking();\n        ResetParkingRecoveryTracking();\n        parkingCandidates.Clear();\n        ResetApproachRouteTracking(clearProjectionCandidates: true);\n        ResetLocalApproachRecovery();\n        ResetLocateSearchTracking();\n    }\n"
    ],
    [
      "private void StartSsAlertDirect(HuntAlertSnapshot alert, string source)\n    {\n        vnav.StopSafe();\n        current = alert;\n        mark = null;\n        killConfirmed = false;\n        postTagRetreatActive = false;\n        markEverIdentified = false;\n        markCombatObserved = false;\n        pullCycleCombatObserved = false;\n        tagCycle.ResetPull();\n        ResetTagRecoveryTracking();\n        identifiedMarkGameObjectId = 0;\n        pullCycle = 1;\n        pullResetCandidateSinceUtc = DateTime.MinValue;\n        activeTagActionId = 0;\n        ResetPendingTagDispatch();\n        discardAtUldah = false;\n        discardReason = string.Empty;\n        ssChainObserved = true;\n        ssSpawnAnnounced = true;\n        activeSsProfile = HuntCatalog.GetSsProfileForSsName(alert.CreatureName) ??\n                          HuntCatalog.GetSsProfileForTerritory(alert.TerritoryId);\n        postKillSsGraceDeadlineUtc = DateTime.MinValue;\n        ssWatchDeadlineUtc = DateTime.MinValue;\n        ClearPendingSsChainEvidence();\n        ResetSsStagingTracking();\n        playerReadySinceUtc = DateTime.MinValue;\n        lastMarkSeenUtc = DateTime.MinValue;\n        ResetReturnRecoveryTracking();\n        ResetRaiseTracking();\n        ResetIncidentalAggroTracking();\n        ResetParkingRecoveryTracking();\n        PrepareCurrentTravel();\n\n        // A real loaded SS entity always supersedes static/provider coordinates. Provider links\n        // can arrive during the prey phase and must not pull staging away from the documented\n        // territory spawn location.\n        var visibleSs = FindMark();\n        if (visibleSs is not null)\n        {\n            mark = visibleSs;\n            MarkWasIdentified(visibleSs);\n            SetState(SentinelState.LocateMark,\n                $\"{source}: {alert.CreatureName} is already visible; switching to dynamic safe parking\");\n            return;\n        }\n\n        if (!vnav.IsReadySafe())\n        {\n            BeginMeshWait($\"{source}: prioritizing {alert.CreatureName} directly without an Ul'dah reset\");\n            return;\n        }\n\n        SetState(SentinelState.PrepareApproachDestination,\n            $\"{source}: prioritizing {alert.CreatureName} directly from its alert coordinates\");\n    }\n",
      "private void StartSsAlertDirect(HuntAlertSnapshot alert, string source)\n    {\n        vnav.StopSafe();\n        current = alert;\n        mark = null;\n        killConfirmed = false;\n        tagAttempted = false;\n        postTagRetreatActive = false;\n        markEverIdentified = false;\n        markCombatObserved = false;\n        pullCycleCombatObserved = false;\n        pullCycleTagged = false;\n        ResetTagRecoveryTracking();\n        identifiedMarkGameObjectId = 0;\n        pullCycle = 1;\n        pullResetCandidateSinceUtc = DateTime.MinValue;\n        activeTagActionId = 0;\n        ResetPendingTagDispatch();\n        discardAtUldah = false;\n        discardReason = string.Empty;\n        ssChainObserved = true;\n        ssSpawnAnnounced = true;\n        activeSsProfile = HuntCatalog.GetSsProfileForSsName(alert.CreatureName) ??\n                          HuntCatalog.GetSsProfileForTerritory(alert.TerritoryId);\n        postKillSsGraceDeadlineUtc = DateTime.MinValue;\n        ssWatchDeadlineUtc = DateTime.MinValue;\n        ClearPendingSsChainEvidence();\n        ResetSsStagingTracking();\n        playerReadySinceUtc = DateTime.MinValue;\n        lastMarkSeenUtc = DateTime.MinValue;\n        ResetReturnRecoveryTracking();\n        ResetRaiseTracking();\n        ResetIncidentalAggroTracking();\n        ResetParkingRecoveryTracking();\n        PrepareCurrentTravel();\n\n        // A real loaded SS entity always supersedes static/provider coordinates. Provider links\n        // can arrive during the prey phase and must not pull staging away from the documented\n        // territory spawn location.\n        var visibleSs = FindMark();\n        if (visibleSs is not null)\n        {\n            mark = visibleSs;\n            MarkWasIdentified(visibleSs);\n            SetState(SentinelState.LocateMark,\n                $\"{source}: {alert.CreatureName} is already visible; switching to dynamic safe parking\");\n            return;\n        }\n\n        if (!vnav.IsReadySafe())\n        {\n            BeginMeshWait($\"{source}: prioritizing {alert.CreatureName} directly without an Ul'dah reset\");\n            return;\n        }\n\n        SetState(SentinelState.PrepareApproachDestination,\n            $\"{source}: prioritizing {alert.CreatureName} directly from its alert coordinates\");\n    }\n"
    ],
    [
      "private void StartAlert(HuntAlertSnapshot alert, string source)\n    {\n        worldTravel = null;\n        if (HuntCatalog.IsAnySsName(alert.CreatureName) &&\n            HuntCatalog.TryGetSsStagingLocation(alert.TerritoryId, out var ssLocation))\n            alert = alert with { MapX = ssLocation.MapX, MapY = ssLocation.MapY };\n        current = alert;\n        mark = null;\n        killConfirmed = false;\n        postTagRetreatActive = false;\n        markEverIdentified = false;\n        markCombatObserved = false;\n        pullCycleCombatObserved = false;\n        tagCycle.ResetPull();\n        ResetTagRecoveryTracking();\n        identifiedMarkGameObjectId = 0;\n        pullCycle = 1;\n        pullResetCandidateSinceUtc = DateTime.MinValue;\n        activeTagActionId = 0;\n        ResetPendingTagDispatch();\n        discardAtUldah = false;\n        discardReason = string.Empty;\n        activeSsProfile = HuntCatalog.GetSsProfileForSsName(alert.CreatureName);\n        ssChainObserved = activeSsProfile is not null;\n        ssSpawnAnnounced = activeSsProfile is not null && !IsReservedSsWatch(alert);\n        postKillSsGraceDeadlineUtc = DateTime.MinValue;\n        ssWatchDeadlineUtc = DateTime.MinValue;\n        ClearPendingSsChainEvidence();\n        ResetSsStagingTracking();\n        playerReadySinceUtc = DateTime.MinValue;\n        lastMarkSeenUtc = DateTime.MinValue;\n        ResetReturnRecoveryTracking();\n        resetToUldahAllowsLiveEntityExit = false;\n        resetToUldahRequestSource = HuntExitRequestSource.AutomaticStateTransition;\n        resetToUldahRequestedFromState = SentinelState.Idle;\n        resetToUldahRequestReason = string.Empty;\n        ResetRaiseTracking();\n        ResetIncidentalAggroTracking();\n        ResetParkingRecoveryTracking();\n        PrepareCurrentTravel();\n        log.Information(\n            \"Hunt activated: {Mark} on {World}, territory {Territory}, instance {Instance}; positive kill evidence remains required\",\n            alert.CreatureName, alert.World, alert.TerritoryId, alert.Instance);\n        SetState(SentinelState.ResetToUldah,\n            $\"{source}: resetting through Ul'dah before {alert.CreatureName} on {alert.World}\");\n    }\n",
      "private void StartAlert(HuntAlertSnapshot alert, string source)\n    {\n        worldTravel = null;\n        if (HuntCatalog.IsAnySsName(alert.CreatureName) &&\n            HuntCatalog.TryGetSsStagingLocation(alert.TerritoryId, out var ssLocation))\n            alert = alert with { MapX = ssLocation.MapX, MapY = ssLocation.MapY };\n        current = alert;\n        mark = null;\n        killConfirmed = false;\n        tagAttempted = false;\n        postTagRetreatActive = false;\n        markEverIdentified = false;\n        markCombatObserved = false;\n        pullCycleCombatObserved = false;\n        pullCycleTagged = false;\n        ResetTagRecoveryTracking();\n        identifiedMarkGameObjectId = 0;\n        pullCycle = 1;\n        pullResetCandidateSinceUtc = DateTime.MinValue;\n        activeTagActionId = 0;\n        ResetPendingTagDispatch();\n        discardAtUldah = false;\n        discardReason = string.Empty;\n        activeSsProfile = HuntCatalog.GetSsProfileForSsName(alert.CreatureName);\n        ssChainObserved = activeSsProfile is not null;\n        ssSpawnAnnounced = activeSsProfile is not null && !IsReservedSsWatch(alert);\n        postKillSsGraceDeadlineUtc = DateTime.MinValue;\n        ssWatchDeadlineUtc = DateTime.MinValue;\n        ClearPendingSsChainEvidence();\n        ResetSsStagingTracking();\n        playerReadySinceUtc = DateTime.MinValue;\n        lastMarkSeenUtc = DateTime.MinValue;\n        ResetReturnRecoveryTracking();\n        resetToUldahAllowsLiveEntityExit = false;\n        resetToUldahRequestSource = HuntExitRequestSource.AutomaticStateTransition;\n        resetToUldahRequestedFromState = SentinelState.Idle;\n        resetToUldahRequestReason = string.Empty;\n        ResetRaiseTracking();\n        ResetIncidentalAggroTracking();\n        ResetParkingRecoveryTracking();\n        PrepareCurrentTravel();\n        log.Information(\n            \"Hunt activated: {Mark} on {World}, territory {Territory}, instance {Instance}; positive kill evidence remains required\",\n            alert.CreatureName, alert.World, alert.TerritoryId, alert.Instance);\n        SetState(SentinelState.ResetToUldah,\n            $\"{source}: resetting through Ul'dah before {alert.CreatureName} on {alert.World}\");\n    }\n"
    ],
    [
      "    private readonly TagCheckpointCycle tagCycle = new();\n    private bool pullCycleTagged => tagCycle.HasConfirmedTag;\n    private bool tagRequired => tagCycle.Pending is not null;",
      "    private bool pullCycleTagged;\n    private bool tagRequired;"
    ],
    [
      "    private bool markEverIdentified;\n",
      "    private bool tagAttempted;\n    private bool markEverIdentified;\n"
    ],
    [
      "        var currentMatched = current is not null && Matches(current);\n        ObserveKnownHistoryDeaths(world, territory, instance, deathAtUtc,\n            name => HuntCatalog.NamesMatch(name, creature));\n        var removed = pendingAlerts.Where(Matches).ToArray();",
      "        var currentMatched = current is not null && Matches(current);\n        var removed = pendingAlerts.Where(Matches).ToArray();"
    ],
    [
      "                ObserveKnownHistoryDeaths(killedWorld, killedTerritory, killedInstance, DateTime.UtcNow,\n                    name => HuntCatalog.TextMentionsMark(text, name));\n                RemoveKilledQueuedAlerts(text, killedWorld, killedTerritory, killedInstance);",
      "                RemoveKilledQueuedAlerts(text, killedWorld, killedTerritory, killedInstance);"
    ],
    [
      "status = $\"Removed {removed} queued hunt(s) from disabled expansions or Data Centers\";",
      "status = $\"Removed {removed} queued hunt(s) from disabled expansions\";"
    ],
    [
      "    private void RemoveDisabledQueuedAlerts()\n    {\n        PruneUnresolvedFaloopAlerts();\n        var survivors = pendingAlerts\n            .Where(alert => config.IsExpansionEnabled(HuntCatalog.GetExpansion(alert.TerritoryId)) &&\n                            IsHuntDataCenterEnabled(alert.World))",
      "    private void RemoveDisabledQueuedAlerts()\n    {\n        var survivors = pendingAlerts\n            .Where(alert => config.IsExpansionEnabled(HuntCatalog.GetExpansion(alert.TerritoryId)))"
    ],
    [
      "                           !config.IsExpansionEnabled(HuntCatalog.GetExpansion(pair.Value.Alert.TerritoryId)) ||\n                           !IsHuntDataCenterEnabled(pair.Value.Alert.World))",
      "                           !config.IsExpansionEnabled(HuntCatalog.GetExpansion(pair.Value.Alert.TerritoryId)))"
    ],
    [
      "    private bool IsHuntDataCenterEnabled(string world) =>\n        config.HuntDataCenters == NorthAmericanDataCenters.All ||\n        DataCenterHunting.Allows(travel.ResolveVisitWorld(world), config.HuntDataCenters);\n\n    private bool IsQueuedAlertFresh(HuntAlertSnapshot alert, DateTime now) =>\n        IsHuntDataCenterEnabled(alert.World) && IsAlertFresh(alert, now);\n\n    private bool IsAlertFresh(HuntAlertSnapshot alert, DateTime now) =>",
      "    private bool IsAlertFresh(HuntAlertSnapshot alert, DateTime now) =>"
    ],
    [
      "var valid = queued.Where(candidate => IsQueuedAlertFresh(candidate, now))",
      "var valid = queued.Where(candidate => IsAlertFresh(candidate, now))"
    ],
    [
      "if (IsQueuedAlertFresh(alert, now) && pendingAlerts.All(existing => existing.Key != alert.Key))",
      "if (IsAlertFresh(alert, now) && pendingAlerts.All(existing => existing.Key != alert.Key))"
    ],
    [
      "        if (ssAlert is not null && !IsHuntDataCenterEnabled(ssAlert.World))\n        {\n            nextActionUtc = now;\n            SetState(SentinelState.ResetToUldah, $\"{reason}; SS follow-up Data Center is disabled\");\n            return;\n        }\n        if (ssAlert is null)\n        {\n            log.Warning(\n                \"Could not reserve {Ss} after completed {Mark}: fixed SS staging coordinates are unavailable\",",
      "        if (ssAlert is null)\n        {\n            log.Warning(\n                \"Could not reserve {Ss} after completed {Mark}: fixed SS staging coordinates are unavailable\","
    ],
    [
      "        var dcEnabled = IsHuntDataCenterEnabled(world);\n        LogProviderRegion(source, \"spawn\", world, eligible && dcEnabled);\n        if (!eligible || !dcEnabled)\n        {\n            status = $\"Ignored {source} alert for {creature.Trim()} on {world}: \" +\n                     (!eligible ? \"outside the current World Visit region\" : \"its logical Data Center is disabled\");\n            return;\n        }",
      "        LogProviderRegion(source, \"spawn\", world, eligible);\n        if (!eligible)\n        {\n            status = $\"Ignored {source} alert for {creature.Trim()} on {world}: outside the current World Visit region\";\n            return;\n        }"
    ],
    [
      "        if (!IsHuntDataCenterEnabled(world))\n            return;\n\n        var expansion = HuntCatalog.GetExpansion(territory);\n        if (!config.IsExpansionEnabled(expansion))\n        {\n            log.Debug(\"Ignored supported S/SS event",
      "        var expansion = HuntCatalog.GetExpansion(territory);\n        if (!config.IsExpansionEnabled(expansion))\n        {\n            log.Debug(\"Ignored supported S/SS event"
    ]
  ],
  "HuntProgressPolicy.cs": [
    [
      "    public static bool IsConfirmedPullReset(\n ",
      "    public static bool ShouldLatchTag(\n        bool markAlive,\n        bool markInCombat,\n        float hpPercent,\n        float engageThreshold,\n        bool tagConfirmed) =>\n        markAlive && markInCombat && hpPercent <= engageThreshold && !tagConfirmed;\n\n    public static bool IsConfirmedPullReset(\n "
    ]
  ]
};
const baseline = JSON.parse(read("docs/sentinel-modern-hunt-baseline.json"));
for (const [path, expectedHash] of Object.entries(baseline.sha256)) {
  let source = read(path);
  for (const [change, original] of reviewedWorldVisitDeathPatches[path] ?? []) {
    assert(source.split(change).length === 2, `Missing/duplicated World Visit death cancellation edit: ${path}`);
    source = source.replace(change, original);
  }
  for (const [change, original] of reviewedCheckpointPatches[path] ?? []) {
    assert(source.split(change).length === 2, `Missing/duplicated DC or checkpoint edit: ${path}`);
    source = source.replace(change, original);
  }
  for (const [change, original] of reviewedLifestreamPatches[path] ?? []) {
    assert(source.split(change).length === 2, `Missing/duplicated Lifestream handoff edit: ${path}`);
    source = source.replace(change, original);
  }
  for (const [change, original] of reviewedWorldVisitPatches[path] ?? []) {
    assert(source.split(change).length === 2, `Missing/duplicated regional travel edit: ${path}`);
    source = source.replace(change, original);
  }
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
console.log("Sentinel Modern 2 audit passed: exact Core 0.3.1 packages, shared UI, passive history hooks, and baseline hunt sources after removing exact reviewed travel changes.");
