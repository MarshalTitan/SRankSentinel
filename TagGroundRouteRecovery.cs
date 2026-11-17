using System.Numerics;

namespace SRankSentinel;

internal readonly record struct TagGroundRouteFrame(
    Vector3 Player, Vector3 Mark, float Hitboxes, float Clearance, bool Eligible);

internal enum TagGroundRouteStep { Direct, Retry, Searching, Following, Holding, InRange, Paused }

internal interface ITagGroundNavigation
{
    bool RouteRunning { get; }
    bool QueryRunning { get; }
    bool Ready { get; }
    bool Direct(Vector3 mark, float centerRange);
    Vector3? Floor(Vector3 sample);
    Task<List<Vector3>>? FindPath(Vector3 from, Vector3 to);
    bool Follow(List<Vector3> path);
    void Stop();
    void Report(string message);
}

// Only ground TagApproach uses this session. It has no authority over checkpoint completion,
// hunt lifetime, travel, parking or retreat. Each search is finite; exhaustion holds the live hunt.
internal sealed class TagGroundRouteRecovery
{
    internal const int DirectFailureLimit = 2;
    internal const double StallSeconds = 8;
    internal const double RetrySeconds = 10;
    private const float RepositionDistance = 6f;
    private readonly List<Vector3> failed = [];
    private readonly List<Vector3> stalledPositions = [];
    private readonly Queue<Vector3> candidates = new();
    private readonly List<(Vector3 Point, List<Vector3> Path, float Length)> valid = [];
    private Task<List<Vector3>>? query;
    private Vector3 queryPoint;
    private Vector3 origin;
    private Vector3 markOrigin;
    private Vector3 lastPlayer;
    private Vector3 progressPlayer;
    private float bestClearance;
    private DateTime started;
    private DateTime progressAt;
    private DateTime queryAt;
    private DateTime nextSearch;
    private bool directSubmitted;
    private bool searching;
    private bool sampled;
    private bool queryTimeoutReported;
    private (Vector3 Point, List<Vector3> Path, float Length)? active;

    internal int DirectFailures { get; private set; }
    internal bool AlternatesActive => DirectFailures >= DirectFailureLimit;
    internal IReadOnlyList<Vector3> FailedDestinations => failed;

    internal void Reset()
    {
        Suspend();
        failed.Clear();
        stalledPositions.Clear();
        sampled = false;
        DirectFailures = 0;
        nextSearch = DateTime.MinValue;
    }

    // Stop is performed by the caller on range arrival, target loss or state changes. Abandoned
    // queries can never start movement by themselves; observe any eventual fault after a reset.
    internal void Suspend()
    {
        if (query is not null)
            _ = query.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        query = null;
        candidates.Clear();
        valid.Clear();
        active = null;
        searching = directSubmitted = false;
    }

