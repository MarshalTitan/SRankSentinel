namespace SentinelHunts.Core;

public static class HuntCatalog
{
    private static readonly HuntDefinition[] Definitions =
    [
        new(2962, 134, 52, "Croque-Mitaine", Expansion.ARealmReborn),
        new(2965, 138, 14, "Bonnacon", Expansion.ARealmReborn),
        new(2964, 137, 11, "The Garlok", Expansion.ARealmReborn),
        new(2963, 135, 10, "Croakadile", Expansion.ARealmReborn),
        new(2966, 139, 15, "Nandi", Expansion.ARealmReborn),
        new(2967, 180, 16, "Chernobog", Expansion.ARealmReborn),
        new(2956, 154, 7, "Thousand-cast Theda", Expansion.ARealmReborn),
        new(2954, 152, 4, "Wulgaru", Expansion.ARealmReborn),
        new(2955, 153, 5, "Mindflayer", Expansion.ARealmReborn),
        new(2958, 141, 53, "Brontes", Expansion.ARealmReborn),
        new(2957, 140, 17, "Zona Seeker", Expansion.ARealmReborn),
        new(2961, 147, 22, "Minhocao", Expansion.ARealmReborn),
        new(2968, 155, 23, "Safat", Expansion.ARealmReborn),
        new(2960, 146, 19, "Nunyunuwi", Expansion.ARealmReborn),
        new(2953, 148, 3, "Laideronnette", Expansion.ARealmReborn),
        new(2959, 145, 18, "Lampalagua", Expansion.ARealmReborn),
        new(2969, 156, 24, "Agrippa the Mighty", Expansion.ARealmReborn),

        new(4374, 397, 71, "Kaiser Behemoth", Expansion.Heavensward),
        new(4375, 398, 76, "Senmurv", Expansion.Heavensward),
        new(4376, 399, 75, "The Pale Rider", Expansion.Heavensward),
        new(4378, 401, 73, "Bird of Paradise", Expansion.Heavensward),
        new(4380, 402, 74, "Leucrotta", Expansion.Heavensward),
        new(4377, 400, 78, "Gandarewa", Expansion.Heavensward),

        new(5987, 612, 99, "Udumbara", Expansion.Stormblood),
        new(5988, 620, 100, "Bone Crawler", Expansion.Stormblood),
        new(5989, 621, 102, "Salt and Light", Expansion.Stormblood),
        new(5984, 613, 106, "Okina", Expansion.Stormblood),
        new(5986, 622, 110, "Orghana", Expansion.Stormblood),
        new(5985, 614, 108, "Gamma", Expansion.Stormblood),

        new(8905, 813, 132, "Tyger", Expansion.Shadowbringers),
        new(8910, 814, 139, "Forgiven Pedantry", Expansion.Shadowbringers),
        new(8900, 815, 140, "Tarchia", Expansion.Shadowbringers),
        new(8653, 816, 144, "Aglaope", Expansion.Shadowbringers),
        new(8890, 817, 142, "Ixtab", Expansion.Shadowbringers),
        new(8895, 818, 148, "Gunitt", Expansion.Shadowbringers),

        new(10617, 956, 168, "Burfurlur the Canny", Expansion.Endwalker),
        new(10618, 957, 171, "Sphatika", Expansion.Endwalker),
        new(10619, 958, 172, "Armstrong", Expansion.Endwalker),
        new(10620, 959, 175, "Ruminator", Expansion.Endwalker),
        new(10621, 961, 176, "Ophioneus", Expansion.Endwalker),
        new(10622, 960, 180, "Narrow-rift", Expansion.Endwalker),

        new(13360, 1187, 200, "Kirlirger the Abhorrent", Expansion.Dawntrail),
        new(13444, 1188, 204, "Ihnuxokiy", Expansion.Dawntrail),
        new(12754, 1189, 205, "Neyoozoteel", Expansion.Dawntrail),
        new(13399, 1190, 208, "Sansheya", Expansion.Dawntrail),
        new(13156, 1191, 210, "Atticus the Primogenitor", Expansion.Dawntrail),
        new(13437, 1192, 215, "The Forecaster", Expansion.Dawntrail),
    ];

    private static readonly IReadOnlyDictionary<uint, HuntDefinition> ByDataId =
        Definitions.ToDictionary(definition => definition.DataId);

    private static readonly IReadOnlyDictionary<uint, HuntDefinition> ByTerritory =
        Definitions.ToDictionary(definition => definition.TerritoryId);

    public static IReadOnlyList<HuntDefinition> All { get; } = Array.AsReadOnly(Definitions);

    public static HuntDefinition? FindByDataId(uint dataId) =>
        ByDataId.TryGetValue(dataId, out var definition) ? definition : null;

    public static HuntDefinition? FindByTerritory(uint territoryId) =>
        ByTerritory.TryGetValue(territoryId, out var definition) ? definition : null;

    public static HuntDefinition? Resolve(uint territoryId, string? name)
    {
        if (!ByTerritory.TryGetValue(territoryId, out var definition))
            return null;

        if (string.IsNullOrWhiteSpace(name))
            return definition;

        var normalizedText = Normalize(name);
        var normalizedName = Normalize(definition.Name);
        return normalizedText.Contains(normalizedName, StringComparison.Ordinal) ? definition : null;
    }

    public static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
