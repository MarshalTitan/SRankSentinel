using Dalamud.Configuration;
using Dalamud.Plugin;

namespace SRankSentinel;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 3;
    public bool Enabled { get; set; } = true;
    public float FlagApproachDistance { get; set; } = 50f;
    public float WaitingDistance { get; set; } = 45f;
    public float EmergencyDistance { get; set; } = 35f;
    public float EngageHpPercent { get; set; } = 95f;
    public uint TagActionId { get; set; } = 46; // Tomahawk; configurable for other jobs.
    public int TravelTimeoutSeconds { get; set; } = 300;
    public int LocateTimeoutSeconds { get; set; } = 90;

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
    }
    public void Save() => pluginInterface?.SavePluginConfig(this);
}
