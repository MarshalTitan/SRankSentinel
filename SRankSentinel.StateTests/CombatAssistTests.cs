using SRankSentinel;

internal static class CombatAssistTests
{
    private static readonly AssistFrame Valid = new(new(101, 202, 21, 1), true, true, true, true, 95, 95, true, true);
    private sealed class Lease : IGuardedRotation
    {
        internal int Releases;
        public bool Healthy { get; set; } = true;
        public void Dispose() { Releases++; }
    }
    private sealed class Provider : ICombatAssistProvider
    {
        internal string? Missing;
        internal bool Throw;
        internal int Acquires;
        internal Lease Last = new();
        public string? Unavailable(AssistFight fight) =>
            Throw ? throw new InvalidOperationException("IPC reloaded") : Missing;
        public IGuardedRotation TryAcquire(AssistFight fight) { Acquires++; return Last = new(); }
    }
    private static void Check(bool ok) { if (!ok) throw new Exception("Combat assist regression"); }
    internal static IEnumerable<(string, Action)> Cases()
    {
        foreach (var (name, frame) in new (string, AssistFrame)[] {
            ("off", Valid with { Enabled = false }), ("precursor or wrong identity", Valid with { Exact = false }),
            ("boss dead", Valid with { Alive = false }), ("idle boss", Valid with { InCombat = false }),
            ("above engage HP", Valid with { Hp = 96 }), ("initial tag only submitted", Valid with { ConfirmedInitial = false }),
            ("Raise not settled", Valid with { PlayerUsable = false }) })
            yield return ("assist refuses " + name, () => {
                var p = new Provider(); Check(!new FullCombatAssist().TryEnter(frame,p) && p.Acquires == 0);
            });
        foreach (var reason in new[] { "Wrath missing", "job not ready", "BossMod missing", "AI disabled",
                     "movement forbidden", "module missing", "hostile target guard unavailable" })
            yield return ("safe tag fallback: " + reason, () => {
                var p = new Provider { Missing = reason }; var a = new FullCombatAssist();
                Check(!a.TryEnter(Valid,p) && !a.Active && p.Acquires == 0 && a.Detail == reason);
            });
        yield return ("one protected grant after confirmed initial; no checkpoint requests while healthy", () => {
            var p = new Provider(); var a = new FullCombatAssist(); Check(a.TryEnter(Valid,p));
            foreach (int hp in new[] {75,50,25}) Check(a.Maintain(Valid with { Hp = hp },p));
            Check(p.Acquires == 1 && !a.TryEnter(Valid,p));
            a.End("kill"); a.End("dispose"); Check(!a.Active && p.Last.Releases == 1);
        });
        yield return ("death releases ownership; accepted Raise revalidates exact pull and acquires once", () => {
            var p = new Provider(); var a = new FullCombatAssist(); a.TryEnter(Valid,p);
            a.SuspendForDeath(); Check(!a.Active && p.Last.Releases == 1);
            Check(a.TryResume(Valid,true,p)); Check(!a.TryResume(Valid,true,p) && p.Acquires == 2);
        });
        foreach (var kind in new[]{ "no accepted Raise", "wrong actor", "new pull", "dependency gone" })
            yield return ("Raise fallback: " + kind, () => {
                var p = new Provider(); var a = new FullCombatAssist(); a.TryEnter(Valid,p); a.SuspendForDeath();
                if (kind == "dependency gone") p.Missing = kind;
                var f = Valid with { Fight = kind == "new pull" ? Valid.Fight with { Pull = 2 } :
                            kind == "wrong actor" ? Valid.Fight with { Actor = 102 } : Valid.Fight };
                Check(!a.TryResume(f,kind != "no accepted Raise",p) && !a.Active && p.Acquires == 1);
                var checkpoints = new TagCheckpointCycle(); Check(checkpoints.ObserveRevival(DateTime.UtcNow,true,true));
                Check(checkpoints.RaisePending);
            });
        foreach (var kind in new[]{ "boss reset", "dead", "AI off", "cancelled", "BossMod reload", "job changed" })
            yield return ("release and retreat: " + kind, () => {
                var a = new FullCombatAssist(); var p = new Provider(); Check(a.TryEnter(Valid,p));
                if (kind == "AI off") p.Missing = kind; if (kind == "cancelled") p.Last.Healthy = false;
                if (kind == "BossMod reload") p.Throw = true;
                var f = Valid with { InCombat = kind != "boss reset", Alive = kind != "dead",
                    Fight = kind == "job changed" ? Valid.Fight with { Job = 31 } : Valid.Fight };
                Check(!a.Maintain(f,p) && p.Last.Releases == 1 && !a.Active);
            });
    }
}
