namespace SRankSentinel;

public sealed record HuntGearset(int Slot, uint Job, string Name);
internal enum JobStep { Ready, Loading, Select, Invalid, Blocked, Equip, Confirm, Failed }

// A request is never proof of a job change. Only observed live job/slot can complete a restore.
internal sealed class HuntJobReadiness
{
    internal const int Limit = 2;
    internal int Attempts { get; private set; }
    private DateTime nextAttempt;
    private ulong owner;
    private HuntGearset? choice;

    internal static string? Restriction(bool loading, bool combat, bool fishing, bool duty, bool idle)
        => loading ? "loading or occupied" : combat ? "in combat" : fishing ? "fishing or gathering"
         : duty ? "in a duty" : !idle ? "another activity is in progress" : null;

    internal void Retry() { Attempts = 0; nextAttempt = default; }

    internal JobStep Check(DateTime now, ulong character, uint job, bool supported, HuntGearset? selected,
        int equippedSlot, bool validSelection, bool mayEquip)
    {
        if (character == 0 || job == 0) return JobStep.Loading;
        if (owner != character || choice != selected)
        {
            owner = character;
            choice = selected;
            Retry();
        }
        if (supported && (selected == null || (job == selected.Job &&
            (Attempts == 0 || (validSelection && equippedSlot == selected.Slot)))))
        {
            Retry();
            return JobStep.Ready;
        }
        if (selected == null) return JobStep.Select;
        if (now < nextAttempt) return JobStep.Confirm;
        if (Attempts >= Limit) return JobStep.Failed;
        if (!mayEquip) return JobStep.Blocked;
        if (!validSelection) return JobStep.Invalid;
        Attempts++;
        nextAttempt = now.AddSeconds(4);
        return JobStep.Equip;
    }
}
