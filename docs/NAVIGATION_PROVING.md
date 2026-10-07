# Controlled shared-navigation proving build

Current candidate: **SRankSentinel 0.7.60.0**, bundled published **Core 0.4.1**.
PR #25 remains unmerged/unreleased; public catalog and rollback baseline remain **0.7.54.0**.
Default travel, configuration, UI, S/SS semantics, crowd-aware parking, facing, tagging,
kill/reset evidence and return policy are unchanged. No PvP migration.

## Accepted evidence and current gate

The inspected 0.7.59.0 probe passed current-zone readiness, mount, takeoff, query and continuous
following with zero retries. The user subsequently confirmed active-flight cancellation and
immediate replacement without a reported late interruption. Final InFlight=true leaves landing
open. Do not repeat that old flight/cancellation test.

Only the isolated probe now requests explicit landing. Ordinary hunt approach still hands off
to existing SRank parking/landing policy. Core retains the same operation through Landing, stops
its owned follower first, invalidates pending queries and permits only synchronous, bounded native
landing requests. The adapter submits the existing native general action 23 only while in flight,
mounted, near the saved floor and out of combat, with no follower. Submission never proves success.

Success requires InFlight=false, positive local ground evidence and destination/floor bounds,
stable for 0.75 seconds. The adapter excludes loading, jumping, swimming/diving and mount transitions
and compares a raised local floor query with player height. Missing ground evidence is unknown,
not success. Landing has a fixed 20-second deadline independent of repeated action attempts;
drift, dependency loss, zoning, combat and hunt activation stop or fail visibly with diagnostics.
There is no unconditional dismount, delayed landing task or automatic legacy fallback.

Ownership is consumer-local, not a lock against other plugins. vnavmesh has no mesh-zone identity
IPC; readiness is inferred from observed zone epochs and stable ready/negative build progress.
The real landing action and physical-ground inference require the focused live proof below.

## Obtain and load

Download the **0.7.60.0** artifact from PR #25's latest successful Build workflow.
The outer artifact ZIP contains latest.zip. Extract latest.zip into the separate dev-plugin
directory used for previous candidates, replacing that candidate while it is unloaded, and reload
SRankSentinel.dll. Keep the installed public SRank copy disabled and all other movement automation
stopped. Confirm version 0.7.60.0. No configuration migration or public release is involved.

## Short supervised landing test

1. Turn the main SRank Enabled toggle **off** and confirm **Idle**, out of combat. On clear flat
   outdoor ground with flight unlocked, 150–400 yalms from an aetheryte, land manually.
   Run `/sranknavtest on`, then `/sranknavtest probe set`; remain still until destination-set confirmation.
2. Teleport to another zone and back to the saved zone/world/instance's aetheryte. Run
   `/sranknavtest probe run cancel-landing`. Observe the flight. At Landing the armed test cancels
   before the first native landing action and confirms follower stop. This makes the cancellation
   point repeatable without racing a short transition.
3. Immediately run `/sranknavtest probe resume`. This creates a **new operation** near the saved point.
   Expect actual physical landing, no late action from the cancelled operation, and
   **SHARED probe landed: physical ground confirmed**. Merely hovering at ground level is FAIL.
4. Run `/sranknavtest off`. It restores legacy travel and exports. Return PASS/FAIL plus the final
   JSON Entries (paste if attachment access fails). Required evidence: cancelled first operation,
   distinct replacement ID, Landing → Arrived with Reason=GroundConfirmed, Grounded=true and
   InFlight=false. Only on failure include timestamp and a short clip or description.

No S-rank spawn is needed. Do not deliberately induce an unsafe landing to test timeout;
automated tests cover rejection, deadline, dependency loss, cancellation and stale work.
The armed live cancellation proves revocation before the first landing action; cancellation after
submission is covered in isolation and is not claimed live-proven by this procedure.

## Rollback and failure

A probe failure disables proving and leaves main Sentinel disabled; export first. Use
`/sranknavtest off`. If follower stop cannot be confirmed, keep automation disabled, stop vnavmesh,
then retry off. Unload the dev candidate and re-enable the accepted public **0.7.54.0** copy.
Do not run both copies. Reload clears proving and the in-memory destination. Coordinates are never
saved in configuration or sanitized export.

## Subsequent gate

This mechanics test does not certify hunt safe parking, facing, tagging, kill evidence or return.
After landing passes, prepare the next controlled ordinary-hunt integration, preserving crowd-aware
policy and rollback. Require actual shared movement plus zone load → navmesh ready → mount →
takeoff → continuous flight → existing safe landing → tag → kill/return before broader adoption.
PR #25 stays unpublished and PvP migration stays blocked until the applicable gates pass.
