namespace SRankSentinel;

internal sealed record HuntAlertSnapshot(
    string HuntType,
    string World,
    string CreatureName,
    uint TerritoryId,
    uint MarkDataId,
    uint PreferredAetheryteId,
    int Instance,
    float MapX,
    float MapY,
    DateTime ReceivedAtUtc)
{
    public string Key => $"{World.Trim().ToUpperInvariant()}|{TerritoryId}|{Instance}|" +
                         (MarkDataId == 0 ? CreatureName.Trim().ToUpperInvariant() : MarkDataId);
}

[Serializable]
public sealed class PersistedHuntAlert
{
    public string HuntType { get; set; } = "srank";
    public string World { get; set; } = string.Empty;
    public string CreatureName { get; set; } = string.Empty;
    public uint TerritoryId { get; set; }
    public uint MarkDataId { get; set; }
    public uint PreferredAetheryteId { get; set; }
    public int Instance { get; set; } = 1;
    public float MapX { get; set; }
    public float MapY { get; set; }
    public DateTime ReceivedAtUtc { get; set; }

    internal static PersistedHuntAlert From(HuntAlertSnapshot alert) => new()
    {
        HuntType = alert.HuntType,
        World = alert.World,
        CreatureName = alert.CreatureName,
        TerritoryId = alert.TerritoryId,
        MarkDataId = alert.MarkDataId,
        PreferredAetheryteId = alert.PreferredAetheryteId,
        Instance = alert.Instance,
        MapX = alert.MapX,
        MapY = alert.MapY,
        ReceivedAtUtc = alert.ReceivedAtUtc,
    };

    internal HuntAlertSnapshot ToSnapshot() => new(
        HuntType, World, CreatureName, TerritoryId, MarkDataId, PreferredAetheryteId,
        Instance, MapX, MapY, ReceivedAtUtc);
}

[Serializable]
public sealed class KilledHuntRecord
{
    public string Key { get; set; } = string.Empty;
    public DateTime KilledAtUtc { get; set; }
}
