# S Rank Sentinel

S Rank Sentinel is a standalone Dalamud S-rank orchestrator. HuntAlerts and Sonar are optional alert sources only; Sentinel owns the complete hunt lifecycle.

## Workflow

1. Accept an S-rank alert from HuntAlerts IPC or a Sonar chat/map-link alert.
2. Reject cross-data-center alerts, deduplicate same-data-center alerts by world + territory + instance + mark, and persist the ordered queue without interrupting the active hunt.
3. Reset through Ul'dah before every hunt.
4. Use the Ul'dah aetheryte's normal World Visit menus when the alert is on another world.
5. Choose the hunt territory's preferred attuned aetheryte deterministically, use the game's normal teleport system, and select the requested zone instance. If already far from that aetheryte, teleport back to it before opening the instance menu.
6. Use vnavmesh flight to approach the flag, positively identify the S rank by stable battle-NPC ID (with a localized-name fallback), sample multiple reachable parking points, and land 45 yalms clear of both hitboxes.
7. Maintain a 38-yalm emergency floor as the mark moves.
8. Engage only after the active S/SS mark itself is in combat and at or below 95% HP. Target it, choose an appropriate native ranged action for the current job, move into range, make exactly one client action attempt, permanently close the attack gate for that mark, then retreat.
9. Detect the mark's death from the live battle object or a matching same-world Sonar kill notice.
10. After a normal Shadowbringers, Endwalker, or Dawntrail S rank dies, remain in its territory for a shared two-second SS-evidence check. A matching zone message, HuntAlerts/Sonar report, or visible precursor enters the five-minute SS watch.
11. Keep each expansion's chain separate: Forgiven Gossip leads to Forgiven Rebellion, Ker Shroud leads to Ker, and Crystal Incarnation leads to Arch Aethereater. Precursors are observed only and never replace the active target, trigger navigation, or receive attacks.
12. If the matching SS is announced, reported, queued, or visible, replace the completed S-rank context with that SS, navigate directly to it without an Ul'dah reset, then use the same safe parking and one-tag gates.
13. If dead while an active mark or SS opportunity is alive, accept a Raise prompt and never use Return. If still dead after the completed opportunity, use the normal Return action.
14. Once the hunt and any SS opportunity finish, teleport normally to Ul'dah on the **current visited World**. Remove kill-reported or stale queue entries, recheck freshness immediately before departure, and start the next valid queued S rank in arrival order. Sentinel never World Visits back to the character's Home World automatically.

## Safety defaults

- Initial flag stop: **60y**
- Safe parking clearance: **45y** plus player/mark hitboxes
- Emergency clearance: **38y** plus player/mark hitboxes
- Engagement gate: mark reports **in combat** and is **<=95% HP**
- Ranged tag action: selected automatically from the current combat job and adjusted for learned upgrades
- Exactly one client action attempt per mark, whether the client accepts or rejects it; no retry loop and no combat rotation
- Pugilist/Monk, non-combat jobs, and other unsupported jobs wait without attacking; a manual action-ID override remains available
- ShB/EW/DT SS check: **2 seconds** after each supported normal S-rank death, then **5 minutes** once that expansion's precursor chain is observed
- Forgiven Gossip, Ker Shroud, and Crystal Incarnation: observation-only; never navigation or combat targets
- Pending queue: saved in plugin configuration, kept in arrival order, deduplicated, kill-invalidated, and stale after **45 minutes** by default
- No coordinate writes or coordinate warping
- Unknown marks, missing flags, and unreachable routes are discarded only after a normal reset through Ul'dah

## Dependencies

- **vnavmesh** is required for navigation.
- **HuntAlerts and/or Sonar** may supply alerts.
- HuntTrainAssistant, Lifestream, Wrath Combo, and BossMod are **not required or used**.

World Visit can only reach worlds on the character's current data center. Cross-data-center alerts are ignored and cross-data-center travel is intentionally not automated. After a hunt, Sentinel stays on the visited World in Ul'dah until another eligible alert requires a World change.

## Command

`/sranksentinel` opens the status/settings window.

The emergency control is **STOP + RESET THROUGH UL'DAH**. It clears the queue, stops vnavmesh, discards the active alert, and performs the same normal Ul'dah reset used by completed hunts.
