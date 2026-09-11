namespace SentinelHunts.Core;

public enum HuntEventKind
{
    Reported,
    Killed,
}

public enum HuntSource
{
    Sonar,
    HuntAlerts,
    Faloop,
    ManualTest,
}

public readonly record struct HuntKey(
    uint WorldId,
    uint TerritoryId,
    byte Instance,
    uint MarkDataId);

public sealed record HuntEvent(
    HuntEventKind Kind,
    HuntSource Source,
    string? SourceEventId,
    uint WorldId,
    string WorldName,
    uint TerritoryId,
    byte Instance,
    uint MarkDataId,
    string MarkName,
    float MapX,
    float MapY,
    DateTimeOffset ObservedAt)
{
    public HuntKey Key => new(WorldId, TerritoryId, Instance, MarkDataId);
    public bool HasCoordinates => float.IsFinite(MapX) && float.IsFinite(MapY) &&
                                  MapX is > 0 and <= 50 && MapY is > 0 and <= 50;
}
