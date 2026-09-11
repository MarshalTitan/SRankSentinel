# Sentinel Hunts

Sentinel Hunts is a clean, independent Dalamud S-rank orchestrator in the Sentinel ecosystem. It is deliberately separate from S Rank Sentinel:

- Plugin name: `Sentinel Hunts`
- Internal name and assembly: `SentinelHunts`
- Command: `/sentinelhunts`
- Configuration and persistent queue: separate Dalamud configuration
- Source folder and build output: separate from the existing root `SRankSentinel` project

Installing or testing this project does not replace `SRankSentinel`.

## Current test build: 0.1.1.0

The 0.1.1 field update tightens the report approach from the original 55-yalm stop,
continues a bounded close-range scan instead of hovering in place, resolves marks by
data ID or exact name, and requires a projected ground parking point followed by a
confirmed landing and dismount before the 95% Tomahawk gate can open.

Implemented:

- Complete 47-rank ARR-through-Dawntrail normal S-rank catalog.
- Separate expansion toggles; Shadowbringers, Endwalker, and Dawntrail are enabled by default.
- Structured Sonar map-link ingestion.
- Current and legacy HuntAlerts IPC field adaptation.
- Chronological deduplication and kill invalidation.
- Persistent FIFO queue; new reports do not pre-empt the active hunt.
- Lifestream boundary for Ul'dah teleport, same-DC World Visit, territory teleport, and instance change.
- vnavmesh boundary for approach, mesh projection, exclusion-radius pathfinding, parking, and retreat.
- 25-yalm default clearance measured from the mark's hitbox.
- WAR/MRD-only, one-attempt Tomahawk gate at or below 95% HP.
- Non-mark incidental-aggro exclusion and bounded single-target WAR rotation.
- Raise acceptance and tagged-death Return lock until positive mark death confirmation.
- Observation mode enabled by default: alert intake works while all player control remains off.
- Framework-free core test runner.

Intentionally held for a later test build:

- Direct Faloop authentication/feed. The old client uses undocumented private endpoints and is not being copied into the new core.
- SS-rank chains.
- The Idyllshire-to-Dravanian-Hinterlands legacy gateway exception.
- Direct import of MMOMinion/HusbandoMax meshes. Those assets use MMOMinion binary formats; runtime movement stays with vnavmesh.

## Dependencies for live testing

- Dalamud API 15 development/runtime files.
- vnavmesh installed and ready in the current territory.
- Lifestream installed for travel and instance changes.
- Sonar and/or HuntAlerts for live reports.
- A Marauder/Warrior character with flight and required aetherytes unlocked.

## Build and test

From this folder:

```powershell
dotnet run --project .\SentinelHunts.Core.Tests\SentinelHunts.Core.Tests.csproj -c Release
dotnet build .\SentinelHunts.csproj -c Debug
```

The Dalamud SDK places the local plugin build under `bin\Debug\SentinelHunts`. Add the produced `SentinelHunts.dll` as a Dalamud development plugin, then use `/sentinelhunts`.

Recommended first test sequence:

1. Load Sentinel Hunts alongside S Rank Sentinel and verify both appear separately.
2. Leave live automation off.
3. Click **Add observation-mode Tyger test report** and verify queue persistence after a reload.
4. Enable only one real alert source and verify report parsing/deduplication.
5. Test Ul'dah/world/territory travel with attacks still prevented by using a non-WAR job.
6. Test vnavmesh approach and 25-yalm parking on a controlled live report.
7. Change to WAR only for an explicit one-Tomahawk test.

`STOP NOW` immediately disables live automation and stops Sentinel Hunts-owned vnavmesh movement. S Rank Sentinel remains independent and must be disabled separately during movement tests so two plugins never compete for control.
