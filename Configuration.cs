using Dalamud.Configuration;
using Dalamud.Plugin;

namespace SRankSentinel;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 12;
    public bool Enabled { get; set; } = true;
    public bool EnableFaloop { get; set; }
    public bool EnableHuntAlertsFallback { get; set; } = true;
    public bool EnableSonarFallback { get; set; } = true;
    public bool EnableCenturio { get; set; }
    public bool EnableShadowbringers { get; set; } = true;
    public bool EnableEndwalker { get; set; } = true;
    public bool EnableDawntrail { get; set; } = true;
    public bool EnableEvercold { get; set; }
    public string FaloopUsername { get; set; } = string.Empty;
    public string FaloopSessionId { get; set; } = string.Empty;
    public bool RememberFaloopLogin { get; set; }
    // Windows DPAPI ciphertext only. A plaintext Faloop password is never serialized.
    public string FaloopProtectedPassword { get; set; } = string.Empty;
    // Retained so version-7 repository installs can migrate their exact saved global values.
    public float FlagApproachDistance { get; set; } = 60f;
    public float WaitingDistance { get; set; } = 45f;
    public float EmergencyDistance { get; set; } = 38f;
    public float EngageHpPercent { get; set; } = 95f;
    // Version-8 expansion-named profiles are retained so existing repository installs can
    // migrate without losing their saved values. Runtime behavior uses the shared profiles.
    public HuntDistanceProfile LegacyShadowbringersProfile { get; set; } = new();
    public HuntDistanceProfile EndwalkerProfile { get; set; } = new();
    public HuntDistanceProfile DawntrailProfile { get; set; } = new();
    public HuntDistanceProfile EvercoldProfile { get; set; } = new();
    public HuntDistanceProfile CloseSafeProfile { get; set; } = new();
    public HuntDistanceProfile ProximitySensitiveProfile { get; set; } = new();
    // Retained for schema compatibility with older installs. Runtime tagging now always selects
    // the current job's supported ranged action and no longer exposes a manual UI override.
    public bool AutomaticTagAction { get; set; } = true;
    public uint TagActionId { get; set; } = 46;
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

        if (Version < 9)
        {
            CloseSafeProfile = (LegacyShadowbringersProfile ?? new HuntDistanceProfile()).Clone();

            // EW and DT were separately configurable in version 8. Consolidate them without
            // lowering either expansion's saved numeric clearances. In normal upgrades these
            // profiles are identical because version 8 cloned the same global settings.
            var endwalker = EndwalkerProfile ?? new HuntDistanceProfile();
            var dawntrail = DawntrailProfile ?? new HuntDistanceProfile();
            ProximitySensitiveProfile = HuntDistanceProfile.ConservativeMerge(endwalker, dawntrail);

            Version = 9;
            Save();
        }

        if (Version < 10)
        {
            // Preserve the existing username and authenticated session exactly. Remembering the
            // password remains opt-in and begins only after the user authenticates with it.
            RememberFaloopLogin = false;
            FaloopProtectedPassword = string.Empty;
            Version = 10;
            Save();
        }

        if (Version < 11)
        {
            // HuntAlerts and Sonar are the production alert providers. Keep the direct Faloop
            // implementation dormant for future development, but do not connect repository
            // users to its evolving event feed or expose its credentials in the public UI.
            EnableFaloop = false;
            EnableHuntAlertsFallback = true;
            EnableSonarFallback = true;
            Version = 11;
            Save();
        }

        // These source roles are intentionally fixed in the simplified public settings.
        EnableFaloop = false;
        EnableHuntAlertsFallback = true;
        EnableSonarFallback = true;

        LegacyShadowbringersProfile ??= new HuntDistanceProfile();
        EndwalkerProfile ??= new HuntDistanceProfile();
        DawntrailProfile ??= new HuntDistanceProfile();
        EvercoldProfile ??= new HuntDistanceProfile();
        CloseSafeProfile ??= LegacyShadowbringersProfile.Clone();
        ProximitySensitiveProfile ??= HuntDistanceProfile.ConservativeMerge(
            EndwalkerProfile, DawntrailProfile);
        FaloopUsername ??= string.Empty;
        FaloopSessionId ??= string.Empty;
        FaloopProtectedPassword ??= string.Empty;

        if (Version < 12)
        {
            // Preserve every lower saved value while bringing all three distance controls under
            // the simplified 35y UI/runtime maximum.
            CloseSafeProfile.FlagApproachDistance = Math.Min(35f, CloseSafeProfile.FlagApproachDistance);
            CloseSafeProfile.WaitingDistance = Math.Min(35f, CloseSafeProfile.WaitingDistance);
            CloseSafeProfile.EmergencyDistance = Math.Min(35f, CloseSafeProfile.EmergencyDistance);
            ProximitySensitiveProfile.FlagApproachDistance =
                Math.Min(35f, ProximitySensitiveProfile.FlagApproachDistance);
            ProximitySensitiveProfile.WaitingDistance =
                Math.Min(35f, ProximitySensitiveProfile.WaitingDistance);
            ProximitySensitiveProfile.EmergencyDistance =
                Math.Min(35f, ProximitySensitiveProfile.EmergencyDistance);
            Version = 12;
            Save();
        }

        CloseSafeProfile.EnforceClearanceInvariant();
        ProximitySensitiveProfile.EnforceClearanceInvariant();
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

    internal HuntBehaviorProfile GetBehaviorProfile(SupportedExpansion expansion) => expansion switch
    {
        SupportedExpansion.Endwalker => HuntBehaviorProfile.ProximitySensitive,
        SupportedExpansion.Dawntrail => HuntBehaviorProfile.ProximitySensitive,
        SupportedExpansion.Evercold => HuntBehaviorProfile.ProximitySensitive,
        _ => HuntBehaviorProfile.CloseSafe,
    };

    internal HuntDistanceProfile GetDistanceProfile(SupportedExpansion expansion)
    {
        var profile = GetBehaviorProfile(expansion) switch
        {
            HuntBehaviorProfile.ProximitySensitive => ProximitySensitiveProfile,
            _ => CloseSafeProfile,
        };
        profile.EnforceClearanceInvariant();
        return profile;
    }

    public void Save()
    {
        CloseSafeProfile?.EnforceClearanceInvariant();
        ProximitySensitiveProfile?.EnforceClearanceInvariant();
        pluginInterface?.SavePluginConfig(this);
    }
}

