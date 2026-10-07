# Controlled shared-navigation proving build

Current candidate: **SRankSentinel 0.7.61.0**, bundled published **Core 0.4.1**.
PR #25 remains unmerged/unpublished; public catalog and practical rollback baseline remain **0.7.54.0**.
No configuration/UI migration, facing change, S/SS policy, tag threshold or kill/return change.
PvP migration remains blocked.

## Accepted mechanics evidence

0.7.59.0 passed observed readiness/mount/takeoff/query/following, active-flight cancellation and
immediate replacement. On 0.7.60.0 the user confirmed physical unmount/landing, stopped cancellation
and successful resume. The inspected excerpt records replacement
75ac0f50-ebca-443c-aad4-9c4cfe4c6fc5: Landing → Arrived/GroundConfirmed at 15:37:29.918 UTC,
and ProbeCancelled for 2d7c66c4-9243-41ed-b792-a6ed043477d0 at 15:37:23.812 UTC.
The excerpt omits PhysicalContext; do not claim those JSON fields were inspected.
This scoped human/transition PASS closes the mechanics landing gate, not actual hunt integration.
No repeated 0.7.60.0 probe is needed. The isolated probe's bounded landing remains available.

## Controlled ordinary-hunt integration

Two earlier natural hunts detected the mark before the shared coordinate approach. Preserve that
working detection. This candidate additionally permits Core to own **pre-tag protected flight
parking** after the original domain algorithm has approved the path AND connected landing ground.
All crowd/geometric candidate selection, clearance, protected-radius checks and preference
bookkeeping remain the same. Unprotected fallback routes, SS, non-flight movement and post-tag/
randomized retreat remain legacy.

The adapter gives Core an immutable one-use copy of that approved route. Core's Pathfinding state
here accepts an already domain-queried/validated path; it does not run a fresh unprotected query.
Before following, recheck the live entity, zone, selected point, protected path and clearance.
Core handles readiness, confirmed takeoff/following, waypoint progress and bounded stops.
Approved-route retries are zero: failure disables proving/Sentinel, retains the hunt and exports;
there is no silent legacy fallback or alternate query outside domain approval.

The exact original alignment/clearance and landing-revalidation checks run BEFORE the shared tick.
At the safe handoff Core is cancelled/stopped and loses its lease, then the existing consumer lands,
settles/faces, tags once, observes positive kill/reward grace and returns. Core landing is deliberately
not requested for hunt parking in this stage: the accepted isolated landing contract must not
replace crowd-aware domain policy implicitly. A planned ParkingLandingHandoff observation and chat
include the operation ID and its last Core state. Retired handles cannot affect tag/retreat/return.

## Obtain and load

Download **0.7.61.0** from PR #25's successful Build artifact. The outer artifact contains latest.zip;
extract it into the separate dev-plugin directory while the old candidate is unloaded, replace it,
and load SRankSentinel.dll. Confirm 0.7.61.0 and keep the installed public copy disabled.
Stop other movement automation. Proving always starts OFF after reload.

## Short supervised ordinary-hunt gate

1. Idle with no hunt/movement, run `/sranknavtest on`. Keep existing hunt/parking/tag settings.
   Enable main Sentinel and use one ordinary live S-rank report requiring zoning and flight,
   preferably while the mark is still near full HP. Avoid SS for this gate.
2. Observe zone load → ready mesh → mount/takeoff → continuous flight. Look for
   **SHARED protected parking started**. At arrival expect
   **SHARED protected parking handoff ... state=Following** for the same ID.
   Existing safe landing must physically unmount/land, then tag once at the configured threshold,
   retain normal post-tag retreat, confirm the kill/reward wait and return to Ul'dah.
3. Once Idle, run `/sranknavtest off`. Return PASS/FAIL for travel, physical landing, tag and return,
   plus the two short shared parking chat lines with the matching operation ID.
   Timestamp/clip and last diagnostic transitions are needed only on failure.

No second probe, second STOP hunt or repeated UI acceptance is requested; Core cancellation/
replacement/landing mechanics already passed and consumer cancellation priorities have isolated tests.
A route that never reaches Core Following does not satisfy this gate. If no eligible shared route
starts, send the chat/status and final domain states; do not change tag/parking settings or wait for
repeated spawns blindly. Source review will determine the next controlled action.

## Rollback

A proving failure leaves main Sentinel disabled and the hunt retained. Export first, run
`/sranknavtest off`, then explicitly enable for legacy travel if stop is confirmed. When off is used
during shared parking, return to LocateMark so the existing protected policy resamples safely.
If stop cannot be confirmed, keep automation disabled, stop vnavmesh and retry off.
Unload the dev candidate and re-enable public **0.7.54.0**. Never run both copies.
No stored position/layout/configuration or catalog entry changed.

## Boundaries and diagnostics

Movement ownership is local to this backend, not a global inter-plugin lock. Current-zone readiness
uses observed zone/world/instance epochs and stable mesh/build readiness; upstream lacks mesh-zone
identity IPC. Core transitions and consumer handoff events are sanitized/allowlisted; coordinates,
entity IDs, credentials, config and chat contents are excluded. Approved route data exists only in
memory and is cleared on cancellation/disposal. Returned live evidence automatically resumes
engineering. Broader adoption, PR publication and PvP migration remain gated on applicable proof.