    internal TagGroundRouteStep Tick(DateTime now, TagGroundRouteFrame frame, ITagGroundNavigation nav)
    {
        if (!frame.Eligible || !Finite(frame.Player) || !Finite(frame.Mark))
        {
            if (directSubmitted || active is not null)
                nav.Stop();
            Suspend();
            return TagGroundRouteStep.Paused;
        }
        if (frame.Clearance <= 9f)
        {
            if (AlternatesActive && (active is not null || searching))
                nav.Report($"tag range reached: player={frame.Player}, mark={frame.Mark}, clearance={frame.Clearance:0.0}");
            nav.Stop();
            Suspend();
            return TagGroundRouteStep.InRange;
        }

        if (!AlternatesActive && DirectFailures > 0 &&
            stalledPositions.All(point => Vector3.Distance(frame.Player, point) >= RepositionDistance))
        {
            nav.Stop();
            directSubmitted = false;
            DirectFailures = DirectFailureLimit;
            nextSearch = now;
            nav.Report($"player repositioned after a failed direct route; replacing the stale approach from {frame.Player}");
        }
        if (!AlternatesActive)
            return TickDirect(now, frame, nav);

        // Manual movement and moving marks invalidate paths calculated from the old position.
        // Keep failed endpoints so re-evaluation cannot immediately choose the old wall again.
        if (((searching || (sampled && now < nextSearch)) && Vector3.Distance(frame.Player, origin) >= RepositionDistance) ||
            (active is { } route && (Vector3.Distance(frame.Player, lastPlayer) >= RepositionDistance ||
                DistanceToPath(frame.Player, origin, route.Path) > 4f ||
                Vector3.Distance(route.Point, frame.Mark) - frame.Hitboxes > 9f)) ||
            ((searching || active is not null) && Vector3.Distance(frame.Mark, markOrigin) > 3f))
        {
            nav.Stop();
            nav.Report($"reposition invalidated old tag route; recalculating from player={frame.Player}, mark={frame.Mark}");
            Suspend();
            nextSearch = now;
        }
        lastPlayer = frame.Player;

        if (active is { } selected)
        {
            ObserveProgress(now, frame);
            if ((now - progressAt).TotalSeconds >= StallSeconds ||
                (now - started).TotalSeconds >= 60 ||
                (!nav.RouteRunning && (now - started).TotalSeconds >= 2))
            {
                nav.Stop();
                RememberFailure(selected.Point);
                RememberStall(frame.Player);
                nav.Report($"alternate tag route stalled/stopped early: point={selected.Point}, pathLength={selected.Length:0.0}, player={frame.Player}, mark={frame.Mark}, clearance={frame.Clearance:0.0}");
                Suspend();
                nextSearch = now.AddSeconds(1);
                return TagGroundRouteStep.Retry;
            }
            return TagGroundRouteStep.Following;
        }

        if (!nav.Ready || now < nextSearch)
            return TagGroundRouteStep.Holding;
        if (!searching)
            BeginSearch(frame, nav);
        if (nav.RouteRunning)
            nav.Stop(); // A late result from the failed SimpleMove must not restart wall-side movement.

        if (query is not null)
        {
            if (!query.IsCompleted)
            {
                if ((now - queryAt).TotalSeconds >= 5 && !queryTimeoutReported)
                {
                    queryTimeoutReported = true;
                    nav.Report($"candidate query timed out at {queryPoint}; waiting for vnavmesh before another query");
                }
                return TagGroundRouteStep.Searching;
            }
            var path = query.IsCompletedSuccessfully && !queryTimeoutReported ? query.Result : null;
            _ = query.Exception;
            query = null;
            if (ValidateCandidatePath(queryPoint, frame, path, out var length, out var reason))
            {
                valid.Add((queryPoint, path!, length));
                nav.Report($"candidate ground path validated: point={queryPoint}, length={length:0.0}");
            }
            else
                nav.Report($"candidate rejected: point={queryPoint}, reason={reason}");
        }

        // A single in-flight Nav.Pathfind request avoids competing with vnavmesh's worker.
        if (nav.QueryRunning)
            return TagGroundRouteStep.Searching;
        if (candidates.TryDequeue(out queryPoint))
        {
            query = nav.FindPath(origin, queryPoint);
            queryAt = now;
            queryTimeoutReported = false;
            if (query is null)
                nav.Report($"candidate rejected: point={queryPoint}, reason=ground path query unavailable");
            return TagGroundRouteStep.Searching;
        }

        if (Vector3.Distance(frame.Player, origin) > 2f)
        {
            Suspend();
            nextSearch = now;
            nav.Report($"player moved during candidate validation; rebuilding tag paths from {frame.Player}");
            return TagGroundRouteStep.Searching;
        }
        foreach (var candidate in valid.OrderBy(route => route.Length))
        {
            if (!ValidateCandidatePath(candidate.Point, frame, candidate.Path, out _, out var reason))
            {
                nav.Report($"candidate rejected before movement: point={candidate.Point}, reason={reason}");
                continue;
            }
            if (!nav.Follow(new(candidate.Path)))
            {
                RememberFailure(candidate.Point);
                nav.Report($"candidate rejected: point={candidate.Point}, reason=validated route could not start");
                continue;
            }
            active = candidate;
            searching = false;
            candidates.Clear();
            valid.Clear();
            StartProgress(now, frame);
            nav.Report($"selected alternate tag route: point={candidate.Point}, length={candidate.Length:0.0}, player={frame.Player}, mark={frame.Mark}, clearance={frame.Clearance:0.0}");
            return TagGroundRouteStep.Following;
        }
        Suspend();
        nextSearch = now.AddSeconds(RetrySeconds);
        nav.Report($"no valid alternate tag route; retaining the live hunt and pending checkpoint; next search in {RetrySeconds:0}s, player={frame.Player}, mark={frame.Mark}");
        return TagGroundRouteStep.Holding;
    }

