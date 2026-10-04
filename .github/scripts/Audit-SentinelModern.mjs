import { readFileSync, readdirSync } from "node:fs";
import { createHash } from "node:crypto";
import { join } from "node:path";

const read = path => readFileSync(path, "utf8").replace(/\r/g, "");
const assert = (condition, message) => { if (!condition) throw new Error(message); };
const project = read("SRankSentinel.csproj");
const pin = JSON.parse(read("sentinelcore-packages.json"));
assert(pin.version === "0.2.1" && pin.release === "v0.2.1.0" &&
  pin.commit === "d1c5798b42cc1e542db3786deaf03e449a991cd9", "Wrong Core release pin.");
const expected = {
  "MarshalTitan.SentinelCore": "5b80a7d8063b1371a11fe87cbe4a6b2e02fd1d492b5389ad4e242d5f1b4a211b",
  "MarshalTitan.SentinelCore.UI": "87adc6ad755500e44a4e719448e83e7f753c0f7eeba5158c1796dc65f25b6207"
};
assert(pin.packages.length === 2, "Only Core and Core.UI are required.");
for (const [id, hash] of Object.entries(expected)) {
  const packagePin = pin.packages.find(p => p.id === id);
  assert(packagePin?.sha256 === hash && packagePin.url ===
    `https://github.com/MarshalTitan/SentinelCore/releases/download/v0.2.1.0/${id}.0.2.1.nupkg`,
    `Wrong published package/hash: ${id}`);
  assert(project.includes(`<PackageReference Include="${id}" Version="[0.2.1]" />`),
    `Reference must pin exact published version: ${id}`);
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
for (const api of ["SentinelModernStyleScope", "SentinelModernConfigurationShell",
  "SentinelModernNavigation", "SentinelModernCard", "SentinelModernUi.SectionHeader"]) {
  assert(ui.includes(api), `Shared UI component missing: ${api}`);
}
assert(ui.includes('ConfigurationWindowId = "S Rank Sentinel###SRankSentinel"'),
  "Saved window identity changed.");
assert(ui.includes("new Vector2(680, 720), ImGuiCond.FirstUseEver") &&
  !ui.includes("SetNextWindowPos") && !ui.includes("NoCollapse") && !ui.includes("NoNav") &&
  !ui.includes("NoTitleBar"), "Classic size/position or standard window controls regressed.");
assert(ui.includes("new Vector2(620f, 520f) * scale") &&
  !/HeaderHeight\s*=|Layout\s*=|AllowStackedNavigation|CompactBreakpoint|IsCompact/.test(ui),
  "Use Core 0.2.1's default left sidebar and non-scrolling 84px header, with a 620x520 minimum.");
for (const helper of ["DrawEnabledControl", "DrawExpansionControls", "DrawDistanceControls",
  "DrawRecoveryControls", "DrawAppearanceControls"]) {
  assert(ui.split(helper + "();").length >= 3, `Themes must share ${helper}`);
}
assert(!/AddRect|AddCircle|DrawRings|new Vector4/.test(ui),
  "The consumer must not draw canonical Modern primitives.");
const bridge = read("ConsumerToggleActivation.cs");
assert(bridge.includes("SentinelModernControls.Toggle(") && bridge.includes('ImGui.Button("##Activation"') &&
  bridge.includes("ImGui.SetItemAllowOverlap()") && bridge.includes("!ImGui.IsMouseReleased") &&
  !/AddRect|AddCircle|DrawRings/.test(bridge), "Keyboard/controller toggle activation bridge regressed.");
const config = read("Configuration.cs");
assert(config.includes("if (Version < 16)") && config.includes("WindowTheme = 0;") &&
  config.includes("WindowPage = 0;") && config.includes("MigrateWindowAppearance();") &&
  config.includes("NormalizeTheme(WindowTheme)"), "Explicit Classic migration/normalization missing.");

const baseline = JSON.parse(read("docs/sentinel-modern-hunt-baseline.json"));
for (const [path, expectedHash] of Object.entries(baseline.sha256)) {
  const actual = createHash("sha256").update(read(path).trim() + "\n").digest("hex");
  assert(actual === expectedHash, `UI-only migration changed hunt source: ${path}`);
}
for (const workflow of ["build.yml", "release.yml"]) {
  const content = read(join(".github/workflows", workflow));
  for (const check of ["Audit-SentinelModern.mjs", "Prepare-SentinelCore.ps1",
    "-AssetsPath", "SRankSentinel.UiTests", "Validate-Package.ps1"]) {
    assert(content.includes(check), `${workflow} must enforce ${check}`);
  }
}
console.log("Sentinel Modern audit passed: exact published packages, canonical shared UI, Classic migration, saved window identity, shared handlers, navigation activation bridge, unchanged hunt sources, and CI/release guards.");
