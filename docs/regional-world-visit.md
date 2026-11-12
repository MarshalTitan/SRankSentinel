# Regional World travel

S Rank Sentinel requires [Lifestream](https://github.com/NightmareXIV/Lifestream) for automated hunts. Install or enable a current build with Patch 7.58 World Visit support. The Plugins page checks for both required IPC functions. If either is missing, Sentinel pauses automation and keeps eligible alerts queued. A same-World hunt still requires Lifestream to be available but does not submit a World change.

[Square Enix's Patch 7.58 rollout](https://na.finalfantasyxiv.com/lodestone/topics/detail/5c3b0f9e2fa2295dfc32c19e02e57e4a09c) allows World Visit within the region without a Home World detour. The installed game data groups Aether, Crystal, Dynamis, and Primal in the same North American World Visit group while preserving separate logical DC IDs. Sentinel uses the physical region for alert eligibility.

Sentinel starts World travel only after its existing reset to Ul'dah. It stops its movement and submits the exact World via `Lifestream.ChangeWorld`, without requiring the Lifestream overlay. The installed Lifestream 2.6.0.13 handles destinations in the shared World Visit group through its World Visit route. Sentinel leaves movement to Lifestream while the provider is busy. An accepted request starts an arrival watch; Sentinel hands off to its own territory teleport only when the game reports the requested World and an interactable character, the game is settled, and Lifestream is idle. Sentinel then handles the entire local hunt and returns to Ul'dah on the visited World.

There are at most two World travel requests per hunt. After a rejected request or a timeout without arrival, Sentinel can retry once when travel is idle. If Lifestream or the game is still traveling, Sentinel waits without aborting or sending competing travel commands. A final failure is recorded as unresolved and the remaining queue can continue. World travel logs record destination World and logical DC, current World, readiness, requests, and arrival or unresolved recovery.

The live public instance state is still checked after territory zoning. In a single-instance zone such as Heritage Found in 7.58, a stale report for instance 2 is treated as instance 1. Numbered instances use the existing Aetheryte recovery.

## Alert providers

| Provider | What to check |
| --- | --- |
| Sonar | Set **Receive Jurisdiction** to **Region**. Ensure S/SS reports and map links are enabled. See [Sonar settings](https://wiki.ffxivsonar.com/settings/config/general-tab). |
| HuntAlerts | Enable S-rank notifications for the four NA DCs where the installed build offers that scope. The public [HuntAlerts fork](https://github.com/priestkos/HuntAlerts) applies provider scope before emitting IPC. If a build delivers only current-DC reports, Sentinel cannot see its other reports. |

Logs beginning with `Provider regional observation` show each parsed report's provider, action, World, logical DC and eligibility alongside the character's current World and logical DC. A received cross-DC spawn and death from each provider verifies the installed provider's scope. Code review and automated tests do not establish live provider coverage.

## In-game acceptance

1. On Zalera in Ul'dah, receive an eligible Balmung alert, then return to Zalera for a Midgardsormr alert. Confirm the `Lifestream.ChangeWorld accepted` and `World arrival confirmed from game state` logs, then verify Sentinel begins its normal territory teleport.
2. From the visited World, continue the queue through Midgardsormr → Seraph → Leviathan → Zalera. Confirm that each request names the exact destination and that Sentinel returns to Ul'dah on the current World after each hunt.
3. Check that a same-World hunt submits no World change, disabling Lifestream pauses automation, and an unavailable destination does not remove the other queued hunts.
