using Dalamud.Plugin;

namespace SRankSentinel;

// Public read-only IPC. Never enable BossMod AI, edit its config, or change the user's job setup.
// Wrath 1.0.4.27 has no protected hostile-target lease. Do not register a rotation lease until
// its provider can enforce that contract; see docs/full-combat-assist.md.
internal sealed class CombatAssistIpc(IDalamudPluginInterface pi) : ICombatAssistProvider
{
    internal const string MissingGuard = "Wrath cannot lock hostile actions to this in-combat mark";
    public string? Unavailable(AssistFight fight)
    {
        try
        {
            var wrath = pi.GetIpcSubscriber<bool>("WrathCombo.IPCReady");
            if (!wrath.HasFunction || !wrath.InvokeFunc()) return "Wrath IPC unavailable";
            if (!pi.GetIpcSubscriber<bool>("WrathCombo.IsCurrentJobAutoRotationReady").InvokeFunc())
                return "Wrath Auto-Rotation is not ready for this job";
            var ai = pi.GetIpcSubscriber<bool>("BossMod.AI.IsEnabled");
            if (!ai.HasFunction) return "BossMod AI IPC unavailable";
            if (!ai.InvokeFunc()) return "BossMod AI disabled";
            if (!pi.GetIpcSubscriber<uint, bool>("BossMod.HasModuleByDataId").InvokeFunc(fight.BaseId))
                return "BossMod has no module for this mark";
            // Upstream Configuration: exactly two tokens read the field. No value or save.
            var result = pi.GetIpcSubscriber<IReadOnlyList<string>, bool, List<string>>("BossMod.Configuration")
                .InvokeFunc(new[] { "AIConfig", "ForbidMovement" }, false);
            if (result is not [var value] || !bool.TryParse(value, out var forbidden))
                return "BossMod AI movement permission unknown";
            if (forbidden) return "BossMod AI movement disabled";
            return MissingGuard;
        }
        catch { return "Wrath or BossMod readiness IPC unavailable"; }
    }

    // Deliberately no fake IPC gate or unguarded SetAutoRotationState fallback.
    public IGuardedRotation? TryAcquire(AssistFight fight) => null;
}
