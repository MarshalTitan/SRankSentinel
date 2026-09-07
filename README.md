# S Rank Sentinel

S Rank Sentinel is a standalone Dalamud S-rank orchestrator. Its experimental primary source is Faloop's authenticated real-time feed; HuntAlerts and Sonar remain optional fallbacks. Every source supplies data only, while Sentinel owns the complete hunt lifecycle.

## Workflow

1. Connect directly to Faloop's authenticated Socket.IO feed and consume spawn/death events. The username and resulting session ID are retained for automatic reconnect. Passwords are never logged or serialized as plaintext; the optional **Remember Faloop login on this PC** setting stores only Windows CurrentUser DPAPI ciphertext.
2. Apply Sentinel's configurable expansion gate before queueing, reset, World Visit, or territory teleport. Centurio (ARR/HW/SB), Shadowbringers, Endwalker, and Dawntrail can be enabled independently; disabled-expansion alerts are ignored locally regardless of the source's website/UI filters. Evercold is reserved as a disabled future placeholder until hunt data exists.
3. Translate Faloop zone/POI IDs through a reviewed coordinate snapshot from the current web client, convert the stored map coordinates to a local game-world destination, and retain it across World Visit, teleport, zoning, and instance transitions. This feeds vnavmesh directly without creating or reading the game's global map flag and is never a direct player-position write.
4. Optionally accept HuntAlerts IPC and Sonar chat/map-link alerts as fallback sources. Either fallback can be disabled independently after the direct feed is proven on the player's data center.
5. Reject cross-data-center alerts, suppress duplicate feed events, deduplicate same-data-center alerts by world + territory + instance + mark, and persist the ordered queue without interrupting the active hunt. Matching Faloop death events immediately invalidate queued entries; historical deaths older than the freshness window cannot invalidate a newer spawn.
6. Reset through Ul'dah before every hunt.
7. Use the Ul'dah aetheryte's normal World Visit menus when the alert is on another world.
8. Choose the hunt territory's preferred attuned aetheryte deterministically, use the game's normal teleport system, and select the requested zone instance. If already far from that aetheryte, teleport back to it before opening the instance menu. The Dravanian Hinterlands uses its normal Idyllshire → Prologue Gate (Western Hinterlands) aethernet route because that field zone has no main teleport crystal.
9. After arriving in the correct territory and instance, first wait for zoning and the local player state to settle, then wait indefinitely and motionless at the aetheryte until vnavmesh reports the mesh fully ready. Mesh generation/download is a blocking state, never a hunt failure; newer alerts remain queued and cannot replace the active hunt.
10. Use the stored Faloop/HuntAlerts/Sonar coordinates directly as the initial vnavmesh flight destination. Direct Faloop operation does not depend on HuntAlerts, Sonar, or either fallback creating a map flag. Do not require or scan for the S-rank entity at the aetheryte; begin resolving the actual battle object only after reaching the reported area.
11. If the mark is not visible near the alert coordinates, remain there and rescan in repeated bounded windows. A missing entity, unavailable coordinate projection, interrupted path, or failed route never means the hunt is dead and never clears it.
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

