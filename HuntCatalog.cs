namespace SRankSentinel;

internal sealed record SRankDefinition(
    uint DataId,
    uint TerritoryId,
    uint PreferredAetheryteId,
    string Name);

internal enum SupportedExpansion
{
    None,
    Centurio,
    Shadowbringers,
    Endwalker,
    Dawntrail,
    Evercold,
}

internal sealed record SsProfile(
    SupportedExpansion Expansion,
    string ExpansionName,
    IReadOnlySet<uint> TerritoryIds,
    uint SsDataId,
    string SsName,
    string PrecursorName);

internal sealed record SsStagingLocation(
    uint TerritoryId,
    string TerritoryName,
    string SsName,
    float MapX,
    float MapY);

internal sealed record SsStagingCoverageAudit(int ExpectedCount, int ConfiguredCount, IReadOnlyList<string> Issues);

/// <summary>
/// Stable game-data identifiers for open-world S ranks.  Alert plugins remain the source of
/// spawn coordinates; this catalog is only used to validate the actor and choose the normal
/// teleport destination deterministically.
/// </summary>
internal static class HuntCatalog
{
    public const uint DravanianHinterlandsTerritoryId = 399;
    public const uint IdyllshireTerritoryId = 478;
    public const uint IdyllshireAetheryteId = 75;
    public const uint ForgivenRebellionDataId = 8915;
    public const uint KerDataId = 10615;
    public const string ForgivenRebellionName = "Forgiven Rebellion";
    public const string ForgivenGossipName = "Forgiven Gossip";
    public const string KerName = "Ker";
    public const string KerShroudName = "Ker Shroud";
    public const string ArchAethereaterName = "Arch Aethereater";
    public const string CrystalIncarnationName = "Crystal Incarnation";

    private static readonly HashSet<uint> CenturioTerritories =
    [
        134, 135, 137, 138, 139, 180,
        140, 141, 145, 146, 147,
        148, 152, 153, 154, 155, 156,
        397, 398, 399, 400, 401, 402,
        612, 613, 614, 620, 621, 622,
    ];
    private static readonly HashSet<uint> ShadowbringersTerritories = [813, 814, 815, 816, 817, 818];
    private static readonly HashSet<uint> EndwalkerTerritories = [956, 957, 958, 959, 960, 961];
    private static readonly HashSet<uint> DawntrailTerritories = [1187, 1188, 1189, 1190, 1191, 1192];

    private static readonly SsProfile[] SsProfiles =
    [
        new(SupportedExpansion.Shadowbringers, "Shadowbringers", ShadowbringersTerritories,
            ForgivenRebellionDataId, ForgivenRebellionName, ForgivenGossipName),
        new(SupportedExpansion.Endwalker, "Endwalker", EndwalkerTerritories,
            KerDataId, KerName, KerShroudName),
        // Arch Aethereater is matched by its localized actor name. Alert coordinates remain
        // authoritative, so an unstable battle-NPC ID is deliberately not required here.
        new(SupportedExpansion.Dawntrail, "Dawntrail", DawntrailTerritories,
            0, ArchAethereaterName, CrystalIncarnationName),
    ];

    // Fixed SS locations verified against the reviewed Faloop POI snapshot in FaloopCatalog.
    // The POI values are kept as the canonical coordinates where they differ slightly from
    // rounded community map labels (for example The Tempest X12.9 Y22.2 rather than X13 Y22).
    private static readonly IReadOnlyDictionary<uint, SsStagingLocation> SsStagingLocations =
        new Dictionary<uint, SsStagingLocation>
        {
            [813] = new(813, "Lakeland", ForgivenRebellionName, 23.3f, 22.1f),
            [814] = new(814, "Kholusia", ForgivenRebellionName, 34.3f, 10.5f),
            [815] = new(815, "Amh Araeng", ForgivenRebellionName, 27.5f, 35.1f),
            [816] = new(816, "Il Mheg", ForgivenRebellionName, 13.4f, 22.9f),
            [817] = new(817, "The Rak'tika Greatwood", ForgivenRebellionName, 24.4f, 37.2f),
            [818] = new(818, "The Tempest", ForgivenRebellionName, 12.9f, 22.2f),

            [956] = new(956, "Labyrinthos", KerName, 24.9f, 16.0f),
            [957] = new(957, "Thavnair", KerName, 24.2f, 16.8f),
            [958] = new(958, "Garlemald", KerName, 20.3f, 23.7f),
            [959] = new(959, "Mare Lamentorum", KerName, 18.5f, 30.2f),
            [960] = new(960, "Ultima Thule", KerName, 14.5f, 29.6f),
            [961] = new(961, "Elpis", KerName, 22.7f, 19.5f),

            [1187] = new(1187, "Urqopacha", ArchAethereaterName, 25.9f, 27.9f),
            [1188] = new(1188, "Kozama'uka", ArchAethereaterName, 13.8f, 14.8f),
            [1189] = new(1189, "Yak T'el", ArchAethereaterName, 29.7f, 18.9f),
            [1190] = new(1190, "Shaaloani", ArchAethereaterName, 13.3f, 13.3f),
            [1191] = new(1191, "Heritage Found", ArchAethereaterName, 17.5f, 20.3f),
            [1192] = new(1192, "Living Memory", ArchAethereaterName, 34.4f, 26.3f),
        };

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
        var ssProfile = GetSsProfileForTerritory(territoryId);
        if (ssProfile is not null && IsSsName(alertName, ssProfile))
        {
            var zone = Definitions.FirstOrDefault(definition => definition.TerritoryId == territoryId);
            return zone is null
                ? null
                : new SRankDefinition(ssProfile.SsDataId, territoryId, zone.PreferredAetheryteId,
                    ssProfile.SsName);
        }

