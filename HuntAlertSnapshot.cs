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
