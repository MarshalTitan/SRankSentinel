namespace SentinelHunts.Core;

public sealed class HuntQueueEntry
{
    private readonly HashSet<HuntSource> sources = [];

    public HuntQueueEntry(HuntEvent report)
    {
        if (report.Kind != HuntEventKind.Reported)
            throw new ArgumentException("A queue entry requires a reported hunt.", nameof(report));

        Key = report.Key;
        WorldName = report.WorldName;
        MarkName = report.MarkName;
        MapX = report.MapX;
        MapY = report.MapY;
        FirstObservedAt = report.ObservedAt;
        LastObservedAt = report.ObservedAt;
        sources.Add(report.Source);
    }

    public HuntKey Key { get; }
    public string WorldName { get; private set; }
    public string MarkName { get; private set; }
    public float MapX { get; private set; }
    public float MapY { get; private set; }
    public DateTimeOffset FirstObservedAt { get; }
    public DateTimeOffset LastObservedAt { get; private set; }
    public IReadOnlyCollection<HuntSource> Sources => sources;

    public void Merge(HuntEvent report)
    {
        if (report.Key != Key || report.Kind != HuntEventKind.Reported)
            throw new ArgumentException("Only a matching report can be merged.", nameof(report));

        sources.Add(report.Source);
        if (report.ObservedAt < LastObservedAt)
            return;

        LastObservedAt = report.ObservedAt;
        if (!string.IsNullOrWhiteSpace(report.WorldName))
            WorldName = report.WorldName;
        if (!string.IsNullOrWhiteSpace(report.MarkName))
            MarkName = report.MarkName;
        if (report.HasCoordinates)
        {
            MapX = report.MapX;
            MapY = report.MapY;
        }
    }
}
