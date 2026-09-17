# S Rank Sentinel

S Rank Sentinel is a standalone Dalamud S-rank orchestrator. HuntAlerts and Sonar supply S-rank reports and map coordinates, while Sentinel owns the complete hunt lifecycle.

## Workflow

1. Listen for HuntAlerts IPC reports and Sonar chat/map-link reports. Both production sources remain enabled so the first valid current-DC S-rank report can activate or enrich the same deduplicated hunt.
2. Apply Sentinel's configurable expansion gate before queueing, reset, World Visit, or territory teleport. Centurio (ARR/HW/SB), Shadowbringers, Endwalker, and Dawntrail can be enabled independently; disabled-expansion alerts are ignored locally regardless of the source's website/UI filters. Evercold is reserved as a disabled future placeholder until hunt data exists.
3. Convert the supplied map coordinates to a local game-world destination and retain it across World Visit, teleport, zoning, and instance transitions. This feeds vnavmesh directly and is never a direct player-position write.
4. Reconcile HuntAlerts and Sonar reports into one alert identity rather than creating duplicates when both providers report the same mark.
5. Reject cross-data-center or coordinate-less alerts, deduplicate same-data-center alerts by world + territory + instance + mark, and persist the ordered queue without interrupting the active hunt. Matching positive HuntAlerts or Sonar `was killed at`/`was just killed` reports—including Sonar's `@Territory <X,Y> @World` format—invalidate the active/queued entry on the reported World; historical deaths older than the freshness window cannot invalidate a newer spawn.
6. Reset through Ul'dah before every hunt.
7. Use the Ul'dah aetheryte's normal World Visit menus when the alert is on another world.
8. Once the hunt coordinates are resolved, build one ordered list of every attuned territory aetheryte. Rank candidates with the same local-coordinate transform used for the hunt—using linked Level data when reliable and the territory's aetheryte map markers as the general fallback—then teleport to the lowest estimated onward-travel cost. Log every estimate and the selection. If Teleport, projection, or the onward route repeatedly fails, advance to the next-ranked candidate rather than waiting forever. The Tempest keeps its centralized safety override to The Ondo Cups and never uses Macarenses Angle. The Dravanian Hinterlands keeps its required Idyllshire → Prologue Gate (Western Hinterlands) route because that field zone has no main teleport crystal.
9. After arriving in the correct territory and instance, first wait for zoning and the local player state to settle, then wait indefinitely and motionless at the aetheryte until vnavmesh reports the mesh fully ready. Mesh generation/download is a blocking state, never a hunt failure; newer alerts remain queued and cannot replace the active hunt.
10. Use the stored HuntAlerts/Sonar coordinates as the initial vnavmesh destination. Normal field hunts use flight, with a full-height nearest-mesh query and a bounded approximate X/Z flight when a 2D alert cannot immediately resolve elevation. Chernobog is not blacklisted: its Outer La Noscea approach lands first and uses elevation-aware ground-only vnavmesh routing through U'Ghamaro Mines so a straight aerial line cannot drive into cave walls. Switch immediately to the actual battle object whenever it becomes visible.
11. If the mark is not visible near the alert coordinates, remain there and rescan in repeated bounded windows. Missing still never means dead. Projection, pathfinding, and movement each have progress/attempt bounds: stalled movement is stopped and replanned, outstanding pathfinding is cancelled after its timeout, another projected point or ranked aetheryte is tried, and an explicitly exhausted route returns through Ul'dah without falsely marking the hunt dead.
12. Once positively identified by stable battle-NPC ID (with a localized-name fallback), switch from the static alert coordinates to dynamic entity-based parking. Prefer a valid point beside the largest nearby player cluster when it remains inside the 35y waiting envelope, but require every crowd and geometric fallback route to stay outside the mark's protected radius. Before committing to a flying parking route, also require a reasonable ground path from the candidate toward the hunt side so gaps, cliffs, and disproportionate walking detours are rejected. Revalidate the live mark, crowd, clearances, and final approach before landing; if the mark moves or resets beyond 35y, safely re-park inside that envelope.
13. Maintain the configured emergency clearance as the mark moves.
14. Engage only after the active S/SS mark itself is in combat and at or below 95% HP. Dismount, target it, choose an appropriate native ranged action for the current job, and use a close ground approach so vnavmesh routes around intervening rocks or trees. A rejected or unconfirmed dispatch keeps the tag state active and may retry while the same mark remains alive; the attack gate closes only after the intended targeted action reaches the game's handled-action sequence. Then retreat and issue no more offensive actions for that pull cycle. Re-arm only when the same positively identified living mark remains out of combat at 99–100% HP for a stable confirmation interval, proving a genuine reset; disappearance or combat-data flicker never re-arms it.
15. Mark a hunt cleared only from positive evidence: a context-matched HuntAlerts/Sonar death event, a matching game hunt/reward kill message, or a previously identified live battle object becoming visibly dead. Every completion source passes through one final live-entity conflict check, so a visible current mark with HP remaining cannot enter a post-kill state. Object absence alone is never death evidence.
16. After a normal Shadowbringers, Endwalker, or Dawntrail S rank that was locally identified and observed in combat dies, remain in its territory for a shared four-second SS-evidence check. A matching zone message received immediately before or after kill confirmation, a HuntAlerts/Sonar report, or a visible precursor enters the five-minute SS watch. Prey coordinates are never destinations: every reservation and early SS report is normalized to the territory's centralized documented SS spawn point, where Sentinel selects connected ground in a tight approximately 25y staging envelope until the actual SS entity is detectable.
17. Keep each expansion's chain separate: Forgiven Gossip leads to Forgiven Rebellion, Ker Shroud leads to Ker, and Crystal Incarnation leads to Arch Aethereater. Precursors are observed only and never replace the active target, trigger navigation, or receive attacks.
18. If the matching SS is announced, reported, or queued while the player is alive, retain the completed S-rank context and stage at the fixed spawn point; replace that context with the SS only when the actual SS object is detectable. If the player is dead after the normal S rank's positive completion, reserve that SS ahead of every ordinary queued S rank, take the normal Return to Ul'dah, and then travel back to the SS territory through the standard hunt flow.
19. If dead while the current S/SS target is still alive, accept a Raise prompt and never use Return. After the successfully tagged current target receives positive death confirmation, remain dead in the territory for a two-second reward/credit grace while still accepting a valid Raise; only then may Return begin. An already-open prompt may be adopted only after that grace and only when it explicitly names Return to Ul'dah/Steps of Nald. Living post-kill travel is not delayed. An SS reserved after a completed normal S is a separate next target and does not keep the completed S's Return lock active.
20. Once the hunt and any SS opportunity finish, attempt a normal Teleport directly to Ul'dah on the **current visited World**, including while mounted/flying when the game permits it. Landing is used only after concrete Teleport rejection, not as a prerequisite. Remove kill-reported or stale queue entries, physically confirm Ul'dah arrival, and then start the next valid queued target. Actual or reserved SS opportunities sort ahead of ordinary S ranks; ordinary S ranks retain expansion priority and oldest-first order. Sentinel never World Visits back to the character's Home World automatically.

