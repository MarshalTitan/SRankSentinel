# S Rank Sentinel

S Rank Sentinel is a standalone Dalamud S-rank orchestrator. HuntAlerts and Sonar are optional alert sources only; Sentinel owns the complete hunt lifecycle.

## Workflow

1. Accept an S-rank alert from HuntAlerts IPC or a Sonar chat/map-link alert.
2. Deduplicate it and queue it without interrupting the active hunt.
3. Reset through Ul'dah before every hunt.
4. Use the Ul'dah aetheryte's normal World Visit menus when the alert is on another world.
5. Use the game's normal teleport system to reach the hunt territory and select the requested zone instance.
6. Use vnavmesh flight to approach the flag, positively identify the named S rank, sample multiple reachable parking points, and land 45 yalms clear of both hitboxes.
7. Maintain a 35-yalm emergency floor as the mark moves.
8. Engage only after the mark itself is in combat and at or below 95% HP. Move into range, execute the configured ranged tag action exactly once, then retreat.
9. Detect the mark's death from the live battle object or a matching Sonar kill notice.
10. If dead while the mark is alive, accept a Raise prompt and never use Return. If still dead after the kill is confirmed, use the normal Return action. Otherwise teleport normally to Ul'dah.
11. Clear the completed alert in Ul'dah and start the next queued S rank, if any.

## Safety defaults

- Initial flag stop: **50y**
- Safe parking clearance: **45y** plus player/mark hitboxes
- Emergency clearance: **35y** plus player/mark hitboxes
- Engagement gate: mark reports **in combat** and is **<=95% HP**
- Ranged tag action: **46 (Tomahawk)** by default; change this in settings if using another job
- One successful tag action per mark; no combat rotation
- No coordinate writes or coordinate warping
- Unknown marks, missing flags, and unreachable routes are discarded only after a normal reset through Ul'dah

## Dependencies

- **vnavmesh** is required for navigation.
- **HuntAlerts and/or Sonar** may supply alerts.
- HuntTrainAssistant, Lifestream, Wrath Combo, and BossMod are **not required or used**.

World Visit can only reach worlds exposed by the game's normal World Visit menu. Cross-data-center travel is intentionally not automated.

## Command

`/sranksentinel` opens the status/settings window.

The emergency control is **STOP + RESET THROUGH UL'DAH**. It clears the queue, stops vnavmesh, discards the active alert, and performs the same normal Ul'dah reset used by completed hunts.