internal enum HuntBehaviorProfile
{
    CloseSafe,
    ProximitySensitive,
}

[Serializable]
public sealed class HuntDistanceProfile
{
    public float FlagApproachDistance { get; set; } = 35f;
    public float WaitingDistance { get; set; } = 35f;
    public float EmergencyDistance { get; set; } = 35f;
    public float EngageHpPercent { get; set; } = 95f;

    public HuntDistanceProfile Clone() => new()
    {
        FlagApproachDistance = FlagApproachDistance,
        WaitingDistance = WaitingDistance,
        EmergencyDistance = EmergencyDistance,
        EngageHpPercent = EngageHpPercent,
    };

    public void EnforceClearanceInvariant() =>
        EmergencyDistance = Math.Min(EmergencyDistance, WaitingDistance);

    public static HuntDistanceProfile ConservativeMerge(
        HuntDistanceProfile first,
        HuntDistanceProfile second)
    {
        var merged = new HuntDistanceProfile
        {
            FlagApproachDistance = Math.Max(first.FlagApproachDistance, second.FlagApproachDistance),
            WaitingDistance = Math.Max(first.WaitingDistance, second.WaitingDistance),
            EmergencyDistance = Math.Max(first.EmergencyDistance, second.EmergencyDistance),
            EngageHpPercent = Math.Min(first.EngageHpPercent, second.EngageHpPercent),
        };
        merged.EnforceClearanceInvariant();
        return merged;
    }
}
