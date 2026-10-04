using System.Reflection;
using System.Text.Json;
using SRankSentinel;

// Capture the real persistence call without requiring a running game or plugin services.
public class RecordingPluginInterface : DispatchProxy
{
    public int SaveCalls { get; private set; }
    public string? SavedJson { get; private set; }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name != "SavePluginConfig")
            throw new NotSupportedException($"Unexpected plugin-interface call: {method?.Name}");

        SavedJson = JsonSerializer.Serialize((Configuration)args![0]!);
        SaveCalls++;
        return null;
    }
}
