# S Rank Sentinel

S Rank Sentinel is a standalone Dalamud S-rank orchestrator. HuntAlerts and Sonar supply S-rank reports and map coordinates, while Sentinel owns the complete hunt lifecycle.

## Workflow

1. Listen for HuntAlerts IPC reports and Sonar chat/map-link reports. Both production sources remain enabled so the first valid same-region S-rank report can activate or enrich the same deduplicated hunt.
2. Apply Sentinel's configurable expansion gate before queueing, reset, World Visit, or territory teleport. Centurio (ARR/HW/SB), Shadowbringers, Endwalker, and Dawntrail can be enabled independently; disabled-expansion alerts are ignored locally regardless of the source's website/UI filters. Evercold is reserved as a disabled future placeholder until hunt data exists.
3. Convert the supplied map coordinates to a local game-world destination and retain it across World Visit, teleport, zoning, and instance transitions. This feeds vnavmesh directly and is never a direct player-position write.
4. Reconcile HuntAlerts and Sonar reports into one alert identity rather than creating duplicates when both providers report the same mark.
5. Reject cross-region or coordinate-less alerts, deduplicate eligible alerts by world + territory + instance + mark, and persist the ordered queue without interrupting the active hunt. Matching positive HuntAlerts or Sonar `was killed at`/`was just killed` reports—including Sonar's `@Territory <X,Y> @World` format—invalidate the active/queued entry on the reported World; historical deaths older than the freshness window cannot invalidate a newer spawn.
6. Reset through Ul'dah before every hunt.
7. From Ul'dah, hand World travel to Lifestream for an alert on another World. Wait for Lifestream to finish and confirm the exact World and an interactable character before territory teleport. A failed visit can retry once while idle, then yields to unresolved recovery and the remaining queue. If the exact active hunt is confirmed killed, Sentinel stops the owned Lifestream task, cancels the matching native World Visit queue when possible, and waits for travel to clear before taking the next hunt. If zoning has already begun, Sentinel waits for arrival and skips the killed hunt.
8. Resolve and teleport through the original `v0.7.34.0` territory-entry path. Prefer the nearest attuned aetheryte only when its linked game-data position resolves cleanly; otherwise use the catalog's configured preferred aetheryte and then the first usable territory aetheryte. The Tempest retains its hard override to The Ondo Cups. The Dravanian Hinterlands uses its normal Idyllshire → Prologue Gate (Western Hinterlands) aethernet route because that field zone has no main teleport crystal.
9. After arriving in the correct territory, wait for zoning and the local player state to settle. Use the live instance state to change only when the territory has public instances, and normalize stale provider instance numbers in single-instance zones. Then wait indefinitely and motionless at the aetheryte until vnavmesh reports the mesh fully ready. Mesh generation/download is a blocking state, never a hunt failure; newer alerts remain queued and cannot replace the active hunt.
10. Use the stored HuntAlerts/Sonar coordinates directly as the initial vnavmesh flight destination. In flyable field territories, request Company Chocobo first (with Mount Roulette as the non-blocking fallback), confirm the mounted state, request takeoff, and require positive `InFlight` confirmation before submitting the long-distance flying route. Each startup cycle has a 10-second budget; the first timeout stops movement and performs a confirmed land/dismount reset before one fresh Company Chocobo-first 10-second cycle. Only the second failed cycle abandons the hunt as unresolved, and a failed takeoff never degrades into an unintentional cross-map ground run. Do not require or scan for the S-rank entity at the aetheryte; begin resolving the actual battle object only after reaching the reported area.
11. If the mark is not visible near the alert coordinates, rescan under a cumulative search budget rather than restarting a 90-second window forever. At budget expiry, return once to the original reported area for a final positive scan. If the object is still absent and no matching kill evidence exists, abandon the alert explicitly as **unresolved, not dead**, return through Ul'dah, and continue the persistent queue. Local projection also uses elevation-aware probes, then a bounded approximate X/Z flight while continuing live entity detection; exhausted projection/path recovery reaches the same unresolved outcome instead of holding at the aetheryte forever.
12. Once positively identified by stable battle-NPC ID (with a localized-name fallback), switch from the static alert coordinates to dynamic entity-based parking. Treat the selected profile's safe distance as the preferred clearance: rank reachable candidates by error from that distance, use crowd size only to break close choices, and accept a materially farther point only after nearer candidates fail projection/path/ground-safety validation. Every crowd and geometric route must stay outside the mark's protected radius, and flying candidates must have a reasonable ground connection toward the hunt side. Small score jitter varies equally safe choices between clients without overriding clearance or safety. A flying route must finish within a bounded 2y horizontal endpoint envelope around its selected safe point and preserve emergency/configured clearance before Sentinel begins landing; this matches normal vnavmesh completion without restoring the former broad 5y arrival sphere. A live-entity exit veto can never place a still-mounted/flying character into `SafeWait`; it resumes parking instead. After landing and dismounting, optionally take a protected 0.9–1.0y outward step and return over prevalidated ground paths so ordinary movement leaves the character naturally facing the mark; any tag requirement or unsafe/unavailable path cancels this cosmetic settle immediately.
13. Maintain the configured emergency clearance as the mark moves.
14. Tag the exact live S/SS only when it is already in combat. Start at the configured engage threshold (95% by default), with optional fixed checkpoints at 75%, 50%, and 25%. Each handled action consumes the checkpoints already crossed. Sentinel retries unconfirmed attempts, uses the existing protected ground retreat after each confirmation, and waits at safe clearance before the next tag. An accepted Raise can arm one retag once the character can act. A stable full-HP/out-of-combat reset starts a new cycle.
15. Mark a hunt cleared only from positive evidence: a context-matched HuntAlerts/Sonar death event, a matching game hunt/reward kill message, or a previously identified live battle object becoming visibly dead. Every completion source passes through one final live-entity conflict check, so a visible current mark with HP remaining cannot enter a post-kill state. Object absence alone is never death evidence.
16. Treat the exact positively identified current entity as the final authority before every automatic exit. If that same object is visible with HP remaining in the expected World, territory, and instance, block `FailCurrent`, recovery-budget abandonment, false post-kill recovery, and framework-exception resets; cancel any tentative kill/discard record and resume local tag or safe-wait recovery. Log the attempted source/state, mark, object ID, HP, physical context, and reason. Explicit manual **SKIP CURRENT + KEEP QUEUE** remains authoritative.
17. After a normal Shadowbringers, Endwalker, or Dawntrail S rank that was locally identified and observed in combat dies, remain in its territory for a shared four-second SS-evidence check. A matching zone message received immediately before or after kill confirmation, a HuntAlerts/Sonar report, or a visible precursor enters the five-minute SS watch. Prey coordinates are never destinations: every reservation and early SS report is normalized to the territory's centralized documented SS spawn point, where Sentinel selects connected ground in a tight approximately 25y staging envelope until the actual SS entity is detectable.
18. Keep each expansion's chain separate: Forgiven Gossip leads to Forgiven Rebellion, Ker Shroud leads to Ker, and Crystal Incarnation leads to Arch Aethereater. Precursors are observed only and never replace the active target, trigger navigation, or receive attacks.
19. If the matching SS is announced, reported, or queued while the player is alive, retain the completed S-rank context and stage at the fixed spawn point; replace that context with the SS only when the actual SS object is detectable. If the player is dead after the normal S rank's positive completion, reserve that SS ahead of every ordinary queued S rank, take the normal Return to Ul'dah, and then travel back to the SS territory through the standard hunt flow.
20. If dead while the current S/SS target is still alive, identify the actual Raise prompt and submit its affirmative callback regardless of which button is visually focused; never use Return. Raise handling remains active even when the mark temporarily leaves object range and during the two-second tagged post-kill reward/credit grace. A completed-hunt Raise prompt that does not close after bounded affirmative attempts is safely dismissed so it cannot block Return forever. An already-open Return prompt may be adopted only after the grace and only when it explicitly names Ul'dah/Steps of Nald.
21. Incidental overworld aggro first uses protected escape routes that do not cross the S/SS safety radius. After a bounded number of failed escape attempts, a WAR/MRD client targets only the positively identified non-mark NPC attacking it and uses a minimal Heavy Swing/Tomahawk fallback until that attacker clears, then resumes the exact hunt state. The current S/SS is excluded by object ID, data ID, and name.
22. Once the hunt and any SS opportunity finish, attempt a normal Teleport directly to Ul'dah on the **current visited World**, including while mounted/flying when the game permits it. Landing is used only after concrete Teleport rejection, not as a prerequisite. Remove kill-reported or stale queue entries, physically confirm Ul'dah arrival, and then start the next valid queued target. Actual or reserved SS opportunities sort ahead of ordinary S ranks; ordinary S ranks retain expansion priority and oldest-first order. Sentinel never World Visits back to the character's Home World automatically.

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
- Default mount: **Company Chocobo** when unlocked and usable, otherwise the existing **Mount Roulette** action. Preferred-mount failure never blocks the hunt.
- At most one confirmed ranged action per available checkpoint per pull, plus one optional retag per accepted Raise. A late arrival never fires several missed checkpoints in sequence.
- Pugilist/Monk, non-combat jobs, and other unsupported jobs wait without attacking
- ShB/EW/DT SS check: **4 seconds** after each supported normal S-rank death, then **5 minutes** once that expansion's precursor chain is observed
- Forgiven Gossip, Ker Shroud, and Crystal Incarnation: observation-only; never navigation or combat targets
- Pending queue: saved in plugin configuration, restored on the first main-thread framework tick (never from Dalamud's possibly asynchronous plugin constructor), deduplicated, kill-invalidated, and ordered SS first, then expansion priority, then oldest first. Ordinary alerts are stale after **45 minutes** by default; an unconfirmed prey-based SS reservation expires with the **5-minute** chain watch.
- No coordinate writes or coordinate warping
- Missing marks, unavailable coordinate projections, and unreachable local routes are neutral rather than death evidence. They receive bounded recovery and can end only as an explicit unresolved abandonment through Ul'dah, never as a false kill
- Chernobog alerts are intentionally ignored and never queued because U'Ghamaro Mines navigation is unsupported; every other supported Centurio S rank remains enabled.

## Dependencies

- **vnavmesh** is required for navigation.
- **HuntAlerts and Sonar** are the production alert providers and should both be installed and enabled.
- **Lifestream** is required for automated hunts and owns World travel. Enable a build with Patch 7.58 World Visit support. Same-World hunts do not submit a World change.
- HuntTrainAssistant, Wrath Combo, and BossMod are **not required or used**.

Patch 7.58 World Visit supports Aether, Crystal, Dynamis, and Primal for North American characters. Eligibility uses the physical region in the game data. Under **Data Center Hunting**, all four North American DCs are on by default. Disabling a DC drops its queued hunts and excludes its reports from History while leaving the active hunt alone. After a hunt, Sentinel stays on the visited World in Ul'dah until another eligible alert requires a World change. See [regional World Visit and provider setup](docs/regional-world-visit.md).

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
5. After public ZIP validation, the pinned shared distribution action promotes the owning entry in this repository's root `repo.json`, optionally notifies the central generator, and always verifies the exact public child/central entry. This also applies to SentinelHunts without changing SRankSentinel's entry. No child writes the central manifest.

Dalamud compares `AssemblyVersion` in `repo.json` with the installed assembly. The tester receives the newer beta through the normal **Update** button while development can continue on `main` between published versions.

`DALAMUD_CATALOG_TOKEN` is optional for faster `plugin-released` notification. Without it, the hourly central reconciliation is used and verification waits up to 90 minutes. The normal child `GITHUB_TOKEN` handles only this repository's release and manifest. See [release infrastructure](docs/RELEASE_INFRASTRUCTURE.md).
# Configuration appearance

Existing users remain on **Classic** through an explicit version-16 configuration migration.
Select **Sentinel Modern** in the window's **Window theme** setting to opt in. Modern groups
the same controls into Main and Distance Profiles, with Plugins and History alongside them. Pages start directly with their
settings without a separate page heading or introductory text. Main combines hunt status,
expansion switches, and recovery actions. Modern uses one compact custom header, a slim
Font Awesome icon rail, and responsive settings rows on a continuous shared surface.
The Theme page offers a compact bottom-left **Use Classic Theme** button; Classic retains its native title bar and
the **Window theme** selector for switching back to Modern.
The existing window identity, saved position, close/collapse controls, hunt configuration, and
keyboard/controller activation are retained.

**Plugins** shows vnavmesh, Lifestream, HuntAlerts, and Sonar, their live status and setup needs,
and settings/installer actions. Installed, disabled plugins can be enabled explicitly when
Dalamud's default collection allows it. Collection-managed or unavailable plugins use the
installer. **History** keeps the latest 500 spawn reports for enabled hunt expansions, with
tag and confirmed-credit status on each row and a daily stacked untagged/tagged chart.
Credit requires a confirmed final-pull tag, positive kill evidence, and a matching in-game
Seals or Nuts reward. Disabled expansion records are hidden without deleting earlier saved
data. One Clear History button clears reports and the retained legacy credit backup.
It starts with new alerts; previous suppression records do not imply personal credit.
History never changes hunt decisions.

The plugin bundles the exact published SentinelCore and SentinelCore.UI 0.3.1 assemblies.
No separately installed SentinelCore plugin is required. See
[the integration and validation notes](docs/SentinelModern.md) for build instructions.