    private TagGroundRouteStep TickDirect(DateTime now, TagGroundRouteFrame frame, ITagGroundNavigation nav)
    {
        if (now < nextSearch)
            return TagGroundRouteStep.Retry;
        if (directSubmitted)
        {
            ObserveProgress(now, frame);
            if ((now - progressAt).TotalSeconds < StallSeconds &&
                (nav.RouteRunning || (now - started).TotalSeconds < 2))
                return TagGroundRouteStep.Direct;
            nav.Stop();
            RememberFailure(NearSidePoint(frame));
            RememberStall(frame.Player);
            DirectFailures++;
            directSubmitted = false;
            nextSearch = now.AddSeconds(1);
            nav.Report($"direct tag approach stalled/stopped early: failure={DirectFailures}, player={frame.Player}, mark/destination={frame.Mark}, clearance={frame.Clearance:0.0}");
            return TagGroundRouteStep.Retry;
        }
        if (nav.RouteRunning)
            return TagGroundRouteStep.Direct;
        // Preserve the normal direct MoveCloseTo(mark, ground, hitboxes + 8y) as the first route.
        if (nav.Direct(frame.Mark, frame.Hitboxes + 8f))
        {
            directSubmitted = true;
            StartProgress(now, frame);
            return TagGroundRouteStep.Direct;
        }
        DirectFailures++;
        RememberFailure(NearSidePoint(frame));
        RememberStall(frame.Player);
        nextSearch = now.AddSeconds(1);
        nav.Report($"direct tag approach rejected: failure={DirectFailures}, player={frame.Player}, destination={frame.Mark}");
        return TagGroundRouteStep.Retry;
    }

    private void BeginSearch(TagGroundRouteFrame frame, ITagGroundNavigation nav)
    {
        nav.Stop();
        origin = lastPlayer = frame.Player;
        markOrigin = frame.Mark;
        candidates.Clear();
        valid.Clear();
        searching = true;
        sampled = true;
        var misses = 0;
        foreach (var radius in new[] { frame.Hitboxes + 8f, frame.Hitboxes + 6f })
        for (var angle = 0; angle < 12; angle++)
        {
            var direction = Vector3.Normalize(NearSidePoint(frame) - frame.Mark);
            var radians = angle * MathF.PI / 6f;
            var offset = new Vector3(direction.X * MathF.Cos(radians) - direction.Z * MathF.Sin(radians),
                0f, direction.X * MathF.Sin(radians) + direction.Z * MathF.Cos(radians));
            var sample = frame.Mark + offset * radius;
            sample.Y = frame.Mark.Y + 4f;
            var projected = nav.Floor(sample);
            if (projected is not { } point || !Finite(point) ||
                MathF.Abs(point.Y - frame.Mark.Y) > 4f ||
                HorizontalDistance(point, sample) > 2f ||
                Vector3.Distance(point, frame.Mark) - frame.Hitboxes > 8.5f ||
                HorizontalDistance(point, frame.Mark) < frame.Hitboxes + 2f ||
                failed.Any(bad => HorizontalDistance(bad, point) < 2.5f) ||
                candidates.Any(existing => Vector3.Distance(existing, point) < 1f))
            {
                misses++;
                continue;
            }
            candidates.Enqueue(point);
        }
        nav.Report($"alternate tag candidate search: count={candidates.Count}, rejectedProjection/range/failed={misses}, player={origin}, mark={markOrigin}, clearance={frame.Clearance:0.0}");
    }

