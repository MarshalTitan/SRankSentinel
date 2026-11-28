namespace SRankSentinel;

internal readonly record struct MountOption(uint Id, string Name)
{
    internal const uint CompanyChocoboMountId = 1;
    internal static readonly MountOption Roulette = new(0, "Mount Roulette");
}

internal readonly record struct MountRequest(string ActionName, bool Accepted, string Reason);

// The selector refreshes only while it is visible. Runtime summoning always checks live ownership.
internal sealed class MountChoices
{
    private ulong owner;
    private DateTime refreshAfter;
    internal MountOption[] Options { get; private set; } = [MountOption.Roulette];
    internal bool DataAvailable { get; private set; }

    internal void Refresh(ulong character, DateTime now, Func<IEnumerable<MountOption>?> readUnlocked)
    {
        if (character == owner && now < refreshAfter) return;
        owner = character;
        refreshAfter = now.AddSeconds(2);
        var mounts = character == 0 ? null : readUnlocked();
        DataAvailable = mounts is not null;
        Options = mounts is null ? [MountOption.Roulette] :
            [MountOption.Roulette, .. mounts.Where(m => m.Id != 0 && !string.IsNullOrWhiteSpace(m.Name))
                .DistinctBy(m => m.Id).OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(m => m.Id)];
    }

    internal MountOption DisplaySelection(uint preference, bool explicitlySaved)
    {
        foreach (var mount in Options)
            if (mount.Id == preference) return mount;
        if (!DataAvailable) return new(preference, $"Mount #{preference} (data unavailable)");
        return explicitlySaved ? new(preference, $"Mount #{preference} unavailable · using Mount Roulette") : MountOption.Roulette;
    }
}
