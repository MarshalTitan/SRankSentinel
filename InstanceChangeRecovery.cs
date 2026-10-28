using System.Numerics;

namespace SRankSentinel;

internal enum InstanceChangeAction
{
    ResumeHunt, WaitForTransition, Hold, Retry, WaitForMenu, OpenInstanceMenu,
    SelectInstance, WaitForTarget, Dismount, WaitForMesh, Approach, WaitForApproach, Interact,
}

internal readonly record struct InstanceChangeObservation(
    bool CorrectInstance, bool Busy, bool MainMenu, bool InstanceMenu, bool AnyMenu,
    bool TargetAvailable, bool InRange, bool Mounted, bool MeshReady);

/// <summary>Bounded local interaction recovery; menu/zoning/arrival always preempt movement.</summary>
internal sealed class InstanceChangeRecovery
{
    private enum Phase { Resolve, Approach, Interaction, InstanceMenu, Transition }
    private Phase phase;
    private DateTime startedUtc;
    private DateTime phaseDeadlineUtc;
    private DateTime missingSinceUtc;
    public int Failures { get; private set; }
    public const int MaximumFailures = 3;
    public string LastFailure { get; private set; } = string.Empty;
    public float InteractionRange => Failures == 0 ? 8f : 6.5f;

    public void Reset()
    {
        phase = Phase.Resolve;
        startedUtc = phaseDeadlineUtc = missingSinceUtc = DateTime.MinValue;
        Failures = 0;
        LastFailure = string.Empty;
    }

    public void Submitted(InstanceChangeAction action, DateTime now)
    {
        phase = action switch
        {
            InstanceChangeAction.Approach => Phase.Approach,
            InstanceChangeAction.Interact => Phase.Interaction,
            InstanceChangeAction.OpenInstanceMenu => Phase.InstanceMenu,
            InstanceChangeAction.SelectInstance => Phase.Transition,
            _ => Phase.Resolve,
        };
        phaseDeadlineUtc = now.AddSeconds(action == InstanceChangeAction.Approach ? 15 :
            action == InstanceChangeAction.SelectInstance ? 25 : 4);
    }

    public InstanceChangeAction Failed(string reason)
    {
        Failures++;
        LastFailure = reason;
        phase = Phase.Resolve;
        missingSinceUtc = DateTime.MinValue;
        return Failures >= MaximumFailures ? InstanceChangeAction.Hold : InstanceChangeAction.Retry;
    }

    public void Block(string reason)
    {
        Failures = MaximumFailures;
        LastFailure = reason;
    }

    public static bool IsLocalPath(IReadOnlyList<Vector3> path, Vector3 origin, Vector3 player, Vector3 target)
    {
        if (path.Count == 0 || Vector3.Distance(player, path[0]) > 3f ||
            Vector3.Distance(path[^1], target) > 8f)
            return false;
        var length = 0f;
        var previous = player;
        foreach (var point in path)
        {
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z) ||
                Vector3.Distance(point, origin) > 20f)
                return false;
            length += Vector3.Distance(previous, point);
            previous = point;
        }
        return length <= 25f;
    }

    public InstanceChangeAction Decide(DateTime now, InstanceChangeObservation input)
    {
        if (input.CorrectInstance)
            return InstanceChangeAction.ResumeHunt;
        if (startedUtc == DateTime.MinValue)
            startedUtc = now;
        if (input.Busy)
            return InstanceChangeAction.WaitForTransition;
        if (Failures >= MaximumFailures || (now - startedUtc).TotalSeconds >= 90)
            return InstanceChangeAction.Hold;

        if (input.InstanceMenu)
        {
            if (phase != Phase.Transition)
                return InstanceChangeAction.SelectInstance;
            return now < phaseDeadlineUtc ? InstanceChangeAction.WaitForMenu :
                Failed("requested instance did not begin changing after selection");
        }
        if (input.MainMenu)
        {
            if (phase != Phase.InstanceMenu)
                return InstanceChangeAction.OpenInstanceMenu;
            return now < phaseDeadlineUtc ? InstanceChangeAction.WaitForMenu :
                Failed("instance-selection menu did not open");
        }
        if (input.AnyMenu)
            return InstanceChangeAction.WaitForMenu;

        if (phase is Phase.Interaction or Phase.InstanceMenu or Phase.Transition)
            return now < phaseDeadlineUtc ? InstanceChangeAction.WaitForMenu :
                Failed("interaction/selection was submitted but no successful menu or arrival was observed");

        if (!input.TargetAvailable)
        {
            // A disappeared/reloaded object invalidates any pending local path.
            phase = Phase.Resolve;
            if (missingSinceUtc == DateTime.MinValue)
                missingSinceUtc = now;
            return (now - missingSinceUtc).TotalSeconds < 10 ? InstanceChangeAction.WaitForTarget :
                Failed("local Aetheryte object remained unavailable");
        }
        missingSinceUtc = DateTime.MinValue;
        if (input.Mounted)
            return InstanceChangeAction.Dismount;
        if (input.InRange)
        {
            phase = Phase.Resolve;
            return InstanceChangeAction.Interact;
        }
        if (phase == Phase.Approach)
            return now < phaseDeadlineUtc ? InstanceChangeAction.WaitForApproach :
                Failed("local Aetheryte approach did not reach interaction range");
        return input.MeshReady ? InstanceChangeAction.Approach : InstanceChangeAction.WaitForMesh;
    }
}

