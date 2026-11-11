# Patch 7.58 regional World Visit

[Square Enix's rollout notice](https://na.finalfantasyxiv.com/lodestone/topics/detail/5c3b0f9e2fa2295dfc32c19e02e57e4e0614a09c) describes the logical data center selector at the three starter-city aetherytes and travel without a Home World detour. Sentinel uses the Ul'dah aetheryte throughout. NA hunts can use Aether, Crystal, Dynamis and Primal. A destination must be a public World in the current and Home World's physical region.

- In the [World sheet](https://github.com/xivapi/ffxiv-datamining/blob/master/csv/en/World.csv), `LogicalDataCenter` (API 15 Lumina `World.Unknown0`) identifies the real logical DC. The older `DataCenter` column can identify the combined matchmaking group. `WorldDCGroupType.Region` gives the physical region.
- The [regional WorldTravelSelect layout reference](https://github.com/Caraxi/SimpleTweaksPlugin/blob/main/Tweaks/UiAdjustment/ImprovedWorldVisit.cs) identifies selector node 2 and World list node 7. Sentinel verifies typed controls, live labels, and registered list events before taking action. It will not select from old rows after changing the dropdown. Lifestream's public source was reviewed for World Visit behavior and the confirmation text; Sentinel uses no Lifestream IPC for this path.
- The live public instance state is checked after zoning settles. In a single-instance zone such as Heritage Found in 7.58, a stale report for instance 2 is treated as instance 1. A zone with live numbered instances still uses the existing Aetheryte instance-change recovery.

## Alert providers

| Provider | What to check |
| --- | --- |
| Sonar | Set **Receive Jurisdiction** to **Region**. Ensure S/SS reports and map links are enabled. See [Sonar settings](https://ffxiv-sonar.github.io/Sonar/). |
| HuntAlerts | Enable S-rank notifications for the four NA DCs where the installed build offers that scope. The public [HuntAlerts fork](https://github.com/priestkos/HuntAlerts) applies provider scope before emitting IPC. If a build delivers only current-DC reports, Sentinel cannot see its other reports. |

Logs beginning with `Provider regional observation` show each parsed report's provider, action, World, logical DC and eligibility alongside the character's current World and logical DC. A received cross-DC spawn and death from each provider verifies the installed provider's scope. Code review and automated tests do not establish live provider coverage.

## In-game acceptance

Start in Ul'dah on Crystal. With an eligible cross-DC alert, look for the attempt, logical DC selection, exact World row, and confirmed World arrival in the log. After the hunt, verify that the next visit starts from Ul'dah on the visited World. Repeat with a World on a different logical DC and with a same-World alert. Check that an unavailable destination exhausts the retry budget without removing other queued hunts, and that a stale Heritage Found instance report does not open the instance menu.
