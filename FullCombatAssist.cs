namespace SRankSentinel;

// One observed actor/job/pull. Wrath may already have submitted an action when a reset is observed.
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
    // A rejected or partial acquisition must clean up its own temporary overrides.
    IGuardedRotation? TryAcquire(AssistFight fight);
}

internal sealed class FullCombatAssist
{
    private IGuardedRotation? rotation;
    private AssistFight ownedFight;
    private AssistFight? awaitingRaise;
    private float lowestHp = float.PositiveInfinity;
    internal bool ResetSuspected { get; private set; }
    internal bool AwaitingRaise => awaitingRaise is not null;
    internal static bool IsSolo(int partySize, bool alliance, bool crossWorldParty) =>
        partySize is 0 or 1 && !alliance && !crossWorldParty;
    internal bool Active => rotation is not null;
    internal string Detail { get; private set; } = "Waiting for the initial confirmed tag";

    internal bool TryEnter(AssistFrame frame, ICombatAssistProvider provider)
    {
        if (!frame.Eligible || Active || ResetSuspected) return false;
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
            lowestHp = Math.Min(lowestHp, frame.Hp);
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
            if (frame.Exact && frame.Alive && frame.Fight == ownedFight && !ObserveBoss(frame.InCombat, frame.Hp))
                return false;
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
        return acceptedRaise && fight == frame.Fight && ObserveBoss(frame.InCombat, frame.Hp) && TryEnter(frame, provider);
    }

    // This warning only disarms assist. The existing stable full-HP reset detector still owns
    // the tag cycle. Keep the low-water mark across Raise so several smaller heals add up.
    internal bool ObserveBoss(bool inCombat, float hp)
    {
        if (ResetSuspected) return false;
        if (!inCombat || !float.IsFinite(hp) || hp - lowestHp >= 5f)
        {
            End(inCombat ? $"Boss HP rose from {lowestHp:0.0}% to {hp:0.0}%; safe tagging for this hunt"
                         : "Boss left combat; safe tagging for this hunt");
            ResetSuspected = true;
            return false;
        }
        lowestHp = Math.Min(lowestHp, hp);
        return true;
    }

    internal void NewHunt()
    {
        ResetSuspected = false;
        End("Waiting for the initial confirmed tag");
        lowestHp = float.PositiveInfinity;
    }

    internal void End(string reason)
    {
        awaitingRaise = null;
        var old = rotation;
        rotation = null;
        ownedFight = default;
        if (!ResetSuspected) Detail = reason;
        old?.Dispose();
    }
}
