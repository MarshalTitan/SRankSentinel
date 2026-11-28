namespace SRankSentinel;

internal static class MountPreferenceTests
{
    internal static IEnumerable<(string, Action)> Cases()
    {
        yield return ("explicit roulette skips all direct-mount calls", () => Requests(0, true, false, false, true, "roulette"));
        yield return ("default chocobo requests its stable ID", () => Requests(1, true, true, true, true, "read 1", "mount 1"));
        yield return ("another unlocked mount requests its stable ID", () => Requests(73, true, true, true, true, "read 73", "mount 73"));
        yield return ("locked or unresolved ID falls back without direct request", () => Requests(uint.MaxValue, true, false, false, true, $"read {uint.MaxValue}", "roulette"));
        yield return ("rejected specific mount falls back in the same request", () => Requests(73, true, true, false, true, "read 73", "mount 73", "roulette"));
        yield return ("unconfirmed preferred mount retries roulette only", () => Requests(73, false, true, true, true, "roulette"));
        yield return ("both actions failing leaves startup unconfirmed", () => Requests(73, true, true, false, false, "read 73", "mount 73", "roulette"));
        yield return ("mount choices refresh on unlock, data recovery and character change", RefreshChoices);
    }

    private static void Requests(uint id, bool first, bool owned, bool direct, bool roulette, params string[] expected)
    {
        var calls = new List<string>();
        var request = HuntProgressPolicy.SelectMount(id, first,
            mount => { calls.Add($"read {mount}"); return owned ? "Selected mount" : null; },
            mount => { calls.Add($"mount {mount}"); return direct; },
            () => { calls.Add("roulette"); return roulette; });
        var usedRoulette = expected[^1] == "roulette";
        if (!calls.SequenceEqual(expected) || request.Accepted != (usedRoulette ? roulette : direct) ||
            request.ActionName != (usedRoulette ? "Mount Roulette" : "Selected mount"))
            throw new InvalidOperationException($"Unexpected mount request: {string.Join(", ", calls)} / {request}");
    }

    private static void RefreshChoices()
    {
        var choices = new MountChoices();
        var now = DateTime.UtcNow;
        List<MountOption> owned = [new(2, "Zebra"), new(0, "Invalid"), new(2, "Duplicate"), new(3, "  ")];
        choices.Refresh(11, now, () => owned);
        owned.Add(new(4, "Amaro"));
        choices.Refresh(11, now.AddSeconds(3), () => owned);
        if (!choices.Options.Select(m => m.Id).SequenceEqual(new uint[] { 0, 4, 2 }))
            throw new InvalidOperationException("Roulette, ownership, unlock refresh or alphabetical order is wrong.");
        choices.Refresh(22, now.AddSeconds(3), () => [new(5, "Fenrir")]);
        if (!choices.Options.Select(m => m.Id).SequenceEqual(new uint[] { 0, 5 }))
            throw new InvalidOperationException("Another character's mounts remained visible.");
        choices.Refresh(22, now.AddSeconds(6), () => null);
        if (choices.DataAvailable || choices.Options.Length != 1 || choices.DisplaySelection(5, true).Id != 5)
            throw new InvalidOperationException("Unavailable data erased the saved preview.");
        choices.Refresh(22, now.AddSeconds(9), () => [new(5, "Fenrir")]);
        if (choices.DisplaySelection(5, true).Name != "Fenrir") throw new InvalidOperationException("Mount did not recover.");
    }
}
