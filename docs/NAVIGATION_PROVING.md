# Controlled shared-navigation proving build

This PR-only build is 0.7.55.0 with published Core 0.4.0 (release source 67e52f5d4afb080042f9e526a6afae7480b01ff0).
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

## Supervised test

1. Use the CI artifact from this PR as a separate Dalamud dev-plugin build; disable the installed
   SRank copy first so only one SRank instance runs. Do not install it into the custom repository.
   Keep 0.7.54.0 available for rollback. Keep other movement automations stopped.
2. While idle, /sranknavtest on; use an ordinary S-rank report requiring a zone change and flight.
   Record plugin/vnavmesh versions, world/zone/instance/mark, and the start time.
3. Observe zone load -> stable mesh readiness -> mount -> confirmed takeoff -> continuous flight.
   There must be no walking fallback after failed takeoff or recurring start/stop movement.
4. Observe existing safe landing -> single tag at configured threshold -> kill/reward wait -> Ul'dah return.
   Report facing separately; it remains a known plugin-specific defect.
5. /sranknavtest export writes navigation-proving.json into the plugin config directory (exact path in chat).
   Send that sanitized file, PASS/FAIL for each stage, and a short clip/timestamp for any failure.
6. In a second approach, disable Sentinel/STOP while a route is calculating or following.
   Confirm movement stops. Run /sranknavtest off, then explicitly re-enable for a legacy route;
   confirm no late shared task interrupts it. Reload and confirm /sranknavtest status says OFF.

Only after these pass should this PR be considered for catalog release or wider adoption.
SS exceptional recovery and PvP acceptance remain separate gates.