        var normalized = Normalize(alertName);
        return Definitions.FirstOrDefault(definition =>
                   definition.TerritoryId == territoryId && Normalize(definition.Name) == normalized)
               ?? Definitions.FirstOrDefault(definition => definition.TerritoryId == territoryId);
    }

    /// <summary>
    /// Strict resolver for machine-readable feeds. Unlike the human-alert resolver above, this
    /// never guesses the sole S rank in a territory when the supplied identity is unknown.
    /// </summary>
    public static SRankDefinition? ResolveStrict(uint territoryId, string alertName)
    {
        var ssProfile = GetSsProfileForTerritory(territoryId);
        if (ssProfile is not null && IsSsName(alertName, ssProfile))
        {
            var zone = Definitions.FirstOrDefault(definition => definition.TerritoryId == territoryId);
            return zone is null
                ? null
                : new SRankDefinition(ssProfile.SsDataId, territoryId, zone.PreferredAetheryteId,
                    ssProfile.SsName);
        }

        var normalized = Normalize(alertName);
        return Definitions.FirstOrDefault(definition =>
            definition.TerritoryId == territoryId && Normalize(definition.Name) == normalized);
    }

    /// <summary>
    /// Resolves a supported regular S rank when Faloop's lightweight sighting event carries
    /// mob/world identity and a POI but omits the redundant zone slug. Regular S-rank names are
    /// unique in the supported catalog; shared expansion SS names intentionally do not resolve
    /// here because their territory cannot be inferred safely from the name alone.
    /// </summary>
    public static SRankDefinition? ResolveUniqueName(string alertName)
    {
        var normalized = Normalize(alertName);
        var matches = Definitions.Where(definition => Normalize(definition.Name) == normalized).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public static bool NamesMatch(string? left, string? right) =>
        Normalize(left ?? string.Empty) == Normalize(right ?? string.Empty);

    public static bool IsShadowbringersTerritory(uint territoryId) =>
        ShadowbringersTerritories.Contains(territoryId);

    public static bool IsSupportedTerritory(uint territoryId) =>
        GetExpansion(territoryId) is not SupportedExpansion.None;

    public static IReadOnlyCollection<uint> SupportedTerritoryIds { get; } =
        Definitions.Select(definition => definition.TerritoryId).Distinct().Order().ToArray();

    public static SupportedExpansion GetExpansion(uint territoryId)
    {
        if (CenturioTerritories.Contains(territoryId))
            return SupportedExpansion.Centurio;
        if (ShadowbringersTerritories.Contains(territoryId))
            return SupportedExpansion.Shadowbringers;
        if (EndwalkerTerritories.Contains(territoryId))
            return SupportedExpansion.Endwalker;
        if (DawntrailTerritories.Contains(territoryId))
            return SupportedExpansion.Dawntrail;
        return SupportedExpansion.None;
    }

    public static string ExpansionName(SupportedExpansion expansion) => expansion switch
    {
        SupportedExpansion.Centurio => "Centurio (ARR / HW / SB)",
        SupportedExpansion.Shadowbringers => "Shadowbringers",
        SupportedExpansion.Endwalker => "Endwalker",
        SupportedExpansion.Dawntrail => "Dawntrail",
        SupportedExpansion.Evercold => "Evercold",
        _ => "Unknown expansion",
    };

    public static SsProfile? GetSsProfileForTerritory(uint territoryId) =>
        SsProfiles.FirstOrDefault(profile => profile.TerritoryIds.Contains(territoryId));

    public static bool TryGetSsStagingLocation(uint territoryId, out SsStagingLocation location)
    {
        if (SsStagingLocations.TryGetValue(territoryId, out var configured))
        {
            location = configured;
            return true;
        }

        location = null!;
        return false;
    }

    public static SsStagingCoverageAudit AuditSsStagingLocations()
    {
        var expectedTerritories = SsProfiles.SelectMany(profile => profile.TerritoryIds).Distinct().Order().ToArray();
        var issues = new List<string>();
        foreach (var territoryId in expectedTerritories)
        {
            if (!SsStagingLocations.TryGetValue(territoryId, out var location))
            {
                issues.Add($"territory {territoryId} has no fixed SS staging location");
                continue;
            }

            var profile = GetSsProfileForTerritory(territoryId);
            if (profile is null || !NamesMatch(profile.SsName, location.SsName))
                issues.Add($"territory {territoryId} stages {location.SsName} but its SS profile does not match");
            if (!float.IsFinite(location.MapX) || !float.IsFinite(location.MapY) ||
                location.MapX < 1f || location.MapX > 50f || location.MapY < 1f || location.MapY > 50f)
                issues.Add($"territory {territoryId} has unusable SS staging coordinates ({location.MapX}, {location.MapY})");
        }

        foreach (var territoryId in SsStagingLocations.Keys.Except(expectedTerritories))
            issues.Add($"territory {territoryId} has an orphaned SS staging location");

        return new SsStagingCoverageAudit(expectedTerritories.Length, SsStagingLocations.Count, issues);
    }

    public static SsProfile? GetSsProfileForSsName(string? name) =>
        SsProfiles.FirstOrDefault(profile => IsSsName(name, profile));

    public static SsProfile? FindSsProfileInText(string? text) =>
        SsProfiles.FirstOrDefault(profile =>
            !ContainsPhrase(text, profile.PrecursorName) && ContainsPhrase(text, profile.SsName));

    public static SsProfile? GetSsProfileForPrecursorName(string? name) =>
        SsProfiles.FirstOrDefault(profile => IsPrecursorName(name, profile));

    public static bool IsSupportedNormalS(uint territoryId, string name)
    {
        var profile = GetSsProfileForTerritory(territoryId);
        return profile is not null && !IsSsName(name, profile);
    }

    public static bool IsSsName(string? name, SsProfile profile) =>
        Normalize(name ?? string.Empty) == Normalize(profile.SsName);

    public static bool IsPrecursorName(string? name, SsProfile profile) =>
        Normalize(name ?? string.Empty) == Normalize(profile.PrecursorName);

    public static bool IsAnySsName(string? name) => GetSsProfileForSsName(name) is not null;

    public static bool IsAnyPrecursorName(string? name) => GetSsProfileForPrecursorName(name) is not null;

    public static bool TextMentionsMark(string? text, string markName)
    {
        var ssProfile = GetSsProfileForSsName(markName);
        return (ssProfile is null || !ContainsPhrase(text, ssProfile.PrecursorName)) &&
               ContainsPhrase(text, markName);
    }

    public static bool IsForgivenRebellion(string? name) =>
        Normalize(name ?? string.Empty).EndsWith("FORGIVENREBELLION", StringComparison.Ordinal);

    public static bool IsForgivenGossip(string? name) =>
        Normalize(name ?? string.Empty).EndsWith("FORGIVENGOSSIP", StringComparison.Ordinal);

    public static bool IsSsChainStartMessage(string text) =>
        text.Contains("minions of an extraordinarily powerful mark are on the hunt", StringComparison.OrdinalIgnoreCase);

    public static bool IsSsChainWithdrawnMessage(string text) =>
        text.Contains("minions of an extraordinarily powerful mark have withdrawn", StringComparison.OrdinalIgnoreCase);

    public static bool IsSsSpawnMessage(string text) =>
        text.Contains("presence of an extraordinarily powerful mark", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("presence of a powerful mark", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static bool ContainsPhrase(string? text, string phrase)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var start = 0;
        while ((start = text.IndexOf(phrase, start, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var before = start == 0 ? '\0' : text[start - 1];
            var end = start + phrase.Length;
            var after = end >= text.Length ? '\0' : text[end];
            if (!char.IsLetterOrDigit(before) && !char.IsLetterOrDigit(after))
                return true;
            start = end;
        }
        return false;
    }
}
