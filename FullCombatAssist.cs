namespace SRankSentinel;

// The owner token is valid only for one positively tagged actor, job and pull. A provider must
// enforce hostile target identity AND live combat at dispatch. Polling is for handoff/lifecycle,
// not a substitute for that dispatch guard.
internal readonly record struct AssistFight(ulong Actor, uint BaseId, uint Job, int Pull);
internal readonly record struct AssistFrame(AssistFight Fight, bool Enabled, bool Exact, bool Alive,
    bool InCombat, float Hp, float EngageHp, bool ConfirmedInitial, bool PlayerUsable)
{
    internal bool Eligible => Enabled && Exact && Alive && InCombat && ConfirmedInitial && PlayerUsable &&
        Fight.Actor != 0 && Fight.BaseId != 0 && Fight.Job != 0 && Fight.Pull > 0 && Hp > 0 && Hp <= EngageHp;
}

internal interface IGuardedRotation : IDisposable
{
    // Represents only Sentinel's overrides; disposal must never turn off the user's rotation.
    bool Healthy { get; }
}

internal interface ICombatAssistProvider
{
    string? Unavailable(AssistFight fight);
    // Atomic conditional grant, or no ownership. A rejected/partial acquisition cleans up itself.
    IGuardedRotation? TryAcquire(AssistFight fight);
}

internal sealed class FullCombatAssist
{
    private IGuardedRotation? rotation;
    private AssistFight ownedFight;
    private AssistFight? awaitingRaise;
    internal bool Active => rotation is not null;
    internal string Detail { get; private set; } = "Waiting for the initial confirmed tag";

    internal bool TryEnter(AssistFrame frame, ICombatAssistProvider provider)
    {
        if (!frame.Eligible || Active) return false;
        try
        {
            if (provider.Unavailable(frame.Fight) is { } why)
            {
                Detail = why;
                return false;
            }
            rotation = provider.TryAcquire(frame.Fight);
            if (rotation is null || !rotation.Healthy)
            {
                End("Wrath did not grant protected rotation");
                return false;
            }
            ownedFight = frame.Fight;
            Detail = "Wrath rotation + BossMod mechanic avoidance";
            return true;
        }
        catch { End("Combat assist IPC unavailable"); return false; }
    }

    internal bool Maintain(AssistFrame frame, ICombatAssistProvider provider)
    {
        if (!Active) return false;
        try
        {
            if (frame.Eligible && frame.Fight == ownedFight && rotation!.Healthy)
            {
                if (provider.Unavailable(frame.Fight) is not { } why) return true;
                End(why);
            }
            else End("Fight, target, player or lease no longer ready");
        }
        catch { End("Combat assist IPC unavailable"); }
        return false;
    }

    internal void SuspendForDeath()
    {
        var fight = Active ? ownedFight : (AssistFight?)null;
        End("Waiting for Raise");
        awaitingRaise = fight;
    }

    internal bool TryResume(AssistFrame frame, bool acceptedRaise, ICombatAssistProvider provider)
    {
        var fight = awaitingRaise;
        awaitingRaise = null;
        return acceptedRaise && fight == frame.Fight && TryEnter(frame, provider);
    }

    internal void End(string reason)
    {
        awaitingRaise = null;
        var old = rotation;
        rotation = null;
        ownedFight = default;
        Detail = reason;
        old?.Dispose();
    }
}
