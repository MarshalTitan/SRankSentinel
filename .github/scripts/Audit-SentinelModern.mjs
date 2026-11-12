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
const baseline = JSON.parse(read("docs/sentinel-modern-hunt-baseline.json"));
for (const [path, expectedHash] of Object.entries(baseline.sha256)) {
  let source = read(path);
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
