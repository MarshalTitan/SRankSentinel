using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace SRankSentinel;

internal sealed partial class NativeTravel
{
    private const string WorldVisitStatusAddon = "WorldTravelFinderStatus";
    // Patch 7.58 worldtranslatestatus.uld and the bundled ECommons status helper agree on this button.
    private const uint WorldVisitCancelNodeId = 13;

    public string ReadVisitWorldName(uint id) => VisitWorlds.TryGetValue(id, out var world) ? world.Name : string.Empty;

    public unsafe WorldVisitQueueStatus ReadWorldVisitQueue(string source, string destination)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(WorldVisitStatusAddon);
        return ReadWorldVisitQueue(addon, source, destination, out _);
    }

    public unsafe bool TryCancelWorldVisitQueue(string source, string destination)
    {
        if (IsBetweenAreas || !condition[ConditionFlag.WaitingToVisitOtherWorld] ||
            !CurrentWorld.Equals(source, StringComparison.OrdinalIgnoreCase))
            return false;

        var addon = gameGui.GetAddonByName<AtkUnitBase>(WorldVisitStatusAddon);
        var queue = ReadWorldVisitQueue(addon, source, destination, out var click);
        if (!queue.CancelEnabled || click is null)
            return false;

        // Dispatch the actual enabled button's registered event to its owning addon.
        // No hard-coded callback or event parameter, and no generic dialog interaction.
        addon->ReceiveEvent(click->State.EventType, (int)click->Param, click, null);
        return true;
    }

    private static unsafe WorldVisitQueueStatus ReadWorldVisitQueue(AtkUnitBase* addon,
        string source, string destination, out AtkEvent* click)
    {
        click = null;
        if (addon is null || !addon->IsVisible)
            return new(false, false, false, string.Empty, string.Empty);
        if (!IsReady(addon))
            return new(true, false, false, string.Empty, string.Empty);

        // The two top-row text nodes flank the arrow in worldtranslatestatus.uld.
        var from = ReadWorldVisitLabel(addon->GetTextNodeById(2));
        var to = ReadWorldVisitLabel(addon->GetTextNodeById(4));
        var matches = source.Length > 0 && destination.Length > 0 &&
            from.Equals(source, StringComparison.OrdinalIgnoreCase) &&
            to.Equals(destination, StringComparison.OrdinalIgnoreCase);
        if (matches)
        {
            var owner = addon->GetComponentNodeById(WorldVisitCancelNodeId);
            var button = addon->GetComponentButtonById(WorldVisitCancelNodeId);
            if (owner is not null && button is not null && button->OwnerNode == owner &&
                button->GetComponentType() == ComponentType.Button && button->IsEnabled &&
                button->AtkResNode is not null && button->AtkResNode->IsVisible() && owner->AtkResNode.IsVisible())
            {
                var evt = owner->AtkResNode.AtkEventManager.Event;
                for (var i = 0; evt is not null && i < 16; i++, evt = evt->NextEvent)
                {
                    if (evt->State.EventType == AtkEventType.ButtonClick && evt->Listener == &addon->AtkEventListener)
                    {
                        click = evt;
                        break;
                    }
                }
            }
        }

        return new(true, matches, click is not null, from, to);
    }

    private static unsafe string ReadWorldVisitLabel(AtkTextNode* node) =>
        node is null ? string.Empty : node->NodeText.ToString().Trim();
}