    private void StartProgress(DateTime now, TagGroundRouteFrame frame)
    {
        started = progressAt = now;
        progressPlayer = lastPlayer = frame.Player;
        bestClearance = frame.Clearance;
    }

    private void ObserveProgress(DateTime now, TagGroundRouteFrame frame)
    {
        if (Vector3.Distance(progressPlayer, frame.Player) < 1.5f && bestClearance - frame.Clearance < 1.5f)
            return;
        progressPlayer = frame.Player;
        bestClearance = MathF.Min(bestClearance, frame.Clearance);
        progressAt = now;
    }

    private void RememberFailure(Vector3 point)
    {
        if (!failed.Any(bad => HorizontalDistance(point, bad) < 1f))
            failed.Add(point);
    }

    private void RememberStall(Vector3 player)
    {
        if (!stalledPositions.Any(point => Vector3.Distance(point, player) < 2f))
            stalledPositions.Add(player);
    }

    private bool ValidateCandidatePath(Vector3 candidate, TagGroundRouteFrame frame, IReadOnlyList<Vector3>? path,
        out float length, out string reason)
    {
        if (!ValidatePath(origin, candidate, frame.Mark, frame.Hitboxes, path, out length, out reason))
            return false;
        // Allow escape when still at the stall. After repositioning, do not send the player
        // back through that known blocked spot just because another endpoint is selected.
        if (stalledPositions.Any(point => Vector3.Distance(origin, point) >= RepositionDistance &&
            DistanceToPath(point, origin, path!) < 2.5f))
        {
            reason = "route returns through a known stalled position after repositioning";
            return false;
        }
        return true;
    }

    private static Vector3 NearSidePoint(TagGroundRouteFrame frame)
    {
        var away = frame.Player - frame.Mark;
        away.Y = 0;
        var direction = away.LengthSquared() < 0.01f ? Vector3.UnitX : Vector3.Normalize(away);
        return frame.Mark + direction * (frame.Hitboxes + 8f);
    }

    internal static bool ValidatePath(Vector3 from, Vector3 candidate, Vector3 mark, float hitboxes,
        IReadOnlyList<Vector3>? path, out float length, out string reason)
    {
        length = 0;
        reason = "empty/incomplete ground path";
        if (path is null || path.Count == 0 || Vector3.Distance(from, path[0]) > 3f ||
            Vector3.Distance(path[^1], candidate) > 1f)
            return false;
        reason = "endpoint outside ranged-tag clearance or wrong elevation";
        if (Vector3.Distance(path[^1], mark) - hitboxes > 9f || MathF.Abs(path[^1].Y - mark.Y) > 4f)
            return false;
        var previous = from;
        foreach (var point in path)
        {
            reason = "non-finite, excessive vertical step or route through mark hitbox";
            if (!Finite(point) || MathF.Abs(point.Y - previous.Y) > 6f ||
                DistanceToSegment(mark, previous, point) < hitboxes + 1f)
                return false;
            length += Vector3.Distance(previous, point);
            previous = point;
        }
        reason = "excessive ground detour";
        if (!float.IsFinite(length) || length > MathF.Max(250f, Vector3.Distance(from, candidate) * 4f + 40f))
            return false;
        reason = string.Empty;
        return true;
    }

    private static bool Finite(Vector3 point) => float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
    private static float HorizontalDistance(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();
    private static float DistanceToSegment(Vector3 point, Vector3 from, Vector3 to)
    {
        var delta = new Vector2(to.X - from.X, to.Z - from.Z);
        var offset = new Vector2(point.X - from.X, point.Z - from.Z);
        var t = delta.LengthSquared() < 0.01f ? 0 : Math.Clamp(Vector2.Dot(offset, delta) / delta.LengthSquared(), 0, 1);
        return (offset - delta * t).Length();
    }
    private static float DistanceToPath(Vector3 player, Vector3 from, IReadOnlyList<Vector3> path)
    {
        var closest = float.MaxValue;
        foreach (var point in path)
        {
            closest = MathF.Min(closest, DistanceToSegment(player, from, point));
            from = point;
        }
        return closest;
    }
}
