# Sentinel Modern integration

SRankSentinel 0.7.50.0 consumes the published Core and Core.UI packages from SentinelCore
v0.3.1.0, commit `300703b360a58fb4b73bf7675d31fe8cab4614cd`. Sentinel Core owns the visual
language shared with Sentinel HUD.

`sentinelcore-packages.json` pins release URLs, package IDs, and SHA256 hashes. Core.UI's
package hash is `e1a9ce4e1ce36042c0fcd53f4c23874d918640be10eef16c21f1cd436c6ba747`.
Both project references use exact `[0.3.1]` constraints, and `packages.lock.json` records
the published NuGet content hashes. `NuGet.Config` maps those IDs to the verified offline
feed. Downloaded packages are ignored by git. Core.Dalamud is not consumed, and users do
not install SentinelCore separately.

## Build

With .NET 10 and Dalamud API 15 development files, run from the repository root:

```powershell
./.github/scripts/Prepare-SentinelCore.ps1
dotnet restore SRankSentinel.csproj --locked-mode
./.github/scripts/Prepare-SentinelCore.ps1 -VerifyOnly -AssetsPath obj/project.assets.json
node .github/scripts/Audit-SentinelModern.mjs
dotnet run --project SRankSentinel.StateTests -c Release
dotnet build SRankSentinel.csproj -c Release --no-restore
./.github/scripts/Prepare-UiTestFont.ps1 -Destination "$env:TEMP/Sentinel-FontAwesomeFreeSolid.otf"
$env:DALAMUD_ICON_FONT = "$env:TEMP/Sentinel-FontAwesomeFreeSolid.otf"
dotnet run --project SRankSentinel.UiTests -c Release
./.github/scripts/Validate-Package.ps1 -PackagePath bin/Release/SRankSentinel/latest.zip -ExpectedVersion 0.7.50.0
```

The font preparation script verifies the official Dalamud Font Awesome asset from a pinned
commit. Local tests can also use an existing Dalamud asset or `DALAMUD_ICON_FONT`.
CI and publication run the same tests and guards. Package validation requires Core and
Core.UI assemblies at the ZIP root, version 0.3.1.0, correct runtime dependency entries,
and exact DLL bytes from the published packages. Tests, development executables, symbols,
source files, and NuGet packages are rejected from the public ZIP. Fresh-install validation
downloads the actual public release and repeats the checks.

## Presentation and preservation

`Plugin.Ui.cs` retains both presentations and their shared settings/action handlers. Classic
keeps its compact one-page controls and native window title bar. Modern uses
`SentinelModernWindowChrome.UseCustomHeader`, `SentinelModernStyleScope.PushAppShell`, and
`SentinelModernAppShell` with `SentinelModernAppSurfaceStyle.Unified`. Core owns the compact
56 logical pixel header, Font Awesome icon placement, left rail, cards, settings rows,
switches, status pill, motion, and procedural ambience at intensity 0.9.

The rail stays on the left, without secondary navigation for these two pages. Main combines
hunt status, expansion switches, and recovery controls; Distance Profiles retains its
existing values and invariants. Both start directly with settings, without redundant page-top
information. One rail action switches to Classic through the same persisted theme setter.
Classic retains its theme selector. The shared native Core switch supports mouse, keyboard,
and controller activation, so the previous consumer activation bridge is removed.

Modern's expanded minimum is 620 x 520 logical pixels. It retains the original window ID
`S Rank Sentinel###SRankSentinel`, saved position, and larger saved size. The custom minimize
action keeps the header visible and saves the expanded dimensions; expanding restores those
dimensions. Close uses the existing open flag, header dragging moves the original top-level
window, and its native resize grip remains available. The consumer honors Dalamud's reduced
motion setting. Every style, font, and Begin/End scope remains balanced across theme changes.

Configuration schema 16 and its existing Classic migration remain unchanged. Main keeps ID 0
and Distance Profiles ID 2; retired Hunting, Recovery Controls, and Appearance page selections
normalize to Main. Theme selection, hunt configuration, credentials, and queues persist.
The migration audit compares hunt/service source hashes against the existing reviewed baseline;
this release changes presentation only.

## Validation and distribution

The 29 state-machine tests exercise existing hunt policies. The 17 UI/migration test groups
use real native ImGui and configuration serialization in isolated contexts, without connecting
to FFXIV or submitting hunt actions. They cover canonical switch input and disabled state,
the sidebar Classic action with all three input methods, window identity and persistence,
custom minimize/close, header dragging, native resizing, reduced motion, and scope balancing.
The actual consumer is rendered at 620 x 520, 800 x 640, and 1040 x 860 logical pixels at
100%, 150%, and 200% UI scale. Native child geometry checks verify the left rail, header without
scrollbars, and control bounds inside responsive rows. The runner reports managed exceptions
and exits with failure instead of an unhandled .NET crash popup. In-game visual acceptance
remains a separate user observation.

Publish Beta updates this repository's authoritative `repo.json`, verifies the public ZIP,
then sends `plugin-released` to the Sentinel catalog generator using `DALAMUD_CATALOG_TOKEN`.
The generator owns `MarshalTitan/Sentinel/repo.json`; this repository does not write it directly.
The workflow waits for the generated version and validates the central public install path.
