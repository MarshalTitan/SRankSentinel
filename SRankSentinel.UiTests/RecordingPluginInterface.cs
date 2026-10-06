using System.Reflection;
using System.Text.Json;
using SRankSentinel;
using Dalamud.Interface;
using Dalamud.Bindings.ImGui;

// Capture the real persistence call without requiring a running game or plugin services.
public class RecordingPluginInterface : DispatchProxy
{
    public int SaveCalls { get; private set; }
    public string? SavedJson { get; private set; }
    public bool ReducedMotion { get; set; }
    public uint LastSaveItemId { get; private set; }
    public bool FocusNextAfterSave { get; set; }

    protected override unsafe object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name == "get_UiBuilder")
        {
            var builder = Create<IUiBuilder, RecordingUiBuilder>();
            ((RecordingUiBuilder)builder).ReducedMotion = ReducedMotion;
            return builder;
        }
        if (method?.Name != "SavePluginConfig")
            throw new NotSupportedException($"Unexpected plugin-interface call: {method?.Name}");

        SavedJson = JsonSerializer.Serialize((Configuration)args![0]!);
        if (ImGui.GetCurrentContext().Handle != null)
            LastSaveItemId = ImGui.GetCurrentContext().LastItemData.ID;
        SaveCalls++;
        if (FocusNextAfterSave) ImGui.SetKeyboardFocusHere();
        return null;
    }
}

public class RecordingUiBuilder : DispatchProxy
{
    public bool ReducedMotion { get; set; }
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name == "get_ShouldUseReducedMotion"
        ? ReducedMotion : throw new NotSupportedException($"Unexpected UI-builder call: {method?.Name}");
}
