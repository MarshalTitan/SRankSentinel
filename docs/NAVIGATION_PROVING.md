# Controlled shared-navigation proving build

This PR-only build is 0.7.58.0 with published Core 0.4.0 (release source 67e52f5d4afb080042f9e526a6afae7480b01ff0).
The catalog and accepted comparison build remain SRankSentinel 0.7.54.0.
Default behavior is the accepted legacy travel implementation. No configuration schema, UI layout,
facing policy, S/SS policy, tagging, death/reset evidence or catalog entry is changed.

## Scope

Run /sranknavtest on while idle before choosing an ordinary S-rank report. This session-only switch
uses Core for observed mesh/build readiness, mounting, takeoff, async pathfinding, following,
waypoint progress and bounded recovery toward the existing projected report destination.
It returns control at the existing scan radius or visible-entity handoff. Existing parking/landing,
tagging, kill/reward grace and Ul'dah return remain authoritative. SS and approximate-coordinate
recovery are not proving targets. No PvPSentinel migration.

The adapter uses Nav.PathfindCancelable; missing IPC fails visibly. Current-zone readiness is
inferred from loading/territory/world/instance epochs and stable ready + negative build progress.
Upstream has no mesh-zone identity IPC, so supervised zoning evidence is required.

STOP/disabling cancels the shared operation; changing to a non-approach state cancels before
handoff. /sranknavtest off cancels and restores legacy approach; if stop cannot be confirmed,
rollback remains held and Sentinel stays disabled. On failure the hunt is retained, Sentinel
is disabled, and automatic fallback is forbidden. Export first, then off and explicitly re-enable.
Reload always starts with proving off. Old handles/task completions cannot stop the successor.

## Deterministic supervised probe

This candidate adds a session-only anchor-and-return probe because two natural hunts detected the mark
before the earlier shared approach branch and therefore exercised only legacy parking. Probe coordinates
remain in memory and are never exported or saved in configuration. The probe can run only with SRank
automation disabled, Idle, out of combat, in the exact saved territory/world/instance, with no external
vnavmesh route. It requires a finite route of at least 80 yalms and requires flight; an incoming hunt,
combat, enabling Sentinel or leaving Idle cancels it.

1. Load the PR artifact as a separate dev plugin and disable the public SRank copy. Keep all other
   movement automation stopped. In SRank, turn the main Enabled toggle off and confirm Idle.
2. In an outdoor zone with flight unlocked, manually choose clear ground 150-400 yalms from an aetheryte.
   Avoid enemies, cliffs, water, structures and narrow passages. Land there and run
   `/sranknavtest on`, then `/sranknavtest probe set`. Stay still while setup polls readiness for up to
   30 seconds. Continue only after the destination-set confirmation; a failure reports loading, mesh,
   build progress, current-zone readiness and flight state without coordinates.
3. Teleport to another zone, then teleport back to that same zone/world/instance. At the aetheryte run
   `/sranknavtest probe run`. The command refuses a start closer than 80 yalms.
4. Observe current-zone mesh wait -> mount -> takeoff -> one continuous flight -> arrival at the projected
   point. Use `/sranknavtest status` if needed; it must show a nonempty operation and a Core state.
   Arrival exports diagnostics automatically.
5. Teleport back to the aetheryte and run a second probe. While status is Pathfinding or Following, run
   `/sranknavtest probe cancel`. Movement must stop. Immediately run `/sranknavtest probe run` again;
   the replacement must reach the anchor without a late stop or route replacement from the cancelled task.
6. Run `/sranknavtest off`. It clears the session anchor, restores legacy behavior and exports again.
   Send navigation-proving.json plus PASS/FAIL for readiness, mount, takeoff, continuous flight, arrival,
   cancellation and replacement. Include a timestamp/short clip only for a failure.

`probe clear` removes the saved point when no probe is active. Reload always clears it and starts with
proving off. The deterministic probe validates Core movement mechanics, ownership and cancellation. It
does not certify hunt parking, facing, tagging, kill evidence or return policy. The two supplied hunts
remain evidence that those existing legacy branches functioned, not shared-path acceptance. PR #25
remains unreleased until this probe passes and a later controlled hunt integration reaches shared movement.

## Evidence correction in 0.7.58.0

The user supplied two complete 0.7.56.0 hunt traces from one plugin instance. Both reached
PrepareApproachDestination and then LocateMark in about 0.3 seconds, followed by MoveToSafePoint,
Landing, SafeWait and tag/return states. OperationsStarted remained zero and Core Entries stayed empty.
Source review confirms FindMark can resolve the NPC from the object table before the shared coordinate
approach, after which existing protected parking owns movement. The exporter retained both runs correctly.

0.7.58.0 keeps the bounded ProvingSession evidence and adds the isolated deterministic probe above.
Probe observations contain event names, domain state and operation IDs only; the saved point is excluded.
Core Entries remain genuine movement transitions. No routing, parking, facing, tagging, configuration or
catalog policy changes. The candidate remains PR-only.