## Safety defaults

- Initial coordinate stop: **35y**
- Safe parking clearance: **35y** plus player/mark hitboxes
- Emergency clearance: **35y** plus player/mark hitboxes
- Engagement gate: mark reports **in combat** and is **<=95% HP**
- Expansion checkboxes and distance-profile assignment are independent. Centurio and Shadowbringers currently share the **Close-safe** behavior profile; Endwalker and Dawntrail share the **Proximity-sensitive** profile. Evercold remains disabled but is reserved for the proximity-sensitive profile when support arrives.
- Each behavior profile stores its own initial stop, safe clearance, emergency clearance, and HP gate. Existing settings migrate into the corresponding shared profiles without lowering valid saved clearances.
- Close-safe ranges are **5–35y** for initial stop, safe clearance, and emergency clearance. Proximity-sensitive ranges are **20–35y** initial stop, **20–35y** safe clearance, and **15–35y** emergency clearance. Yard values use one-yard increments and HP gates are displayed as percentages.
- Emergency clearance is constrained to the selected profile's safe parking clearance, so it cannot be configured to exceed the normal waiting radius.
- Ranged tag action: always selected automatically from the current combat job and adjusted for learned upgrades; no user-facing selector is needed
- Exactly one confirmed ranged tag per genuine combat/pull cycle. Client-rejected or unconfirmed submissions may retry while the exact mark is still alive; once a dispatch is server-handled the attack gate closes. A stable full-health reset may open a new cycle, but there is no rotation or further offensive action after confirmation
- Pugilist/Monk, non-combat jobs, and other unsupported jobs wait without attacking
- ShB/EW/DT SS check: **4 seconds** after each supported normal S-rank death, then **5 minutes** once that expansion's precursor chain is observed
- Forgiven Gossip, Ker Shroud, and Crystal Incarnation: observation-only; never navigation or combat targets
- Pending queue: saved in plugin configuration, deduplicated, kill-invalidated, and ordered SS first, then expansion priority, then oldest first. Ordinary alerts are stale after **45 minutes** by default; an unconfirmed prey-based SS reservation expires with the **5-minute** chain watch.
- No coordinate writes or coordinate warping
- Missing marks keep the active hunt reserved and never produce a cleared/dead result. Projection and route failures use bounded recovery, alternate projected points/aetherytes, and finally an explicit non-kill abandonment through Ul'dah rather than an unlimited stationary or collision loop

