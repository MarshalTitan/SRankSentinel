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
            ("BossMod.Configuration", new List<string>{"False"}, CombatAssistIpc.MissingGuard),
        })
        {
            probe.ExpectedOid = f.BaseId;
            probe.OverrideChannel = channel;
            probe.OverrideValue = value;
            Check(adapter.Unavailable(f)?.Contains(reason) == true, "Wrong fallback reason: " + reason);
            Check(adapter.TryAcquire(f) is null, "Installed Wrath must never be granted unsafe rotation");
        }
        Check(probe.DidCheckModule && probe.DidReadMovement && !probe.AttemptedMutation, "Probe wrote config or missed a required check");
    }
}

public class AssistProbeInterface : DispatchProxy
{
    internal uint ExpectedOid;
    internal string OverrideChannel = "";
    internal object? OverrideValue;
    internal bool DidCheckModule, DidReadMovement, AttemptedMutation;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name != "GetIpcSubscriber") { AttemptedMutation = true; throw new InvalidOperationException(); }
        var channel = (string)args![0]!;
        var sub = Create(method.ReturnType, typeof(AssistProbeChannel));
        ((AssistProbeChannel)sub).Read = (name, a) =>
        {
            if (name == "get_HasFunction") return true;
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
                case "WrathCombo.IPCReady": case "WrathCombo.IsCurrentJobAutoRotationReady": case "BossMod.AI.IsEnabled": break;
                default: AttemptedMutation = true; throw new Exception("Unexpected IPC: " + channel);
            }
            return channel == OverrideChannel ? OverrideValue : true;
        };
        return sub;
    }
}
public class AssistProbeChannel : DispatchProxy
{
    internal Func<string, object?[]?, object?> Read = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Read(method!.Name,args);
}
