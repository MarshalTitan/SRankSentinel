using System.Numerics;
using SRankSentinel;

internal static class TagGroundRouteTests
{
    internal static void NormalDirect()
    {
        var f = new Fixture();
        Check(f.Tick() == TagGroundRouteStep.Direct && f.Nav.DirectCalls == 1, "direct route must remain first");
        f.Frame = f.At(new(-35, 0, 0));
        Check(f.Tick(4) == TagGroundRouteStep.Direct && f.Nav.Requests.Count == 0, "ordinary progress must not sample alternatives");
        f.Frame = f.At(new(-11, 0, 0));
        Check(f.Tick(1) == TagGroundRouteStep.InRange && !f.Recovery.AlternatesActive, "direct arrival must stop normally");
    }

    internal static void RepeatedDirectFailure()
    {
        var f = new Fixture();
        f.Escalate();
        Check(f.Recovery.DirectFailures == 2 && f.Nav.DirectCalls == 2, "only two direct failures before fallback");
        Check(f.Tick(1) == TagGroundRouteStep.Searching && f.Nav.Requests.Count == 1, "stall must trigger ground-path search");

        var stopped = new Fixture();
        stopped.Tick();
        stopped.Nav.RouteRunning = false;
        stopped.Tick(2);
        stopped.Tick(1);
        stopped.Nav.RouteRunning = false;
        stopped.Tick(2);
        Check(stopped.Recovery.AlternatesActive, "early stops must count rather than resubmitting forever");
    }

    internal static void UnreachableCandidate()
    {
        var f = new Fixture();
        f.Nav.Path = (from, to) => f.Nav.Requests.Count == 1 ? [] : AroundWall(from, to);
        f.Escalate();
        f.SearchUntilFollow();
        Check(f.Nav.Requests.Count > 1 && f.Nav.Follows.Count == 1, "empty first path must not suppress another angle");
        var lengths = f.Nav.Requests.Select(r => AroundWall(r.From, r.To)).Where(path =>
            TagGroundRouteRecovery.ValidatePath(f.Frame.Player, path[^1], f.Frame.Mark, f.Frame.Hitboxes,
                path, out _, out _)).Select(Length).ToArray();
        Check(MathF.Abs(Length(f.Nav.Follows[0]) - lengths.Min()) < 0.1f, "prefer shortest validated practical route");
    }

    internal static void ObstacleAndCheckpoint()
    {
        var f = new Fixture();
        var cycle = new TagCheckpointCycle();
        Check(cycle.TryLatch(true, true, true, 70, 95, TagMilestone.At75 | TagMilestone.At50 | TagMilestone.At25), "checkpoint fixture");
        var checkpoint = cycle.Pending!.Value;
        f.Escalate();
        f.SearchUntilFollow();
        var path = f.Nav.Follows.Single();
        Check(path.Any(point => point.Z == 20), "the route must go around the synthetic wall opening");
        foreach (var endpoint in path.Skip(1))
        {
            var from = f.Frame.Player;
            var steps = (int)MathF.Ceiling(Vector3.Distance(from, endpoint) / 2f);
            for (var i = 1; i <= steps; i++)
            {
                f.Frame = f.At(Vector3.Lerp(from, endpoint, (float)i / steps));
                f.Tick(0.25);
                Check(cycle.Pending == checkpoint && !cycle.HasConfirmedTag, "navigation must never consume the checkpoint");
            }
        }
        Check(f.Nav.Follows.Count == 1, "legitimate progress around the wall must not be mistaken for manual displacement");
        Check(!f.Nav.RouteRunning && f.Frame.Clearance <= 9, "alternate arrival must stop in usable tag range");
        Check(cycle.ConfirmHandled(checkpoint, TagCheckpointCycle.Crossed(70, 95), 70, 95) && cycle.RetreatPending,
            "only handled confirmation completes the checkpoint and requests ordinary retreat");
        f.Recovery.Reset();
        Check(!f.Recovery.AlternatesActive && f.Recovery.FailedDestinations.Count == 0, "confirmed checkpoint/new pull/new hunt clears route memory");
    }

