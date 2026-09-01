# S Rank Sentinel

A private-use / custom-repository Dalamud plugin prototype for safely approaching FFXIV S-rank hunts without early-pulling them.

## v0.1 scope

**v0.1 never attacks.** It is intentionally limited to validating safe travel/parking behavior.

1. HuntAlerts emits an S-rank event.
2. HuntAlerts / HuntTrainAssistant / Lifestream handle normal world and zone travel.
3. S Rank Sentinel waits until the correct territory is loaded and vnavmesh is ready.
4. It reads the map flag that HuntTrainAssistant opened and flies toward it, but only to a conservative outer radius.
5. It positively identifies the real S-rank object by creature name.
6. It computes a terrain-valid parking point about 65 yalms from the actual mark.
7. It lands/dismounts using the game's normal land/dismount action.
8. While waiting, if the S rank roams inside the emergency radius, it backs away on the ground.

### Safety defaults

- Flag approach: **80y**
- Normal waiting radius: **65y**
- Emergency minimum: **55y**
- Future engagement threshold: **95% HP** (not active in v0.1)
- If the map flag or actual S rank cannot be identified, Sentinel **stops instead of guessing**.
- No coordinate warping / SetPosition / teleport-to-mouse behavior is used.

## Dependencies

Installed/running in Dalamud:

- HuntAlerts
- HuntTrainAssistant (for the current travel workflow)
- Lifestream
- vnavmesh

Wrath Combo and BossMod are **not used by v0.1**. They are planned for the later combat handoff.

## Command

`/sranksentinel`

Opens the status/settings window.

## First-time GitHub setup

Create a **public** GitHub repository named `SRankSentinel`, then upload the contents of this folder to its `main` branch.

The custom repository URL will be:

`https://raw.githubusercontent.com/MarshalTitan/SRankSentinel/main/repo.json`

Do not add that URL to Dalamud until a `v0.1.0` GitHub release exists, because `repo.json` deliberately points to the pinned release ZIP.

## First release

After the source is pushed and the Build workflow succeeds:

1. Create/push tag `v0.1.0`.
2. The Release workflow builds `latest.zip` and publishes it as `SRankSentinel.zip` on the GitHub release.
3. Add the raw `repo.json` URL to Dalamud Settings -> Experimental -> Custom Plugin Repositories.
4. Install **S Rank Sentinel** from `/xlplugins`.

## Field-test plan

For the first real S-rank test, keep the plugin window open. Verify:

- Correct HuntAlerts event is accepted.
- Existing world/zone travel completes normally.
- Sentinel flies only to the outer approach radius.
- It identifies the actual mark before moving closer.
- It lands around the configured waiting distance.
- It never targets or attacks the S rank.
- If the mark roams inside the emergency radius, Sentinel moves away instead of toward it.

Use **STOP / ABORT** in the plugin window if anything looks wrong.
