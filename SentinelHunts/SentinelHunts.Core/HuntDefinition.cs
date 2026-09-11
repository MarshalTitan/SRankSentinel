namespace SentinelHunts.Core;

public sealed record HuntDefinition(
    uint DataId,
    uint TerritoryId,
    uint PreferredAetheryteId,
    string Name,
    Expansion Expansion);