    internal static void FailedCandidateNotReused()
    {
        var f = new Fixture();
        f.Escalate();
        f.SearchUntilFollow();
        var failed = f.Nav.Follows[0][^1];
        Check(f.Tick(8) == TagGroundRouteStep.Retry, "alternate stall must stop and fail the endpoint");
        Check(f.Recovery.FailedDestinations.Contains(failed), "remember the failed endpoint for this checkpoint");
        var queriesBefore = f.Nav.Requests.Count;
        f.SearchUntilFollow();
        Check(f.Nav.Follows.Count == 2 && Vector3.Distance(f.Nav.Follows[1][^1], failed) >= 2.5f,
            "do not submit the same failed alternate again");
        Check(f.Nav.Requests.Skip(queriesBefore).All(request => Vector3.Distance(request.To, failed) >= 2.5f),
            "failed endpoint is skipped even during validation");
    }

    internal static void ManualReposition()
    {
        var direct = new Fixture();
        direct.Tick();
        direct.Tick(8);
        direct.Frame = direct.At(new(-10, 0, 15));
        Check(direct.Tick(0.25) == TagGroundRouteStep.Searching && direct.Nav.DirectCalls == 1,
            "after a real failure, manual progress must not restart the stale direct route");

        var f = new Fixture();
        f.Escalate();
        f.SearchUntilFollow();
        var before = f.Nav.Requests.Count;
        var reposition = new Vector3(-10, 0, 15);
        f.Nav.Path = (from, to) => f.Nav.Requests.Count == before + 1
            ? [from, new(-40, 0, 0), .. AroundWall(new(-40, 0, 0), to).Skip(1)]
            : AroundWall(from, to);
        f.Frame = f.At(reposition);
        Check(f.Tick(0.25) == TagGroundRouteStep.Searching && !f.Nav.RouteRunning,
            "manual move must invalidate and stop the old route");
        f.SearchUntilFollow();
        Check(f.Nav.Requests.Skip(before).All(r => r.From == reposition), "replan from actual current position");
        Check(!f.Nav.Follows.Last().Contains(new Vector3(-40, 0, 0)),
            "a new endpoint must not route the player back through the known wall-side stall");

        var async = new Fixture();
        var pending = new TaskCompletionSource<List<Vector3>>();
        async.Nav.Pending = pending.Task;
        async.Escalate();
        async.Tick(1);
        var original = async.Nav.Requests.Single();
        async.Frame = async.At(reposition);
        async.Nav.QueryRunning = true;
        async.Tick(0.25);
        pending.SetResult(AroundWall(original.From, original.To));
        async.Nav.Pending = null;
        async.Nav.QueryRunning = false;
        async.Tick(0.25);
        Check(async.Nav.Follows.Count == 0 && async.Nav.Requests.Last().From == reposition,
            "late completion of a query from the old position must never move the player back");
    }

    internal static void NoValidRouteHolds()
    {
        var f = new Fixture();
        f.Nav.Path = (_, _) => [];
        f.Escalate();
        for (var i = 0; i < 30; i++) f.Tick(0.25);
        var queries = f.Nav.Requests.Count;
        var logs = f.Nav.Messages.Count;
        for (var i = 0; i < 10; i++) f.Tick(0.25);
        Check(queries is > 0 and <= 24 && f.Nav.Requests.Count == queries && f.Nav.Messages.Count == logs,
            "finite search must hold without repeated movement/query/log spam");
        Check(f.Nav.DirectCalls == 2 && f.Nav.Follows.Count == 0 && !f.Nav.RouteRunning, "never return to the broken direct route");
        f.Tick(130);
        Check(f.Recovery.AlternatesActive, "elapsed time cannot discard positively living hunt navigation");
    }

    internal static void DeathAndContextPreempt()
    {
        var f = new Fixture();
        f.Escalate();
        f.SearchUntilFollow();
        var follows = f.Nav.Follows.Count;
        f.Frame = f.Frame with { Eligible = false };
        Check(f.Tick(0.25) == TagGroundRouteStep.Paused && !f.Nav.RouteRunning, "dead/lost/idle/wrong-context mark stops recovery");
        for (var i = 0; i < 4; i++) f.Tick(8);
        Check(f.Nav.Follows.Count == follows, "recovery cannot start another route after death");
    }

