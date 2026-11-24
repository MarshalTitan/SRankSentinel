# Solo combat assist

Available in S Rank Sentinel 0.7.75.0. **Stay and fight with Wrath + BossMod** defaults off. You can turn it off during a fight: Sentinel releases its Wrath lease and resumes normal tagging and retreat. You do not need to stop the plugin or reset the hunt.

## Setup

- Farm solo. In Wrath Combo, configure the current job's single- and multi-target combos for Auto Mode.
  Leave the standalone Auto-Rotation switch **off**. Sentinel turns rotation on under a temporary lease
  only after its own confirmed initial tag. It does not edit the user's job setup.
- Enable BossMod AI yourself and allow movement (disable “Disable movement”). Check that your BossMod
  setup actually moves in a supported solo encounter. Sentinel verifies AI, movement permission and the
  module for the exact boss data ID; permission is not confirmation that a dodge succeeded.
- Turn on **Stay and fight with Wrath + BossMod** under Tagging in Sentinel. When any prerequisite is
  unavailable, the normal initial tag, retreat, checkpoints and Raise retag still apply.

## During a hunt

Sentinel requires the exact, living S/SS in the correct context to be pulled, below the configured engage
threshold (inclusive), and initially tagged through its own handled-action check. It releases its movement
before the rotation handoff. Incidental enemies do not redefine the monitored boss. Engaged adds can take
rotation damage. Sentinel does not go hunting trash or intentionally pull a new enemy.

While assisting, the framework samples the boss before the regular hunt tick. Leaving combat, or regaining
five percentage points from the lowest HP observed (including gradual healing), immediately releases
the lease and clears the boss target if still selected. This is a precaution, not a kill or confirmed reset.
Ordinary safe tagging takes over for the rest of that hunt, including a subsequent pull of the same mark.
A new hunt needs its own confirmed initial tag. No reward message is required to re-arm.

A submitted action may still land across the reset. The public Wrath API has no per-action cancellation or
exact-actor dispatch guard. On player death, release the lease; after an accepted Raise, revalidate the same
actor and pull before resuming. On failed handoff the ordinary Raise-retag setting applies.

## Provider references

- [Wrath lease contract](https://github.com/PunishXIV/WrathCombo/blob/5365eb1edc13654be945a624b2311a8b8541a599/docs/IPC.md)
  and [lease implementation](https://github.com/PunishXIV/WrathCombo/blob/5365eb1edc13654be945a624b2311a8b8541a599/WrathCombo/Services/IPC/Leasing.cs).
  Seed the leased rotation state off, apply and read back InCombatOnly, manual DPS targeting,
  OnlyAttackInCombat and disabled quest/FATE bypasses. Enable last. Release only this lease. A revoked
  lease, changed effective settings, or an uncertain handoff returns to safe tagging.
- [BossMod IPC](https://github.com/awgil/ffxiv_bossmod/blob/02a16b4425ceed2b63994468da406adddefcd57b/BossMod/Services/IpcService.cs):
  AI.IsEnabled, HasModuleByDataId(actor.BaseId), and the read-only Configuration query for AIConfig ForbidMovement.

## Live acceptance

On a supported ordinary S, verify it stays parked until another player pulls, then confirms its own
tag before switching to AssistedCombat. Check rotation and actual mechanic movement. If the mark
naturally resets or heals, check the log for the suspension and confirm there is no second Wrath
grant on that hunt. On the next eligible hunt, check that a fresh tag is required. Also verify a
fight with the option off. Do not deliberately reset a public hunt to test this.
