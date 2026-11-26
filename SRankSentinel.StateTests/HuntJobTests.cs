using SRankSentinel;

internal static class HuntJobTests
{
    internal static (string, Action)[] Cases() =>
    [
        ("Fisher at startup: no selection cannot trigger an arbitrary equip or hunt travel", NoSelection),
        ("Fisher during travel: stop until chosen job AND gearset are observed", TravelChange),
        ("Loading job unknown cannot start a hunt; Fisher after zoning still needs recovery", Arrival),
        ("Blocked combat, fishing, duty, loading and other activities consume no equip attempts", Restrictions),
        ("Stale, replaced or missing gearset cannot be equipped", Invalid),
        ("Failed restore stays paused after bounded requests until retry or verified manual correction", Failure),
        ("Ordinary supported jobs proceed without choosing or equipping a set", Normal),
        ("A different character cannot inherit a pending restore", CharacterChange),
    ];
    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly HuntGearset War = new(4, 21, "Tank");
    private static JobStep At(HuntJobReadiness g, int second, uint job, HuntGearset? set, int slot = -1,
        bool valid = true, bool can = true, ulong owner = 1) =>
        g.Check(Start.AddSeconds(second), owner, job, job is 21 or 41, set, slot, valid, can);
    private static void Eq<T>(T a, T b) where T : notnull
    { if (!a.Equals(b)) throw new Exception($"Expected {b}; got {a}"); }
    private static void NoSelection()
    {
        var g = new HuntJobReadiness();
        Eq(At(g, 0, 18, null), JobStep.Select); Eq(At(g, 300, 18, null), JobStep.Select);
        Eq(g.Attempts, 0); Eq(At(g, 301, 21, null), JobStep.Ready);
    }
    private static void TravelChange()
    {
        var g = new HuntJobReadiness(); Eq(At(g, 0, 21, War), JobStep.Ready);
        Eq(At(g, 1, 18, War), JobStep.Equip);
        Eq(At(g, 2, 18, War, War.Slot), JobStep.Confirm);
        Eq(At(g, 3, 21, War, 9), JobStep.Confirm);
        Eq(At(g, 4, 21, War, War.Slot), JobStep.Ready);
        Eq(At(g, 5, 18, War, can:false), JobStep.Blocked);
    }
    private static void Arrival()
    {
        var g = new HuntJobReadiness();
        Eq(At(g, 0, 0, War, owner:0), JobStep.Loading);
        Eq(At(g, 30, 18, War, can:false), JobStep.Blocked); Eq(g.Attempts, 0);
        Eq(At(g, 31, 18, War), JobStep.Equip); Eq(At(g, 32, 21, War, War.Slot), JobStep.Ready);
    }
    private static void Restrictions()
    {
        var g = new HuntJobReadiness();
        for (int i=0; i<5; i++)
        {
            var why = HuntJobReadiness.Restriction(i==0,i==1,i==2,i==3,i!=4);
            Eq(At(g, i*60, 18, War, can:why==null), JobStep.Blocked);
        }
        Eq(g.Attempts, 0); Eq(At(g, 500, 18, War, can:HuntJobReadiness.Restriction(false,false,false,false,true)==null), JobStep.Equip);
    }
    private static void Invalid()
    {
        var g = new HuntJobReadiness();
        Eq(At(g, 0, 18, War, valid:false), JobStep.Invalid); Eq(g.Attempts, 0);
    }
    private static void Failure()
    {
        var g = new HuntJobReadiness();
        Eq(At(g, 0, 18, War), JobStep.Equip); Eq(At(g, 1, 18, War), JobStep.Confirm);
        Eq(At(g, 4, 18, War), JobStep.Equip); Eq(At(g, 8, 18, War), JobStep.Failed);
        Eq(At(g, 500, 18, War), JobStep.Failed); Eq(g.Attempts, 2);
        Eq(At(g, 501, 21, War, War.Slot), JobStep.Ready); Eq(g.Attempts, 0);
        At(g, 502, 18, War); At(g, 506, 18, War); g.Retry(); Eq(At(g, 507, 18, War), JobStep.Equip);
    }
    private static void Normal()
    {
        var g = new HuntJobReadiness();
        Eq(At(g, 0, 41, null, can:false), JobStep.Ready); Eq(At(g, 1, 21, War, can:false), JobStep.Ready);
        Eq(g.Attempts, 0);
    }
    private static void CharacterChange()
    {
        var g = new HuntJobReadiness(); At(g, 0, 18, War); At(g, 4, 18, War);
        Eq(At(g, 5, 18, null, owner:2), JobStep.Select); Eq(g.Attempts, 0);
    }
}
