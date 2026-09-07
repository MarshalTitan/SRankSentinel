using Dalamud.Configuration;
using Dalamud.Plugin;

namespace SRankSentinel;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 8;
    public bool Enabled { get; set; } = true;
    public bool EnableFaloop { get; set; } = true;
    public bool EnableHuntAlertsFallback { get; set; } = true;
    public bool EnableSonarFallback { get; set; } = true;
    public bool EnableCenturio { get; set; }
    public bool EnableShadowbringers { get; set; } = true;
    public bool EnableEndwalker { get; set; } = true;
    public bool EnableDawntrail { get; set; } = true;
    public bool EnableEvercold { get; set; }
    public string FaloopUsername { get; set; } = string.Empty;
    public string FaloopSessionId { get; set; } = string.Empty;
    // Retained so version-7 repository installs can migrate their exact saved global values.
    public float FlagApproachDistance { get; set; } = 60f;
    public float WaitingDistance { get; set; } = 45f;
    public float EmergencyDistance { get; set; } = 38f;
    public float EngageHpPercent { get; set; } = 95f;
    public HuntDistanceProfile LegacyShadowbringersProfile { get; set; } = new();
    public HuntDistanceProfile EndwalkerProfile { get; set; } = new();
    public HuntDistanceProfile DawntrailProfile { get; set; } = new();
    public HuntDistanceProfile EvercoldProfile { get; set; } = new();
    public bool AutomaticTagAction { get; set; } = true;
    public uint TagActionId { get; set; } = 46; // Manual override when automatic selection is disabled.
    public int TravelTimeoutSeconds { get; set; } = 300;
    public int LocateTimeoutSeconds { get; set; } = 90;
    public int PostKillSsGraceSeconds { get; set; } = 2;
    public int SsChainTimeoutSeconds { get; set; } = 300;
    public int AlertFreshnessMinutes { get; set; } = 45;
    public List<PersistedHuntAlert> PendingAlerts { get; set; } = [];
    public List<KilledHuntRecord> KilledAlerts { get; set; } = [];

    [NonSerialized] private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi)
    {
        pluginInterface = pi;

        if (Version < 3)
        {
            if (Math.Abs(WaitingDistance - 40f) < 0.01f)
                WaitingDistance = 45f;
            if (Math.Abs(EmergencyDistance - 30f) < 0.01f)
                EmergencyDistance = 35f;
            if (TagActionId == 0)
                TagActionId = 46;

            Version = 3;
            Save();
        }

        if (Version < 4)
        {
            AutomaticTagAction = true;
            Version = 4;
            Save();
        }

        if (Version < 5)
        {
            if (Math.Abs(EmergencyDistance - 35f) < 0.01f)
                EmergencyDistance = 38f;
            SsChainTimeoutSeconds = Math.Max(300, SsChainTimeoutSeconds);
            Version = 5;
            Save();
        }

        if (Version < 6)
        {
            if (Math.Abs(FlagApproachDistance - 50f) < 0.01f)
                FlagApproachDistance = 60f;
            WaitingDistance = Math.Max(45f, WaitingDistance);
            EmergencyDistance = Math.Clamp(EmergencyDistance, 38f, 40f);
            PostKillSsGraceSeconds = 2;
            SsChainTimeoutSeconds = Math.Max(300, SsChainTimeoutSeconds);
            AlertFreshnessMinutes = Math.Max(10, AlertFreshnessMinutes);
            PendingAlerts ??= [];
            KilledAlerts ??= [];
            Version = 6;
            Save();
        }

        if (Version < 7)
        {
            EnableFaloop = true;
            EnableHuntAlertsFallback = true;
            EnableSonarFallback = true;
            FaloopUsername ??= string.Empty;
            FaloopSessionId ??= string.Empty;
            Version = 7;
            Save();
        }

        if (Version < 8)
        {
            var migrated = new HuntDistanceProfile
            {
                FlagApproachDistance = FlagApproachDistance,
                WaitingDistance = WaitingDistance,
                EmergencyDistance = EmergencyDistance,
                EngageHpPercent = EngageHpPercent,
            };
            LegacyShadowbringersProfile = migrated.Clone();
            EndwalkerProfile = migrated.Clone();
            DawntrailProfile = migrated.Clone();
            EvercoldProfile = migrated.Clone();

            // Preserve the old eligibility behavior on upgrade. Centurio is an explicit opt-in.
            EnableCenturio = false;
            EnableShadowbringers = true;
            EnableEndwalker = true;
            EnableDawntrail = true;
            EnableEvercold = false;
            Version = 8;
            Save();
        }

        LegacyShadowbringersProfile ??= new HuntDistanceProfile();
        EndwalkerProfile ??= new HuntDistanceProfile();
        DawntrailProfile ??= new HuntDistanceProfile();
        EvercoldProfile ??= new HuntDistanceProfile();
    }

    internal bool IsExpansionEnabled(SupportedExpansion expansion) => expansion switch
    {
        SupportedExpansion.Centurio => EnableCenturio,
        SupportedExpansion.Shadowbringers => EnableShadowbringers,
        SupportedExpansion.Endwalker => EnableEndwalker,
        SupportedExpansion.Dawntrail => EnableDawntrail,
        // The setting and profile are reserved for a future data update, but cannot activate yet.
        SupportedExpansion.Evercold => false,
        _ => false,
    };

    internal HuntDistanceProfile GetDistanceProfile(SupportedExpansion expansion) => expansion switch
    {
        SupportedExpansion.Endwalker => EndwalkerProfile,
        SupportedExpansion.Dawntrail => DawntrailProfile,
        SupportedExpansion.Evercold => EvercoldProfile,
        _ => LegacyShadowbringersProfile,
    };

    public void Save() => pluginInterface?.SavePluginConfig(this);
}

[Serializable]
public sealed class HuntDistanceProfile
{
    public float FlagApproachDistance { get; set; } = 60f;
    public float WaitingDistance { get; set; } = 45f;
    public float EmergencyDistance { get; set; } = 38f;
    public float EngageHpPercent { get; set; } = 95f;

    public HuntDistanceProfile Clone() => new()
    {
        FlagApproachDistance = FlagApproachDistance,
        WaitingDistance = WaitingDistance,
        EmergencyDistance = EmergencyDistance,
        EngageHpPercent = EngageHpPercent,
    };
}
