# Remote recovery v1 — experimental

Candidate **SRank 0.7.62.0**, based on published **0.7.56.0**, paired with experimental
Relay **0.5.0.6**. No release/catalog promotion or live acceptance yet.
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

Use the exact successful CI SRank **0.7.62.0** and Relay **0.5.0.6** artifacts;
unload prior dev copies and keep public copies disabled. Follow the
[Wrothy-first paired procedure](https://github.com/MarshalTitan/SentinelRelay/blob/feature/srank-remote-controls/docs/SRANK_REMOTE_CONTROLS.md):
permission on/save; status; safe skip preserving queue; reset request with no action;
confirm within 30s; wrong profile and permission-off rejection.
No live behavior or private Discord authentication is certified by a successful build.
Rollback: permission OFF/save, unload experimental plugins, re-enable public
SRank **0.7.56.0** / Relay **0.5.0.5**. Do not delete configuration.
