using Dalamud.Configuration;
using Dalamud.Plugin;

namespace SRankSentinel;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public float FlagApproachDistance { get; set; } = 80f;
    public float WaitingDistance { get; set; } = 65f;
    public float EmergencyDistance { get; set; } = 55f;
    public float EngageHpPercent { get; set; } = 95f; // reserved for v0.2
    public int ArrivalTimeoutSeconds { get; set; } = 120;
    public int LocateTimeoutSeconds { get; set; } = 90;

    [NonSerialized] private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi) => pluginInterface = pi;
    public void Save() => pluginInterface?.SavePluginConfig(this);
}
