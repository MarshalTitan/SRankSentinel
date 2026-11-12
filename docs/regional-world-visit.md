# Patch 7.58 regional World Visit

[Square Enix's rollout notice](https://na.finalfantasyxiv.com/lodestone/topics/detail/5c3b0f9e2fa2295dfc32c19e02e57e4e0614a09c) describes the logical data center selector at the three starter-city aetherytes and travel without a Home World detour. Sentinel uses the Ul'dah aetheryte throughout. NA hunts can use Aether, Crystal, Dynamis and Primal. A destination must be a public World in the current and Home World's physical region.

- In the [World sheet](https://github.com/xivapi/ffxiv-datamining/blob/master/csv/en/World.csv), `LogicalDataCenter` (API 15 Lumina `World.Unknown0`) identifies the real logical DC. The older `DataCenter` column can identify the combined matchmaking group. `WorldDCGroupType.Region` gives the physical region.
- The installed Patch 7.58 `ui/uld/worldtranslate.uld` (`ffxivgame.ver` `2026.11.03.0000.0000`) puts the regional panel at resource node 2. Its header node 3 owns the actual dropdown at node 4 (component 1010); the panel owns the World list at node 7 (component 1019). The dropdown owns its own item list. The legacy list is node 20. Casting node 2 to a dropdown rejected the valid live Crystal page in 0.7.65.0.
- The installed [Lifestream 2.6.0.13](https://github.com/NightmareXIV/Lifestream/releases/tag/2.6.0.13) binary confirms the Patch 7.58 addon callbacks: DC `(1, dcSlot)` and World `(0, worldSlot + 2)`. Slots come from the native `WorldTranslate` string array: DC labels start at 0 and World labels start at the number of regional DCs plus 2. Sentinel verifies the actual selected dropdown, exact slot order and rendered World list before input. Lifestream is not a dependency.
- The live public instance state is checked after zoning settles. In a single-instance zone such as Heritage Found in 7.58, a stale report for instance 2 is treated as instance 1. A zone with live numbered instances still uses the existing Aetheryte instance-change recovery.

## Alert providers

| Provider | What to check |
| --- | --- |
| Sonar | Set **Receive Jurisdiction** to **Region**. Ensure S/SS reports and map links are enabled. See [Sonar settings](https://wiki.ffxivsonar.com/settings/config/general-tab). |
| HuntAlerts | Enable S-rank notifications for the four NA DCs where the installed build offers that scope. The public [HuntAlerts fork](https://github.com/priestkos/HuntAlerts) applies provider scope before emitting IPC. If a build delivers only current-DC reports, Sentinel cannot see its other reports. |

Logs beginning with `Provider regional observation` show each parsed report's provider, action, World, logical DC and eligibility alongside the character's current World and logical DC. A received cross-DC spawn and death from each provider verifies the installed provider's scope. Code review and automated tests do not establish live provider coverage.

## In-game acceptance

First reproduce the reported route: start in Ul'dah on Zalera (Crystal) and use an eligible Midgardsormr (Aether) alert. Watch for the selector changing to Aether, the Aether rows refreshing, exact Midgardsormr selection, the normal confirmation, and arrival on Midgardsormr before territory teleport. With an eligible cross-DC alert, look for the attempt, logical DC selection, exact World row, and confirmed World arrival in the log. After the hunt, verify that the next visit starts from Ul'dah on the visited World. Repeat with a World on a different logical DC and with a same-World alert. Check that an unavailable destination exhausts the retry budget without removing other queued hunts, and that a stale Heritage Found instance report does not open the instance menu.

If the menu is not understood or refreshed, the `World Visit UI before close` warning includes the observed node types, parents, counts, selected DC index, labels, event ownership, and bounded `WorldTranslate` slots. This is a UI read or refresh failure, separate from a native visit rejection.
