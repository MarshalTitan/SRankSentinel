# S Rank Sentinel

S Rank Sentinel is a standalone Dalamud S-rank orchestrator. Its experimental primary source is Faloop's authenticated real-time feed; HuntAlerts and Sonar remain optional fallbacks. Every source supplies data only, while Sentinel owns the complete hunt lifecycle.

## Workflow

1. Connect directly to Faloop's authenticated Socket.IO feed and consume spawn/death events. Faloop credentials are entered in Sentinel's window; the password is never saved or logged. Only the resulting session ID is saved for automatic reconnect.
2. Apply Sentinel's own expansion gate before any reset, World Visit, or territory teleport. Only Shadowbringers, Endwalker, and Dawntrail S/SS alerts are eligible, even when HuntAlerts or Sonar also announces ARR, Heavensward, or Stormblood marks.
3. Translate Faloop zone/POI IDs through a reviewed coordinate snapshot from the current web client; the result is an approach flag, not a direct position write.
4. Optionally accept HuntAlerts IPC and Sonar chat/map-link alerts as fallback sources. Either fallback can be disabled independently after the direct feed is proven on the player's data center.
5. Reject cross-data-center alerts, suppress duplicate feed events, deduplicate same-data-center alerts by world + territory + instance + mark, and persist the ordered queue without interrupting the active hunt. Matching Faloop death events immediately invalidate queued entries; historical deaths older than the freshness window cannot invalidate a newer spawn.
6. Reset through Ul'dah before every hunt.
7. Use the Ul'dah aetheryte's normal World Visit menus when the alert is on another world.
8. Choose the hunt territory's preferred attuned aetheryte deterministically, use the game's normal teleport system, and select the requested zone instance. If already far from that aetheryte, teleport back to it before opening the instance menu.
9. After arriving in the correct territory and instance, first wait for zoning and the local player state to settle, then wait indefinitely and motionless at the aetheryte until vnavmesh reports the mesh fully ready. Mesh generation/download is a blocking state, never a hunt failure; newer alerts remain queued and cannot replace the active hunt.
10. Use the stored Faloop/HuntAlerts/Sonar coordinates as the initial vnavmesh flight destination. Do not require or scan for the S-rank entity at the aetheryte; begin resolving the actual battle object only after reaching the reported area.
11. If the mark is not visible near the alert coordinates, remain there and rescan in repeated bounded windows. A missing entity, unavailable flag projection, interrupted path, or failed route never means the hunt is dead and never clears it.
12. Once positively identified by stable battle-NPC ID (with a localized-name fallback), switch from the static alert coordinates to dynamic entity-based parking, sample multiple reachable parking points, and land 45 yalms clear of both hitboxes.
13. Maintain a 38-yalm emergency floor as the mark moves.
14. Engage only after the active S/SS mark itself is in combat and at or below 95% HP. Target it, choose an appropriate native ranged action for the current job, move into range, make exactly one client action attempt, permanently close the attack gate for that mark, then retreat.
15. Mark a hunt cleared only from positive evidence: a matching Faloop/HuntAlerts/Sonar death event, a matching game hunt/reward kill message, or a previously identified live battle object becoming visibly dead. Object absence alone is never death evidence.
16. After a normal Shadowbringers, Endwalker, or Dawntrail S rank dies, remain in its territory for a shared two-second SS-evidence check. A matching zone message, Faloop/HuntAlerts/Sonar report, or visible precursor enters the five-minute SS watch.
17. Keep each expansion's chain separate: Forgiven Gossip leads to Forgiven Rebellion, Ker Shroud leads to Ker, and Crystal Incarnation leads to Arch Aethereater. Precursors are observed only and never replace the active target, trigger navigation, or receive attacks.
18. If the matching SS is announced, reported, queued, or visible, replace the completed S-rank context with that SS, navigate directly to it without an Ul'dah reset, then use the same safe parking and one-tag gates.
19. If dead while an active mark or SS opportunity is alive, accept a Raise prompt and never use Return. If still dead after the completed opportunity, use the normal Return action.
20. Once the hunt and any SS opportunity finish, teleport normally to Ul'dah on the **current visited World**. Remove kill-reported or stale queue entries, recheck freshness immediately before departure, and start the next valid queued S rank in arrival order. Sentinel never World Visits back to the character's Home World automatically.

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
- Faloop transport: native Engine.IO v4 WebSocket with server-ping replies, heartbeat timeout, bounded messages, exponential reconnect backoff, event replay suppression, and no extra NuGet runtime
- No coordinate writes or coordinate warping
- Missing marks, unavailable flags, and unreachable local routes keep the active hunt reserved and are retried; they never produce a cleared/dead result

## Dependencies

- **vnavmesh** is required for navigation.
- A linked Faloop account is required for the direct authenticated feed. Faloop's own account/region visibility still determines which events the server sends; Sentinel does not bypass server-side access limits.
- **HuntAlerts and Sonar are optional fallbacks** and may both be disabled.
- HuntTrainAssistant, Lifestream, Wrath Combo, and BossMod are **not required or used**.

World Visit can only reach worlds on the character's current data center. Cross-data-center alerts are ignored and cross-data-center travel is intentionally not automated. After a hunt, Sentinel stays on the visited World in Ul'dah until another eligible alert requires a World change.

## Command

`/sranksentinel` opens the status/settings window.

The emergency control is **STOP + RESET THROUGH UL'DAH**. It clears the queue, stops vnavmesh, discards the active alert, and performs the same normal Ul'dah reset used by completed hunts.
