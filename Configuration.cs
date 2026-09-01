using Dalamud.Configuration;
using Dalamud.Plugin;

namespace SRankSentinel;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;
    public bool Enabled { get; set; } = true;
    public float FlagApproachDistance { get; set; } = 50f;
    public float WaitingDistance { get; set; } = 40f;
    public float EmergencyDistance { get; set; } = 30f;
    public bool ReturnToUldahAfterKill { get; set; } = true;
    public float EngageHpPercent { get; set; } = 95f; // reserved for v0.2
    public int ArrivalTimeoutSeconds { get; set; } = 120;
    public int LocateTimeoutSeconds { get; set; } = 90;

    [NonSerialized] private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi)
    {
        pluginInterface = pi;

        // Carry existing installations onto the safer field-test defaults without
        // overwriting distances the user already customized.
        if (Version < 2)
        {
            if (Math.Abs(FlagApproachDistance - 80f) < 0.01f)
                FlagApproachDistance = 50f;
            if (Math.Abs(WaitingDistance - 65f) < 0.01f)
                WaitingDistance = 40f;
            if (Math.Abs(EmergencyDistance - 55f) < 0.01f)
                EmergencyDistance = 30f;

            Version = 2;
            Save();
        }
    }
    public void Save() => pluginInterface?.SavePluginConfig(this);
}
