# Remote recovery v1

Versions: **SRank 0.7.62.0 / Relay 0.5.0.6**, based on prior SRank **0.7.56.0**.
The user authorized normal live-updater distribution on 2026-10-17 UTC with the Relay
permission default OFF. Real in-game acceptance remains pending.
Navigation PR #25 stays unchanged and separate.

## Contract

Exactly three namespaced Dalamud IPC functions, each expected-character-key string
input and JSON-string output:
`SRankSentinel.Remote.v1.Status`,
`SRankSentinel.Remote.v1.SkipCurrentKeepQueue`,
`SRankSentinel.Remote.v1.StopResetThroughUldah`.

Schema `sentinel.srank.remote.v1`; Version, Success, Reason, Status.
Status includes Loaded, Enabled, Ready, Character, State, StatusText (bounded),
Territory, ActiveHunt, QueueCount and Recovering. CharacterKey is deliberately not
serialized. Wrong-character status does not reveal another character's snapshot.
Only synchronous framework-thread calls are accepted. No command names, method names,
IPC names or arbitrary parameters come from Discord.

The two UI buttons and IPC invoke the SAME extracted methods with exact existing
ManualSkip/recovery semantics. Skip fails without an active hunt; mutations also fail
while disabled, loading/initializing, already resetting, logged out or disposed.
No implicit enable, hunt-policy/navigation/config change or queued recovery is added.
Read-only status does not call recovery methods or alter a queue.
Native/provider exceptions become bounded failure JSON, never exception text.

IPC is a trusted in-process integration, not authentication against installed plugins.
Relay owns Discord authorization/default-off permission/confirmation and replay checks.
SRank rechecks expected character and framework context at the local execution boundary.
No Relay install/runtime requirement is introduced for SRank UI or ordinary hunting.

## Automated evidence

Separate pure .NET contract tests cover read-only status, dispatch isolation, character/
thread/state guards, skip-without-hunt, idle reset, disposal/unsupported operation,
and exception sanitation. Source audits prove UI/IPC share exact old action bodies.
Existing state, native UI, source-hash and package checks remain required; no accepted
travel/tag/parking/reward algorithm is edited.

## Supervision and rollback

Update SRank **0.7.62.0** and Relay **0.5.0.6** through the normal permanent Sentinel
repository URL in Dalamud. Keep dev copies disabled. Follow the
[Wrothy-first paired procedure](https://github.com/MarshalTitan/SentinelRelay/blob/main/docs/SRANK_REMOTE_CONTROLS.md):
permission on/save; status; safe skip preserving queue; reset request with no action;
confirm within 30s; wrong profile and permission-off rejection.
No live behavior or private Discord authentication is certified by a successful build.
Rollback: permission OFF/save immediately stops remote controls while retaining normal
hunting/Relay. If a binary rollback is necessary, unload the new copies and use the
preserved prior SRank **0.7.56.0** / Relay **0.5.0.5** releases. Do not delete configuration.
