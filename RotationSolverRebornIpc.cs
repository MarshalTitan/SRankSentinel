using Dalamud.Plugin;

namespace SRankSentinel;

public enum CombatRotationProvider { WrathCombo, RotationSolverReborn }

// RSR 7.5.8.11: the mode setters have no result or ownership token. AutorotationActive
// merges several modes, and says nothing about an executable rotation for the current job.
// A read-only adapter can guard a Wrath handoff; it must never change the user's RSR mode.
internal sealed class RotationSolverRebornIpc(IDalamudPluginInterface pi) : ICombatAssistProvider
{
    internal const string Unsupported = "RSR cannot verify job readiness or grant exclusive rotation control; using safe tagging";
    private enum Activity { Missing, Inactive, Active, Unknown }

    private Activity Read()
    {
        try
        {
            var gate = pi.GetIpcSubscriber<bool>("RotationSolverReborn.AutorotationActive");
            if (gate.HasFunction) return gate.InvokeFunc() ? Activity.Active : Activity.Inactive;
            return pi.InstalledPlugins.Any(p => p.InternalName == "RotationSolver" && p.IsLoaded)
                ? Activity.Unknown : Activity.Missing;
        }
        catch { return Activity.Unknown; }
    }

    internal string? Conflict() => Read() switch
    {
        Activity.Active => "Rotation Solver Reborn is active; turn its rotation off to use Wrath",
        Activity.Unknown => "Rotation Solver Reborn activity cannot be verified",
        _ => null,
    };

    public string Unavailable(AssistFight fight) => Read() switch
    {
        Activity.Missing => "Rotation Solver Reborn not enabled",
        Activity.Unknown => "Rotation Solver Reborn status IPC unavailable",
        _ => Unsupported,
    };

    public IGuardedRotation? TryAcquire(AssistFight fight) => null;
}

internal sealed class SelectedCombatRotation(IDalamudPluginInterface pi, Action<string>? report = null)
    : IDisposable
{
    internal readonly CombatAssistIpc Wrath = new(pi, report);
    internal readonly RotationSolverRebornIpc Reborn = new(pi);

    internal ICombatAssistProvider? For(CombatRotationProvider provider) => provider switch
    {
        CombatRotationProvider.WrathCombo => Wrath,
        CombatRotationProvider.RotationSolverReborn => Reborn,
        _ => null,
    };
    public void Dispose() => Wrath.Dispose();
}
