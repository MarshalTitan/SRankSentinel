# Sentinel Modern integration

SRankSentinel 0.7.54.0 consumes the published Core and Core.UI packages from SentinelCore
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
./.github/scripts/Validate-Package.ps1 -PackagePath bin/Release/SRankSentinel/latest.zip -ExpectedVersion 0.7.54.0
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

The rail stays on the left, without secondary navigation. Main combines
hunt status, expansion switches, and recovery controls; Distance Profiles retains its
existing values and invariants. Both start directly with settings, without redundant page-top
information. Theme (persisted page ID 7) uses the ecosystem palette icon. Its compact
170 x 34 logical pixel bottom-left Core action-dock button says "Use Classic Theme" and
uses the same persisted theme setter. The dock is absent on every other page, freeing
space for settings and history. Retired Appearance page ID 4 still migrates to Main.
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
The migration audit compares hunt/service source hashes against the existing reviewed baseline,
after removing exactly five allowlisted history-observer insertions. No hunt decisions change.

## Companion plugins and history

Plugins (persisted page ID 5) lists vnavmesh, Lifestream, HuntAlerts, and Sonar with their live
installed/loaded status and settings/installer actions. vnavmesh supplies automatic movement;
HuntAlerts and Sonar supply the existing production alerts. Lifestream is a travel companion,
not a new travel dependency: native World Visit and teleport remain unchanged.
The ecosystem Plug icon matches Sentinel HUD. Canonical Core glass cards use Core's
enabled green and error red tints for the whole card, Font Awesome check/cross icons,
requirement pills, setup guidance, and small actions. Native content-table columns
keep wrapped descriptions separate from controls. Readiness requires
vnavmesh and at least one alert source; optional companions are not presented as blockers.

An explicit Enable action uses an isolated API-15 compatibility bridge because Dalamud's
public exposed-plugin interface has no enable method. It follows the native installer's
default-profile persistence and single-plugin loading order. Unsupported builds, custom
collections, safe mode, outdated/unavailable plugins, and busy/error states fail closed and
offer the installer. It never installs, disables, or unloads plugins, changes collection rules,
or enables a plugin automatically. CI verifies the bridge's real Dalamud method signatures.

History (persisted page ID 6) has one newest-first, independently scrolling spawn list capped
at 500 reports. Each row includes its tag, kill, and confirmed credit status and reward when
available; the duplicate credited-hunts section has been removed. New supported current-DC
reports are recorded only while Sentinel is enabled and the territory's existing expansion
filter is enabled, including SS reports. Turning a filter off hides earlier saved reports
without deleting them; turning it back on reveals those records and permits new ones.
Active tag/kill/reward evidence remains valid across a filter change. Report time is not
claimed as exact server spawn time, and old suppression records are not backfilled as credit.

Four Core summary cards count visible reports, reports today, tagged reports, and reports
with confirmed credit. The seven-day data chart stacks untagged (Core accent blue) and tagged
(Core teal) segments on each local report date, with daily hover tooltips. A tag
is a subset of that day's reports, including a tag occurring later, so the total bar never
double counts it. The consumer draws only chart data segments; Core owns the card and colours.
Empty states remain truthful. The report list and outer page scroll where needed.

One Clear History button clears the saved reports and legacy credited-history backup and
saves once; it is disabled when both are empty. Existing credited-history configuration is
retained until explicitly cleared. Clearing preserves bounded, session-local active tag/receipt
evidence so a hunt awaiting its kill or reward can still earn credit without restoring cleared
reports or duplicating an already confirmed reward. No hunt-state, queue, travel, or combat
decision consumes history or its clear operation.

Credit requires a handled tag in the final pull, positive kill evidence through the unchanged
live-entity veto, and an original game system hunt-currency acquisition message in the matching
world, territory, and instance within 20 seconds. Reward-before-kill ordering is supported;
ambiguous, expired, reset-without-retag, and restored unconfirmed sessions do not earn credit.
Names come from current client data with English fallbacks; unrecognized reward messages or
capped rewards remain credit-unconfirmed rather than inventing success. These observers do not
control hunting, and failures are caught without interrupting the original handler.

## Validation and distribution

The 29 state-machine tests exercise existing hunt policies. The 32 UI/migration/companion/history test groups
use real native ImGui and configuration serialization in isolated contexts, without connecting
to FFXIV or submitting hunt actions. They cover canonical switch input and disabled state,
the Theme-only bottom-left Classic button with all three input methods, window identity and persistence,
custom minimize/close, header dragging, native resizing, reduced motion, and scope balancing.
The actual consumer is rendered at 620 x 520, 800 x 640, and 1040 x 860 logical pixels at
100%, 150%, and 200% UI scale. Native child geometry checks verify the left rail, header without
scrollbars, and control bounds inside responsive rows. Checks also cover compact plugin
actions, empty-history preservation, populated list/chart bounds, expansion-filter recording
and non-destructive display, SS and active-credit filtering, local daily tagged/untagged
aggregation, unified clearing, persistence, disabled input, and active-credit preservation.
The runner reports managed exceptions
and exits with failure instead of an unhandled .NET crash popup. In-game visual acceptance
remains a separate user observation.

Publish Beta updates this repository's authoritative `repo.json`, verifies the public ZIP,
then optionally sends `plugin-released` using `DALAMUD_CATALOG_TOKEN`; public verification is mandatory even without that token (see [release infrastructure](RELEASE_INFRASTRUCTURE.md)).
The generator owns `MarshalTitan/Sentinel/repo.json`; this repository does not write it directly.
The workflow waits for the generated version and validates the central public install path.