    internal static void PathValidation()
    {
        var start = new Vector3(-40, 0, 0);
        var goal = new Vector3(-10, 0, 0);
        bool Valid(List<Vector3> path) => TagGroundRouteRecovery.ValidatePath(start, goal, Vector3.Zero, 3, path, out _, out _);
        Check(!Valid([]), "empty route rejected");
        Check(!Valid([start, new(-25, 0, 0)]), "incomplete route rejected");
        Check(!Valid([start, new(float.NaN, 0, 0), goal]), "invalid geometry rejected");
        Check(!Valid([start, new(-20, 20, 0), goal]), "excessive vertical transition rejected");
        Check(!Valid([start, new(0, 0, 0), goal]), "route through mark body rejected");
        Check(!Valid([start, new(-20, 0, 500), goal]), "extreme detour rejected");
        Check(Valid(AroundWall(start, goal)), "practical obstacle detour accepted");

        var f = new Fixture();
        f.Nav.Projection = sample => new(sample.X, 20, sample.Z);
        f.Escalate();
        Check(f.Tick(1) == TagGroundRouteStep.Holding && f.Nav.Requests.Count == 0,
            "roof/high floor projections must not launch tag routes");
    }

    internal static void QueryTimeoutIsBounded()
    {
        var f = new Fixture();
        var never = new TaskCompletionSource<List<Vector3>>();
        f.Nav.Pending = never.Task;
        f.Escalate();
        f.Tick(1);
        f.Tick(6);
        var logs = f.Nav.Messages.Count;
        for (var i = 0; i < 12; i++) f.Tick(10);
        Check(f.Nav.Requests.Count == 1 && f.Nav.Messages.Count == logs && f.Nav.Follows.Count == 0,
            "a stuck path query cannot queue more work or move on a stale result");
        never.SetException(new InvalidOperationException("mesh changed"));
        f.Nav.Pending = null;
        f.Tick(0.25);
        Check(f.Nav.Follows.Count == 0 && f.Nav.Requests.Count == 2, "faulted query is observed and another candidate can be checked");
    }

    private static List<Vector3> AroundWall(Vector3 from, Vector3 to) =>
        [from, new(from.X, 0, 20), new(to.X, 0, 20), to];

    private static float Length(IReadOnlyList<Vector3> path) =>
        path.Zip(path.Skip(1), Vector3.Distance).Sum();

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private sealed class Fixture
    {
        internal readonly TagGroundRouteRecovery Recovery = new();
        internal readonly FakeNavigation Nav = new();
        internal TagGroundRouteFrame Frame = new(new(-40, 0, 0), Vector3.Zero, 3, 37, true);
        private DateTime now = new(2026, 11, 17, 15, 0, 0, DateTimeKind.Utc);
        internal TagGroundRouteFrame At(Vector3 position) => Frame with
            { Player = position, Clearance = new Vector2(position.X, position.Z).Length() - Frame.Hitboxes };
        internal TagGroundRouteStep Tick(double seconds = 0)
        {
            now = now.AddSeconds(seconds);
            return Recovery.Tick(now, Frame, Nav);
        }
        internal void Escalate()
        {
            Tick();
            Tick(8);
            Tick(1);
            Tick(8);
            Check(Recovery.AlternatesActive, "direct-route failure fixture");
        }
        internal void SearchUntilFollow()
        {
            for (var i = 0; i < 110; i++)
                if (Tick(0.25) == TagGroundRouteStep.Following) return;
            throw new InvalidOperationException("alternate route did not start");
        }
    }

    private sealed class FakeNavigation : ITagGroundNavigation
    {
        public bool RouteRunning { get; set; }
        public bool QueryRunning { get; set; }
        public bool Ready => true;
        internal int DirectCalls;
        internal readonly List<(Vector3 From, Vector3 To)> Requests = [];
        internal readonly List<List<Vector3>> Follows = [];
        internal readonly List<string> Messages = [];
        internal Func<Vector3, Vector3?> Projection = sample => new(sample.X, 0, sample.Z);
        internal Func<Vector3, Vector3, List<Vector3>> Path = AroundWall;
        internal Task<List<Vector3>>? Pending;
        public bool Direct(Vector3 mark, float range)
        {
            Check(mark == Vector3.Zero && range == 11, "normal direct call changed");
            DirectCalls++;
            return RouteRunning = true;
        }
        public Vector3? Floor(Vector3 sample) => Projection(sample);
        public Task<List<Vector3>> FindPath(Vector3 from, Vector3 to)
        {
            Requests.Add((from, to));
            return Pending ?? Task.FromResult(Path(from, to));
        }
        public bool Follow(List<Vector3> path)
        {
            Follows.Add(path);
            return RouteRunning = true;
        }
        public void Stop() => RouteRunning = false;
        public void Report(string message) => Messages.Add(message);
    }
}
