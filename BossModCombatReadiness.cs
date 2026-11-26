using Dalamud.Plugin;

namespace SRankSentinel;

internal static class BossModCombatReadiness
{
    // The built-in VBM Multibox preset contains AutoTarget, FollowSlot and NormalMovement.
    // MiscAI as a whole is not movement-only: PassiveAggressive can pull with a GCD.
    // Requiring an active movement module also rules out the empty preset / planner fallback.
    internal static string? Unavailable(IDalamudPluginInterface pi, uint exactOid)
    {
        try
        {
            bool Flag(string name) => pi.GetIpcSubscriber<bool>("BossMod." + name).InvokeFunc();
            if (!Flag("AI.IsEnabled")) return "BossMod AI disabled";
            var result = pi.GetIpcSubscriber<IReadOnlyList<string>, bool, List<string>>("BossMod.Configuration")
                .InvokeFunc(new[] { "AIConfig", "ForbidMovement" }, false);
            if (result is not [var value] || !bool.TryParse(value, out var forbidden))
                return "BossMod AI movement permission unknown";
            if (forbidden) return "BossMod AI movement disabled";
            if (Flag("Rotation.IsAutorotationActive")) return "Disable BossMod rotation to use a separate rotation provider";
            if (Flag("AI.IsAutoTargetEnabled") || Flag("Rotation.IsAutoTargetActive"))
                return "Disable BossMod auto-target for Full Combat Assist";
            const string prefix = "BossMod.Autorotation.MiscAI.";
            var modules = pi.GetIpcSubscriber<List<string>>("BossMod.Rotation.GetActiveModules").InvokeFunc();
            if (modules is null || !modules.Contains(prefix + "NormalMovement"))
                return "BossMod movement module is not active";
            if (modules.Any(m => m is not (prefix + "NormalMovement" or prefix + "FollowSlot" or
                prefix + "AutoTarget" or prefix + "GoToPositional" or prefix + "StayCloseToPartyRole" or prefix + "StayCloseToTarget")))
                return "BossMod has action-capable or unverified modules active";
            if (exactOid == 0) return "Waiting for an exact active mark to check encounter support";
            return pi.GetIpcSubscriber<uint, bool>("BossMod.HasModuleByDataId").InvokeFunc(exactOid)
                ? null : "BossMod has no module for this mark";
        }
        catch { return "BossMod readiness IPC unavailable"; }
    }
}
