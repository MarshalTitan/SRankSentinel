namespace SRankSentinel;

internal sealed record SRankDefinition(
    uint DataId,
    uint TerritoryId,
    uint PreferredAetheryteId,
    string Name);

/// <summary>
/// Stable game-data identifiers for open-world S ranks.  Alert plugins remain the source of
/// spawn coordinates; this catalog is only used to validate the actor and choose the normal
/// teleport destination deterministically.
/// </summary>
internal static class HuntCatalog
{
    public const uint ForgivenRebellionDataId = 8915;
    public const string ForgivenRebellionName = "Forgiven Rebellion";
    public const string ForgivenGossipName = "Forgiven Gossip";

    private static readonly HashSet<uint> ShadowbringersTerritories = [813, 814, 815, 816, 817, 818];

    private static readonly SRankDefinition[] Definitions =
    [
        new(2962, 134, 52, "Croque-Mitaine"),
        new(2965, 138, 14, "Bonnacon"),
        new(2964, 137, 11, "The Garlok"),
        new(2963, 135, 10, "Croakadile"),
        new(2966, 139, 15, "Nandi"),
        new(2967, 180, 16, "Chernobog"),
        new(2956, 154, 7, "Thousand-cast Theda"),
        new(2954, 152, 4, "Wulgaru"),
        new(2955, 153, 5, "Mindflayer"),
        new(2958, 141, 53, "Brontes"),
        new(2957, 140, 17, "Zona Seeker"),
        new(2961, 147, 22, "Minhocao"),
        new(2968, 155, 23, "Safat"),
        new(2960, 146, 19, "Nunyunuwi"),
        new(2953, 148, 3, "Laideronnette"),
        new(2959, 145, 18, "Lampalagua"),
        new(2969, 156, 24, "Agrippa the Mighty"),

        new(4374, 397, 71, "Kaiser Behemoth"),
        new(4375, 398, 76, "Senmurv"),
        new(4376, 399, 75, "The Pale Rider"),
        new(4378, 401, 73, "Bird of Paradise"),
        new(4380, 402, 74, "Leucrotta"),
        new(4377, 400, 78, "Gandarewa"),

        new(5987, 612, 99, "Udumbara"),
        new(5988, 620, 100, "Bone Crawler"),
        new(5989, 621, 102, "Salt and Light"),
        new(5984, 613, 106, "Okina"),
        new(5986, 622, 110, "Orghana"),
        new(5985, 614, 108, "Gamma"),

        new(8905, 813, 132, "Tyger"),
        new(8910, 814, 139, "Forgiven Pedantry"),
        new(8900, 815, 140, "Tarchia"),
        new(8653, 816, 144, "Aglaope"),
        new(8890, 817, 142, "Ixtab"),
        new(8895, 818, 148, "Gunitt"),

        new(10617, 956, 168, "Burfurlur the Canny"),
        new(10618, 957, 171, "Sphatika"),
        new(10619, 958, 172, "Armstrong"),
        new(10620, 959, 175, "Ruminator"),
        new(10621, 961, 176, "Ophioneus"),
        new(10622, 960, 180, "Narrow-rift"),

        new(13360, 1187, 200, "Kirlirger the Abhorrent"),
        new(13444, 1188, 204, "Ihnuxokiy"),
        new(12754, 1189, 205, "Neyoozoteel"),
        new(13399, 1190, 208, "Sansheya"),
        new(13156, 1191, 210, "Atticus the Primogenitor"),
        new(13437, 1192, 215, "The Forecaster"),
    ];

    public static SRankDefinition? Resolve(uint territoryId, string alertName)
    {
        if (IsForgivenRebellion(alertName) && IsShadowbringersTerritory(territoryId))
        {
            var zone = Definitions.FirstOrDefault(definition => definition.TerritoryId == territoryId);
            return zone is null
                ? null
                : new SRankDefinition(ForgivenRebellionDataId, territoryId, zone.PreferredAetheryteId,
                    ForgivenRebellionName);
        }

        var normalized = Normalize(alertName);
        return Definitions.FirstOrDefault(definition =>
                   definition.TerritoryId == territoryId && Normalize(definition.Name) == normalized)
               ?? Definitions.FirstOrDefault(definition => definition.TerritoryId == territoryId);
    }

    public static bool IsShadowbringersTerritory(uint territoryId) =>
        ShadowbringersTerritories.Contains(territoryId);

    public static bool IsShadowbringersS(uint territoryId, string name) =>
        IsShadowbringersTerritory(territoryId) && !IsForgivenRebellion(name);

    public static bool IsForgivenRebellion(string? name) =>
        Normalize(name ?? string.Empty).EndsWith("FORGIVENREBELLION", StringComparison.Ordinal);

    public static bool IsForgivenGossip(string? name) =>
        Normalize(name ?? string.Empty).EndsWith("FORGIVENGOSSIP", StringComparison.Ordinal);

    public static bool IsSsChainStartMessage(string text) =>
        text.Contains("minions of an extraordinarily powerful mark are on the hunt", StringComparison.OrdinalIgnoreCase);

    public static bool IsSsChainWithdrawnMessage(string text) =>
        text.Contains("minions of an extraordinarily powerful mark have withdrawn", StringComparison.OrdinalIgnoreCase);

    public static bool IsSsSpawnMessage(string text) =>
        text.Contains("presence of an extraordinarily powerful mark", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
