namespace SRankSentinel;

internal sealed record HuntAlertSnapshot(
    string HuntType,
    string World,
    string CreatureName,
    uint TerritoryId,
    int Instance,
    float MapX,
    float MapY,
    DateTime ReceivedAtUtc)
{
    public string Key => $"{World}|{TerritoryId}|{Instance}|{CreatureName}";
}
