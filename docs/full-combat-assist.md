# Full combat assist - release gate

Draft only. The setting is off by default. No Wrath or BossMod dependency is required for safe tagging.
The draft includes the post-confirmation handoff, lifecycle policy, optional readiness probes,
and simulated provider tests. The installed Wrath provider cannot satisfy the grant contract.
This draft cannot enable Wrath rotation and must not be released as a working combat mode.

## Verified contracts

- [Wrath 1.0.4.27 rotation implementation](https://github.com/PunishXIV/WrathCombo/blob/5365eb1edc13654be945a624b2311a8b8541a599/WrathCombo/AutoRotation/AutoRotationController.cs):
  `GetSingleTarget(Manual)` returns the hard target directly. The per-enemy
  `OnlyAttackInCombat` filter lives in `DPSTargeting.Query` for automatic selection.
  `ExecuteST` can retarget and submit to the manual target without rechecking that enemy's combat flag.
  The separate `InCombatOnly` check is a **party** combat check.
  AoE selection may also override Manual via `AoEIgnoreManual`.
- [Wrath public IPC](https://github.com/PunishXIV/WrathCombo/blob/5365eb1edc13654be945a624b2311a8b8541a599/docs/IPC.md):
  rotation state/configuration are lease overrides, with cancellation callback and release.
  Neither actor-bound hostile execution nor a pre-dispatch veto is exposed.
  Per-action request blacklists take an action type, action ID and duration, not a target and predicate.
- [BossMod public IPC](https://github.com/awgil/ffxiv_bossmod/blob/02a16b4425ceed2b63994468da406adddefcd57b/BossMod/Services/IpcService.cs):
  read AI.IsEnabled and HasModuleByDataId using the identified actor's BaseId;
  Configuration with [AIConfig, ForbidMovement], save=false, returns the current field.
  Only one parseable False permits movement. No AI toggle or config write.

## Why the integration refuses control

Counterexample: the mark is the hard target, Sentinel confirmed its tag, another party member
is still fighting an add, and the mark leashes between the two plugins' updates. Manual targeting
can still dispatch a hostile action to the reset mark. Automatic targeting can instead select
the add. A lease, a faster poll, a target assignment, or a last-known combat flag cannot guarantee
the required property at dispatch. This is a source-level counterexample, not a claim of a live pull.

For release, Wrath needs supported lease-scoped enforcement that confines hostile actions to
the named actor while alive and in combat, including retargets and AoE paths. Any override must
be in place **before** enabling rotation; rejected acquisitions must release partial overrides;
revocation, reset, death, disable, and unload must relinquish the Sentinel lease only.
Do not invent an IPC name or replace this requirement with a private hook.
Test that contract against a real provider before enabling the adapter or bumping the public version.
