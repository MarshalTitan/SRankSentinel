import { readFileSync } from "node:fs";
const read = p => readFileSync(p, "utf8").replace(/\r/g, "");
const assert = (value, reason) => { if (!value) throw new Error(reason); };
const ui = read("Plugin.Ui.cs"), remote = read("Plugin.RemoteRecovery.cs");
assert(ui.includes('if (DrawActionButton("SKIP CURRENT + KEEP QUEUE", danger: true))\n            SkipCurrentKeepQueue();'), "UI skip must use shared operation");
assert(ui.includes('if (DrawActionButton("STOP + RESET THROUGH UL\'DAH", danger: true))\n            StopAndResetThroughUldah();'), "UI reset must use shared operation");
assert(remote.includes("SkipCurrentKeepQueue, StopAndResetThroughUldah);"), "IPC must bind the same UI recovery operations");
assert(remote.includes('private void SkipCurrentKeepQueue() =>\n        FailCurrent("Current hunt skipped manually", HuntExitRequestSource.ManualSkip);'), "Original skip semantics changed");
assert(remote.includes("    private void StopAndResetThroughUldah()\n    {\n        pendingAlerts.Clear();\n        PersistQueue();\n        if (current is null)\n            SetState(SentinelState.ResetToUldah,\n                \"Manual reset requested\",\n                HuntExitRequestSource.ManualSkip);\n        else\n            FailCurrent(\"Stopped manually\", HuntExitRequestSource.ManualSkip);\n    }"), "Original reset semantics changed");
for (const endpoint of ["Status", "SkipCurrentKeepQueue", "StopResetThroughUldah"])
    assert(remote.includes('"SRankSentinel.Remote.v1.' + endpoint + '"'), "Missing fixed v1 endpoint");
assert((remote.match(/GetIpcProvider</g) ?? []).length === 3, "Recovery surface must stay narrow");
console.log("Remote recovery audit passed: UI/IPC share exact existing actions; three fixed v1 endpoints only.");
