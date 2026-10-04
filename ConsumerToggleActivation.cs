using Dalamud.Bindings.ImGui;
using SentinelCore.UI;
using System.Numerics;

namespace SRankSentinel;

// Core 0.2.1 draws a non-navigable InvisibleButton. Keep its canonical visuals and
// pointer handling, then overlay only an invisible native navigation target in the same rect.
internal static class ConsumerToggleActivation
{
    public static bool Draw(string id, string label, ref bool value, float scale)
    {
        var pointerChanged = SentinelModernControls.Toggle(id, label, ref value, scale);
        var minimum = ImGui.GetItemRectMin();
        var size = ImGui.GetItemRectSize();
        var nextCursor = ImGui.GetCursorScreenPos();
        ImGui.SetItemAllowOverlap();
        ImGui.SetCursorScreenPos(minimum);
        ImGui.PushID("SRankSentinel-Activation");
        ImGui.PushID(id);
        bool navigationPressed;
        // Native Button supplies navigation activation and its normal focus cursor only.
        // No track, knob, palette, spacing, or switch drawing is implemented here.
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Vector4.Zero);
        try
        {
            navigationPressed = ImGui.Button("##Activation", size);
        }
        finally
        {
            ImGui.PopStyleColor(3);
            ImGui.PopID();
            ImGui.PopID();
            ImGui.SetCursorScreenPos(nextCursor);
        }

        // Pointer click/release remains owned by Core; don't toggle a second time on release.
        var navigationChanged = navigationPressed && !pointerChanged
            && !ImGui.IsMouseClicked(ImGuiMouseButton.Left)
            && !ImGui.IsMouseReleased(ImGuiMouseButton.Left);
        if (navigationChanged)
            value = !value;
        return pointerChanged || navigationChanged;
    }
}
