using Dalamud.Configuration;
using Dalamud.Plugin;
using SentinelHunts.Core;

namespace SentinelHunts;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool AutomationEnabled { get; set; }
    public bool EnableSonar { get; set; } = true;
    public bool EnableHuntAlerts { get; set; } = true;
    public bool EnableFaloop { get; set; }
    public bool EnableARealmReborn { get; set; }
    public bool EnableHeavensward { get; set; }
    public bool EnableStormblood { get; set; }
    public bool EnableShadowbringers { get; set; } = true;
    public bool EnableEndwalker { get; set; } = true;
    public bool EnableDawntrail { get; set; } = true;
    public float SafeHitboxClearance { get; set; } = 25f;
    public float InitialApproachDistance { get; set; } = 55f;
    public float EngageHpPercent { get; set; } = 95f;
    public int AlertFreshnessMinutes { get; set; } = 45;
    public List<PersistedHunt> PendingHunts { get; set; } = [];

    [NonSerialized] private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi)
    {
        pluginInterface = pi;
        SafeHitboxClearance = Math.Clamp(SafeHitboxClearance, 20f, 40f);
        InitialApproachDistance = Math.Clamp(InitialApproachDistance, SafeHitboxClearance + 10f, 100f);
        EngageHpPercent = Math.Clamp(EngageHpPercent, 1f, 99f);
        AlertFreshnessMinutes = Math.Clamp(AlertFreshnessMinutes, 10, 120);
        PendingHunts ??= [];
    }

    public bool IsEnabled(HuntDefinition definition) => definition.Expansion switch
    {
        Expansion.ARealmReborn => EnableARealmReborn,
        Expansion.Heavensward => EnableHeavensward,
        Expansion.Stormblood => EnableStormblood,
        Expansion.Shadowbringers => EnableShadowbringers,
        Expansion.Endwalker => EnableEndwalker,
        Expansion.Dawntrail => EnableDawntrail,
        _ => false,
    };

    public void Save() => pluginInterface?.SavePluginConfig(this);
}

[Serializable]
public sealed class PersistedHunt
{
    public uint WorldId { get; set; }
    public string WorldName { get; set; } = string.Empty;
    public uint TerritoryId { get; set; }
    public byte Instance { get; set; } = 1;
    public uint MarkDataId { get; set; }
    public string MarkName { get; set; } = string.Empty;
    public float MapX { get; set; }
    public float MapY { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
}
