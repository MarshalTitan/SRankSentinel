using Dalamud.Configuration;
using Dalamud.Plugin;

namespace SRankSentinel;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 5;
    public bool Enabled { get; set; } = true;
    public float FlagApproachDistance { get; set; } = 50f;
    public float WaitingDistance { get; set; } = 45f;
    public float EmergencyDistance { get; set; } = 38f;
    public float EngageHpPercent { get; set; } = 95f;
    public bool AutomaticTagAction { get; set; } = true;
    public uint TagActionId { get; set; } = 46; // Manual override when automatic selection is disabled.
    public int TravelTimeoutSeconds { get; set; } = 300;
    public int LocateTimeoutSeconds { get; set; } = 90;
    public int SsNoChainGraceSeconds { get; set; } = 30;
    public int SsChainTimeoutSeconds { get; set; } = 300;

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
            SsNoChainGraceSeconds = Math.Max(15, SsNoChainGraceSeconds);
            SsChainTimeoutSeconds = Math.Max(300, SsChainTimeoutSeconds);
            Version = 5;
            Save();
        }
    }
    public void Save() => pluginInterface?.SavePluginConfig(this);
}