- Initial coordinate stop: **60y**
- Safe parking clearance: **45y** plus player/mark hitboxes
- Emergency clearance: **38y** plus player/mark hitboxes
- Engagement gate: mark reports **in combat** and is **<=95% HP**
- Expansion checkboxes and distance-profile assignment are independent. Centurio and Shadowbringers currently share the **Close-safe** behavior profile; Endwalker and Dawntrail share the **Proximity-sensitive** profile. Evercold remains disabled but is reserved for the proximity-sensitive profile when support arrives.
- Each behavior profile stores its own initial stop, safe clearance, emergency clearance, and HP gate. Existing settings migrate into the corresponding shared profiles without lowering valid saved clearances.
- Close-safe ranges are **25–90y** initial stop, **5–70y** safe clearance, and **5–50y** emergency clearance. Proximity-sensitive ranges remain **35–90y**, **20–70y**, and **15–50y** respectively. Yard values use one-yard increments and HP gates are displayed as percentages.
- Emergency clearance is constrained to the selected profile's safe parking clearance, so it cannot be configured to exceed the normal waiting radius.
- Ranged tag action: selected automatically from the current combat job and adjusted for learned upgrades
- Exactly one client action attempt per mark, whether the client accepts or rejects it; no retry loop and no combat rotation
- Pugilist/Monk, non-combat jobs, and other unsupported jobs wait without attacking; a manual action-ID override remains available
- ShB/EW/DT SS check: **2 seconds** after each supported normal S-rank death, then **5 minutes** once that expansion's precursor chain is observed
- Forgiven Gossip, Ker Shroud, and Crystal Incarnation: observation-only; never navigation or combat targets
- Pending queue: saved in plugin configuration, kept in arrival order, deduplicated, kill-invalidated, and stale after **45 minutes** by default
- Faloop transport: native Engine.IO v4 WebSocket with server-ping replies, heartbeat timeout, bounded messages, exponential reconnect backoff, event replay suppression, and no extra NuGet runtime
- Faloop authentication: reconnect with the saved session first; if Faloop rejects it and remembered login is enabled, decrypt the local DPAPI credential and make one automatic re-authentication attempt. A failed automatic login stops and asks for credentials instead of looping.
- **Forget saved session/login** clears the session, username, DPAPI ciphertext, and remembered-login setting together.
- Dalamud repository updates preserve the plugin configuration file, including the session and user-bound ciphertext. The ciphertext intentionally cannot be decrypted after copying it to another Windows user or PC.
- No coordinate writes or coordinate warping
- Missing marks, unavailable coordinate projections, and unreachable local routes keep the active hunt reserved and are retried; they never produce a cleared/dead result

## Dependencies

- **vnavmesh** is required for navigation.
- A linked Faloop account is required for the direct authenticated feed. Faloop's own account/region visibility still determines which events the server sends; Sentinel does not bypass server-side access limits.
- **HuntAlerts and Sonar are optional fallbacks** and may both be disabled.
- HuntTrainAssistant, Lifestream, Wrath Combo, and BossMod are **not required or used**.

World Visit can only reach worlds on the character's current data center. Cross-data-center alerts are ignored and cross-data-center travel is intentionally not automated. After a hunt, Sentinel stays on the visited World in Ul'dah until another eligible alert requires a World change.

## Command

`/sranksentinel` opens the status/settings window.

The emergency control is **STOP + RESET THROUGH UL'DAH**. It clears the queue, stops vnavmesh, discards the active alert, and performs the same normal Ul'dah reset used by completed hunts.

## Beta installation

The permanent multi-plugin catalog URL is:

`https://raw.githubusercontent.com/MarshalTitan/DalamudPlugins/main/repo.json`

Add it under **Dalamud Settings → Experimental → Custom Plugin Repositories**, save the setting, open `/xlplugins`, search for **S Rank Sentinel**, and choose **Install**.

The former SRankSentinel-specific URL remains supported during migration so existing installations are not disrupted:

`https://raw.githubusercontent.com/MarshalTitan/SRankSentinel/main/repo.json`

If both URLs are enabled temporarily, Dalamud may show a duplicate listing. Confirm the new central listing works, then remove only the old custom-repository URL without uninstalling the plugin. The unchanged `InternalName` preserves the existing installation and configuration.

Published packages remain permanent GitHub Release assets named `SRankSentinel.zip` in this repository. GitHub Actions build artifacts are for development checks only and neither catalog points to them.

## Publishing the next beta

1. Change `<Version>` in `SRankSentinel.csproj` to a new four-part version and commit the development changes to `main`.
2. Wait for the normal **Build** workflow to pass.
3. Run the **Publish Beta** workflow from the Actions tab.
4. The workflow rebuilds and validates the package, publishes or repairs the `v<version>` prerelease asset, and updates this repository's legacy `repo.json` with the new version and permanent download URLs.
5. If the `DALAMUD_CATALOG_TOKEN` repository secret is configured, the workflow also updates only the `SRankSentinel` object in `MarshalTitan/DalamudPlugins/repo.json` and validates a fresh public install. Otherwise it emits a notice and the central repository's **Update Plugin Entry** workflow is the manual fallback.

Dalamud compares `AssemblyVersion` in `repo.json` with the installed assembly. The tester receives the newer beta through the normal **Update** button while development can continue on `main` between published versions.

For automatic central-catalog updates, use a fine-grained GitHub token limited to `MarshalTitan/DalamudPlugins` with **Contents: Read and write** permission. Save it only as the `DALAMUD_CATALOG_TOKEN` Actions repository secret in `MarshalTitan/SRankSentinel`; never commit or log it.