## Dependencies

- **vnavmesh** is required for navigation.
- **HuntAlerts and Sonar** are the production alert providers and should both be installed and enabled.
- HuntTrainAssistant, Lifestream, Wrath Combo, and BossMod are **not required or used**.

World Visit can only reach worlds on the character's current data center. Cross-data-center alerts are ignored and cross-data-center travel is intentionally not automated. After a hunt, Sentinel stays on the visited World in Ul'dah until another eligible alert requires a World change.

## Command

`/sranksentinel` opens the status/settings window.

**SKIP CURRENT + KEEP QUEUE** discards only the current alert, returns through Ul'dah, and continues with the highest-priority valid queued hunt. The emergency **STOP + RESET THROUGH UL'DAH** control clears the entire queue, stops vnavmesh, discards the active alert, and performs the same normal Ul'dah reset used by completed hunts.

## Beta installation

The permanent multi-plugin catalog URL is:

`https://raw.githubusercontent.com/MarshalTitan/Sentinel/main/repo.json`

Add it under **Dalamud Settings → Experimental → Custom Plugin Repositories**, save the setting, open `/xlplugins`, search for **S Rank Sentinel**, and choose **Install**.

The former SRankSentinel-specific URL remains supported during migration so existing installations are not disrupted:

`https://raw.githubusercontent.com/MarshalTitan/SRankSentinel/main/repo.json`

If both URLs are enabled temporarily, Dalamud may show a duplicate listing. Confirm the new central listing works, then remove only the old custom-repository URL without uninstalling the plugin. The unchanged `InternalName` preserves the existing installation and configuration.

Published packages remain permanent GitHub Release assets named `SRankSentinel.zip` in this repository. GitHub Actions build artifacts are for development checks only and neither catalog points to them.

## Publishing the next beta

1. Change `<Version>` in `SRankSentinel.csproj` to a new four-part version and commit the development changes to `main`.
2. Wait for the normal **Build** workflow to pass.
3. The version bump automatically starts the **Publish Beta** workflow.
4. The workflow rebuilds and validates the package, publishes or repairs the `v<version>` prerelease asset, and updates this repository's legacy `repo.json` with the new version and permanent download URLs.
5. If the `DALAMUD_CATALOG_TOKEN` repository secret is configured, the workflow also updates only the `SRankSentinel` object in `MarshalTitan/Sentinel/repo.json` and validates a fresh public install. Otherwise it emits a notice and the central repository's **Update Plugin Entry** workflow is the manual fallback.

Dalamud compares `AssemblyVersion` in `repo.json` with the installed assembly. The tester receives the newer beta through the normal **Update** button while development can continue on `main` between published versions.

For automatic central-catalog updates, use a fine-grained GitHub token limited to `MarshalTitan/Sentinel` with **Contents: Read and write** permission. Save it only as the `DALAMUD_CATALOG_TOKEN` Actions repository secret in `MarshalTitan/SRankSentinel`; never commit or log it.
