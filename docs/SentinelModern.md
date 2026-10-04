# Sentinel Modern integration

SRankSentinel 0.7.48.0 uses the published Core and Core.UI packages from SentinelCore
v0.2.1.0, commit d1c5798b42cc1e542db3786deaf03e449a991cd9. The approved visual
reference is Sentinel HUD 0.8.3.0 at 622429c8eceeafae35e078b9b053aa71ca9f87fd.

The package IDs, immutable release URLs, and SHA256 hashes are pinned in
`sentinelcore-packages.json`. `NuGet.Config` maps these IDs to the verified local feed.
Both references use exact `[0.2.1]` constraints. Core.Dalamud is not consumed.

## Build

Run these steps from the repository root with .NET 10 and Dalamud API 15 development files:

```powershell
./.github/scripts/Prepare-SentinelCore.ps1
dotnet restore SRankSentinel.csproj
./.github/scripts/Prepare-SentinelCore.ps1 -VerifyOnly -AssetsPath obj/project.assets.json
node .github/scripts/Audit-SentinelModern.mjs
dotnet run --project SRankSentinel.StateTests -c Release
dotnet build SRankSentinel.csproj -c Release --no-restore
dotnet run --project SRankSentinel.UiTests -c Release
./.github/scripts/Validate-Package.ps1 -PackagePath bin/Release/SRankSentinel/latest.zip -ExpectedVersion 0.7.48.0
```

The build and publication workflows run the same guards and tests. Downloaded packages are
ignored by git; their complete byte hashes are checked before restore and their NuGet content
hashes are compared with the resolved assets after restore. Package validation verifies
`SentinelCore.dll` and `SentinelCore.UI.dll` at the ZIP root, assembly version 0.2.1.0,
runtime dependency entries, and exact DLL hashes against the published packages. Development
executables, test assemblies, debug symbols, source files, and packages are rejected from the ZIP.
Fresh public-install validation repeats these checks using the public catalog download.

## Presentation and preservation

`Plugin.Ui.cs` contains both presentations and their shared settings/action handlers.
Classic retains the one-page control order; Modern uses Core's shell, navigation, page cards,
style, switches, headings, status chip, and ambient background. No canonical primitive is
defined or drawn in this consumer. Modern uses Core 0.2.1's default shell: an 84 logical pixel
header without scrollbars, a left sidebar at every supported width, and headings/settings
together in the right content pane. Stacked navigation and local layout overrides are disabled.
The Modern minimum is 620 x 520 logical pixels, scaled with Dalamud's UI scale.

Both modes use the unchanged `S Rank Sentinel###SRankSentinel` top-level window ID and
first-use Classic size. The shared shell draws inside the existing ImGui window, preserving
its native close/collapse controls and saved position. A frame retains its starting style even
when the user changes themes during that frame; all style and window scopes are balanced.

Configuration schema 16 explicitly migrates existing users to Classic and preserves every
hunt/configuration value and queue. Modern selection and selected page persist in that same
configuration. Main combines hunt status, expansion switches, recovery controls, and appearance
settings. Both Main and Distance Profiles start directly with their controls, without a separate
page heading or introductory text. Main's persisted page ID is 0; Distance Profiles retains ID 2.
Saved Hunting (1), Recovery Controls (3), and Appearance (4)
selections normalize to Main through the existing migration, retaining the user's selected
theme and all other settings. Invalid
presentation values normalize to Classic/Main.

Core 0.2.1's switch uses a non-navigable InvisibleButton. `ConsumerToggleActivation` invokes
that exact shared renderer, then overlays a transparent native Button solely for navigation
activation and the native focus cursor. Core owns pointer toggling; mouse release is excluded
from the bridge to avoid double activation. There is no local track, knob, palette, or style
implementation.

The migration audit pins the unchanged hunt/service sources to the verified 0.7.44.0 baseline.
For Plugin.cs the only changes are extracting its UI methods and declaring the class partial.
Future deliberate hunt changes must update that baseline evidence along with their regression
tests.

## Validation scope

State tests exercise the existing 29 hunt policies. UI tests exercise real configuration
migration/serialization and native ImGui mouse, keyboard, controller, disabled-control,
shell/card, and style-restoration behavior in an isolated context. They do not connect to FFXIV
or submit hunt actions. The 13 test groups also cover merged-page selection migration,
render every consumer page at 620 x 520, assert
left navigation/header flags, check settings fit the right pane, retain saved position/larger
size across theme switches, and cover restored collapsed windows. The runner prints managed
exceptions and returns failure instead of raising an unhandled .NET crash popup.
An in-game visual check remains the final user observation after update.
