namespace SRankSentinel;

[Flags]
internal enum TagMilestone { None = 0, Initial = 1, At75 = 2, At50 = 4, At25 = 8, Raise = 16 }

internal readonly record struct TagCheckpoint(TagMilestone Kind, float Hp)
{
    internal string Label => Kind == TagMilestone.Raise ? "Raise retag" : $"{Hp:0.#}% checkpoint";
}

/// <summary>One outstanding action per pull. Only handled actions advance milestones or credit.</summary>
internal sealed class TagCheckpointCycle
{
    internal TagMilestone Consumed { get; private set; }
    internal bool HasConfirmedTag { get; private set; }
    internal bool RetreatPending { get; private set; }
    internal bool RaisePending { get; private set; }
    internal TagCheckpoint? Pending { get; private set; }
    private DateTime lastRevival;

    internal bool ObserveRevival(DateTime acceptedAtUtc, bool canAct, bool enabled)
    {
        if (acceptedAtUtc == DateTime.MinValue || acceptedAtUtc <= lastRevival || !canAct)
            return false;
        lastRevival = acceptedAtUtc;
        if (!enabled)
            return false;
        RaisePending = true;
        Pending = null;
        RetreatPending = false;
        return true;
    }

    internal bool TryLatch(bool exact, bool alive, bool inCombat, float hp, float initial,
        TagMilestone enabled)
    {
        if (Pending is not null || RetreatPending || !Eligible(exact, alive, inCombat, hp, initial))
            return false;
        var due = Crossed(hp, initial) & (enabled | TagMilestone.Initial) & ~Consumed;
        Pending = RaisePending ? new(TagMilestone.Raise, initial) :
            (due & TagMilestone.Initial) != 0 ? new(TagMilestone.Initial, initial) :
            (due & TagMilestone.At75) != 0 ? new(TagMilestone.At75, 75f) :
            (due & TagMilestone.At50) != 0 ? new(TagMilestone.At50, 50f) :
            (due & TagMilestone.At25) != 0 ? new(TagMilestone.At25, 25f) : null;
        return Pending is not null;
    }

    internal bool CanDispatch(bool exact, bool alive, bool inCombat, float hp, float initial) =>
        Pending is { } tag && Eligible(exact, alive, inCombat, hp, Math.Min(tag.Hp, initial));

    private static bool Eligible(bool exact, bool alive, bool inCombat, float hp, float threshold) =>
        exact && alive && inCombat && hp > 0f && hp <= threshold;

    internal static TagMilestone Crossed(float hp, float initial)
    {
        if (!(hp > 0f && hp <= initial))
            return TagMilestone.None;
        return TagMilestone.Initial |
               (hp <= 75f ? TagMilestone.At75 : TagMilestone.None) |
               (hp <= 50f ? TagMilestone.At50 : TagMilestone.None) |
               (hp <= 25f ? TagMilestone.At25 : TagMilestone.None);
    }

    internal bool ConfirmHandled(TagCheckpoint checkpoint, TagMilestone crossedOnSubmit, float hp, float initial)
    {
        if (Pending != checkpoint)
            return false;
        Consumed |= (checkpoint.Kind & ~TagMilestone.Raise) | crossedOnSubmit | Crossed(hp, initial);
        if (checkpoint.Kind == TagMilestone.Raise)
            RaisePending = false;
        HasConfirmedTag = true;
        RetreatPending = true;
        Pending = null;
        return true;
    }

    internal void CompleteRetreat() => RetreatPending = false;

    internal void ResetPull()
    {
        Consumed = TagMilestone.None;
        HasConfirmedTag = false;
        RetreatPending = false;
        RaisePending = false;
        Pending = null;
    }
}
