using System.Numerics;
using SentinelHunts.Core;

var tests = new (string Name, Action Run)[]
{
    ("catalog has 47 unique normal S ranks", CatalogHasExpectedCoverage),
    ("default expansions select 18 ranks", DefaultFilterHasEighteenRanks),
    ("multiple sources deduplicate and merge", ReportsDeduplicate),
    ("newer death removes queued hunt", NewerDeathRemovesQueueEntry),
    ("older death does not remove newer spawn", OlderDeathDoesNotRemoveNewSpawn),
    ("death tombstone blocks stale reports but permits a later spawn", DeathTombstoneIsChronological),
    ("queue remains FIFO", QueueRemainsFifo),
    ("disabled expansion is pruned before dequeue", DisabledExpansionIsPruned),
    ("parking ring preserves hitbox clearance", ParkingRingIsSafe),
    ("unsafe protected path is rejected", UnsafePathIsRejected),
    ("retreat path may leave the protected area outward", RetreatPathEscapesSafely),
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL  {test.Name}: {exception.Message}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed");
return failures == 0 ? 0 : 1;

static void CatalogHasExpectedCoverage()
{
    Equal(47, HuntCatalog.All.Count);
    Equal(47, HuntCatalog.All.Select(rank => rank.DataId).Distinct().Count());
    Equal(47, HuntCatalog.All.Select(rank => rank.TerritoryId).Distinct().Count());
}

static void DefaultFilterHasEighteenRanks()
{
    var enabled = new[] { Expansion.Shadowbringers, Expansion.Endwalker, Expansion.Dawntrail };
    Equal(18, HuntCatalog.All.Count(rank => enabled.Contains(rank.Expansion)));
}

static void ReportsDeduplicate()
{
    var queue = new HuntQueue();
    var first = Report(HuntSource.Sonar, DateTimeOffset.UtcNow, 23.1f, 22.2f);
    var second = first with { Source = HuntSource.HuntAlerts, ObservedAt = first.ObservedAt.AddSeconds(2), MapX = 23.2f };
    Equal(QueueMutation.Added, queue.Apply(first));
    Equal(QueueMutation.Merged, queue.Apply(second));
    Equal(1, queue.Entries.Count);
    Equal(2, queue.Entries[0].Sources.Count);
    Equal(23.2f, queue.Entries[0].MapX);
}

static void NewerDeathRemovesQueueEntry()
{
    var queue = new HuntQueue();
    var report = Report(HuntSource.Sonar, DateTimeOffset.UtcNow, 23.1f, 22.2f);
    queue.Apply(report);
    Equal(QueueMutation.RemovedByKill,
        queue.Apply(report with { Kind = HuntEventKind.Killed, ObservedAt = report.ObservedAt.AddSeconds(1) }));
    Equal(0, queue.Entries.Count);
}

static void OlderDeathDoesNotRemoveNewSpawn()
{
    var queue = new HuntQueue();
    var report = Report(HuntSource.Sonar, DateTimeOffset.UtcNow, 23.1f, 22.2f);
    queue.Apply(report);
    Equal(QueueMutation.IgnoredOldKill,
        queue.Apply(report with { Kind = HuntEventKind.Killed, ObservedAt = report.ObservedAt.AddSeconds(-1) }));
    Equal(1, queue.Entries.Count);
}

static void DeathTombstoneIsChronological()
{
    var queue = new HuntQueue();
    var first = Report(HuntSource.Sonar, DateTimeOffset.UtcNow, 23.1f, 22.2f);
    queue.Apply(first);
    queue.Apply(first with { Kind = HuntEventKind.Killed, ObservedAt = first.ObservedAt.AddSeconds(10) });
    Equal(QueueMutation.IgnoredKilledReport,
        queue.Apply(first with { ObservedAt = first.ObservedAt.AddSeconds(5) }));
    Equal(QueueMutation.Added,
        queue.Apply(first with { ObservedAt = first.ObservedAt.AddSeconds(15) }));
}

static void QueueRemainsFifo()
{
    var queue = new HuntQueue();
    var first = Report(HuntSource.Sonar, DateTimeOffset.UtcNow, 23.1f, 22.2f);
    var second = first with
    {
        TerritoryId = 957,
        MarkDataId = 10618,
        MarkName = "Sphatika",
        MapX = 24.2f,
        MapY = 16.8f,
        ObservedAt = first.ObservedAt.AddSeconds(1),
    };
    queue.Apply(first);
    queue.Apply(second);
    Equal(8905u, queue.TakeNext(_ => true, first.ObservedAt.AddSeconds(2), TimeSpan.FromMinutes(45))!.Key.MarkDataId);
    Equal(10618u, queue.TakeNext(_ => true, first.ObservedAt.AddSeconds(2), TimeSpan.FromMinutes(45))!.Key.MarkDataId);
}

static void DisabledExpansionIsPruned()
{
    var queue = new HuntQueue();
    var report = Report(HuntSource.Sonar, DateTimeOffset.UtcNow, 23.1f, 22.2f);
    queue.Apply(report);
    Equal(1, queue.Prune(definition => definition.Expansion != Expansion.Shadowbringers,
        report.ObservedAt.AddSeconds(1), TimeSpan.FromMinutes(45)));
    Equal(0, queue.Entries.Count);
}

static void ParkingRingIsSafe()
{
    var mark = Vector3.Zero;
    var points = SafeParkingPlanner.CreateCandidates(mark, new Vector3(5, 0, 0), 0.5f, 3f, 25f);
    Equal(16, points.Count);
    True(points.All(point => SafeParkingPlanner.Clearance(point, mark, 0.5f, 3f) >= 25f));
}

static void UnsafePathIsRejected()
{
    var path = new[] { new Vector3(40, 0, 0), new Vector3(5, 0, 0), new Vector3(30, 0, 0) };
    True(!SafeParkingPlanner.PathRespectsClearance(path, Vector3.Zero, 0.5f, 3f, 25f));
}

static void RetreatPathEscapesSafely()
{
    var path = new[]
    {
        new Vector3(15, 0, 0),
        new Vector3(22, 0, 0),
        new Vector3(30, 0, 0),
        new Vector3(35, 0, 0),
    };
    True(SafeParkingPlanner.PathEscapesProtectedArea(path, Vector3.Zero, 0.5f, 3f, 25f));
}

static HuntEvent Report(HuntSource source, DateTimeOffset time, float mapX, float mapY) =>
    new(HuntEventKind.Reported, source, null, 74, "Coeurl", 813, 1, 8905, "Tyger", mapX, mapY, time);

static void True(bool value)
{
    if (!value)
        throw new InvalidOperationException("Expected true.");
}

static void Equal<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}
