using System.Reflection;
using System.Text.Json;
using Dalamud.Plugin;
using SRankSentinel;

internal static partial class Program
{
    private static void TestAssistDefaults()
    {
        var cfg = JsonSerializer.Deserialize<Configuration>("{}")!;
        Check(!cfg.FullCombatAssist && cfg.RetagAfterRaise && cfg.TagAt75Percent, "Old saves must retain safe tagging");
        cfg.FullCombatAssist = true;
        cfg.TagAt50Percent = false;
        var restored = JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(cfg))!;
        Check(restored.FullCombatAssist && !restored.TagAt50Percent && restored.RetagAfterRaise, "Tag options must be independent");
    }

    private static void TestAssistReadiness()
    {
        var pi = DispatchProxy.Create<IDalamudPluginInterface, AssistProbeInterface>();
        var probe = (AssistProbeInterface)(object)pi;
        var adapter = new CombatAssistIpc(pi);
        var f = new AssistFight(101, 202, 21, 1);
        foreach (var (channel, value, reason) in new (string, object?, string)[] {
            ("WrathCombo.IPCReady", false, "Wrath IPC unavailable"),
            ("WrathCombo.IsCurrentJobAutoRotationReady", false, "not ready"),
            ("BossMod.AI.IsEnabled", false, "AI disabled"),
            ("BossMod.HasModuleByDataId", false, "no module"),
            ("BossMod.Configuration", new List<string>{"True"}, "movement disabled"),
            ("BossMod.Configuration", new List<string>{"unknown"}, "permission unknown"),
            ("BossMod.Configuration", new List<string>{"False","False"}, "permission unknown"),
            ("BossMod.Configuration", null, "permission unknown"),
            ("WrathCombo.GetAutoRotationState", true, "standalone"),
        })
        {
            probe.ExpectedOid = f.BaseId;
            probe.OverrideChannel = channel;
            probe.OverrideValue = value;
            Check(adapter.Unavailable(f)?.Contains(reason) == true, "Wrong fallback reason: " + reason);
            Check(adapter.TryAcquire(f) is null, "Readiness failure granted rotation");
        }
        Check(probe.DidCheckModule && probe.DidReadMovement && !probe.AttemptedMutation, "Probe wrote config or missed a required check");
        probe.OverrideChannel = "";
        var lease = adapter.TryAcquire(f);
        Check(lease?.Healthy == true && probe.Enabled && probe.Effective.Count == 5, "Rotation wasn't granted after verified overrides");
        probe.Effective[13] = false;
        Check(!lease!.Healthy, "Conflicting settings went unnoticed");
        lease.Dispose();
        Check(!probe.Enabled && probe.Effective.Count == 0 && probe.Releases == 1, "Release did not restore user's settings");
        probe.DenyOption = 13;
        Check(adapter.TryAcquire(f) is null && probe.Releases == 2 && !probe.Enabled && probe.Effective.Count == 0,
            "A partially acquired lease survived a denied safety override");
        probe.DenyOption = -1;
        lease = adapter.TryAcquire(f);
        probe.ThrowOnRelease = true;
        lease!.Dispose();
        Check(!probe.Enabled && adapter.Unavailable(f)!.Contains("release") && adapter.TryAcquire(f) is null,
            "A failed release left leased rotation on or allowed overlapping grants");
        probe.ThrowOnRelease = false;
        adapter.RetryRelease(DateTime.UtcNow.AddSeconds(2));
        Check(adapter.Unavailable(f) is null && probe.Effective.Count == 0, "Failed release was never retried");
        lease = adapter.TryAcquire(f);
        probe.Cancel!(0, "user revoked");
        Check(!lease!.Healthy, "Lease cancellation didn't stop assist");
        lease.Dispose();
    }
}

public class AssistProbeInterface : DispatchProxy
{
    internal uint ExpectedOid;
    internal string OverrideChannel = "";
    internal object? OverrideValue;
    internal bool DidCheckModule, DidReadMovement, AttemptedMutation;
    internal bool Enabled, Seeded, ThrowOnRelease;
    internal int DenyOption = -1, Releases;
    internal readonly Dictionary<int, object> Effective = [];
    internal Action<int,string>? Cancel;
    private readonly Guid id = Guid.NewGuid();
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name is not ("GetIpcSubscriber" or "GetIpcProvider")) throw new InvalidOperationException();
        var channel = (string)args![0]!;
        var sub = Create(method.ReturnType, typeof(AssistProbeChannel));
        ((AssistProbeChannel)sub).Read = (name, a) =>
        {
            if (name is "get_HasFunction" or "get_HasAction") return true;
            if (channel.EndsWith(".WrathComboCallback", StringComparison.Ordinal))
            {
                AttemptedMutation = true;
                if (name == "RegisterAction") Cancel = (Action<int,string>)a![0]!;
                else if (name == "UnregisterAction") Cancel = null;
                else throw new Exception("unexpected callback operation");
                return null;
            }
            if (name == "InvokeAction" && channel == "WrathCombo.ReleaseControl" && Equals(a![0], id))
            {
                if (ThrowOnRelease) throw new InvalidOperationException("release temporarily unavailable");
                Enabled = false; Seeded = false; Effective.Clear(); Releases++; Cancel?.Invoke(1, "released"); return null;
            }
            if (name != "InvokeFunc") throw new Exception("Unexpected IPC operation");
            switch (channel)
            {
                case "BossMod.HasModuleByDataId":
                    if (a is not [uint oid] || oid != ExpectedOid) throw new Exception("Wrong live OID");
                    DidCheckModule = true; break;
                case "BossMod.Configuration":
                    if (a is not [IReadOnlyList<string> tokens, false] ||
                        !tokens.SequenceEqual(new[] { "AIConfig", "ForbidMovement" })) throw new Exception("Not a read-only movement query");
                    DidReadMovement = true; break;
                case "WrathCombo.GetAutoRotationState": return channel == OverrideChannel ? OverrideValue : Enabled;
                case "WrathCombo.RegisterForLeaseWithCallback":
                    AttemptedMutation = true;
                    if (Cancel is null || a is not ["SRankSentinel","S Rank Sentinel",string]) throw new Exception("bad registration");
                    return (Guid?)id;
                case "WrathCombo.SetAutoRotationConfigState":
                    if (a is not [Guid owner, int key, object value] || owner != id || Enabled || !Seeded) throw new Exception("bad override");
                    if (key == DenyOption) return 15;
                    Effective[key] = value; return 0;
                case "WrathCombo.GetAutoRotationConfigState": return Effective[(int)a![0]!];
                case "WrathCombo.SetAutoRotationState":
                    if (a is [Guid seedId, false] && seedId == id) { Seeded = true; Enabled = false; return 0; }
                    if (a is not [Guid ownerId, true] || ownerId != id || Effective.Count != 5 ||
                        !Equals(Effective[0],true) || !Equals(Effective[1],0) || !Equals(Effective[13],true) ||
                        !Equals(Effective[21],false) || !Equals(Effective[22],false)) throw new Exception("early enable");
                    Enabled = true; return 0;
                case "WrathCombo.IPCReady": case "WrathCombo.IsCurrentJobAutoRotationReady": case "BossMod.AI.IsEnabled": break;
                default: AttemptedMutation = true; throw new Exception("Unexpected IPC: " + channel);
            }
            return channel == OverrideChannel ? OverrideValue : channel == "BossMod.Configuration" ? new List<string>{"False"} : true;
        };
        return sub;
    }
}
public class AssistProbeChannel : DispatchProxy
{
    internal Func<string, object?[]?, object?> Read = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Read(method!.Name,args);
}
