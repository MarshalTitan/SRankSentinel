# MMOMinion navigation archive audit

The supplied archives were inspected without modifying or repackaging them.

## Navigation.zip

- Path: `C:\Plugins\MinionNavigation\Navigation\Navigation.zip`
- Compressed size: approximately 374 MB
- Entries: 6,941
- Formats: 2,490 `.ncs`, 2,097 `.mesh`, 1,862 `.cube`, 246 `.nav`
- Broad coverage from ARR through Dawntrail, plus duties and special areas.

## HMMeshes.zip

- Path: `C:\Plugins\MinionNavigation\HMMeshes\HMMeshes.zip`
- Compressed size: approximately 277 MB
- Entries: 7,006
- Formats: 2,880 `.ncs`, 2,775 `.cube`, 1,231 `.mesh`, 40 `.nav`, 40 `Version.lua`
- All 18 default-scope ShB/EW/DT S-rank territories are represented by named HusbandoMax folders.

## Compatibility decision

The `.nav` samples are binary MMOMinion metadata, not interchange manifests. The `.mesh`, `.cube`, and `.ncs` files form MMOMinion's own tiled navigation format. Neither archive contains a vnavmesh IPC adapter or a documented conversion contract.

Sentinel Hunts therefore does not load or redistribute these files. They remain potentially useful for human coverage comparison, route troubleshooting, or a separately licensed conversion experiment. Live Dalamud pathfinding and movement use vnavmesh.
