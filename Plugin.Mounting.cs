using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using NativePlayerState = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState;

namespace SRankSentinel;

public sealed partial class Plugin
{
    private MountChoices? mountChoices;

    private void DrawPreferredMount()
    {
        var owner = CharacterContentId;
        var choices = mountChoices ??= new();
        choices.Refresh(owner, DateTime.UtcNow, ReadUnlockedMounts);
        var preferred = config.GetPreferredMount(owner);
        var selected = choices.DisplaySelection(preferred, config.PreferredMounts?.ContainsKey(owner) == true);
        var preview = owner == 0 ? "Waiting for character" : selected.Name;

        ImGui.BeginDisabled(owner == 0);
        try
        {
            if (!ImGui.BeginCombo("Preferred Mount", preview)) return;
            try
            {
                foreach (var mount in choices.Options)
                {
                    if (ImGui.Selectable($"{mount.Name}###mount{mount.Id}", mount.Id == selected.Id) &&
                        owner != 0 && owner == CharacterContentId)
                        config.SavePreferredMount(owner, mount.Id);
                    if (mount.Id == selected.Id) ImGui.SetItemDefaultFocus();
                }
            }
            finally { ImGui.EndCombo(); }
        }
        finally { ImGui.EndDisabled(); }
    }

    private unsafe IEnumerable<MountOption>? ReadUnlockedMounts()
    {
        try
        {
            var sheet = data.GetExcelSheet<Mount>();
            var player = NativePlayerState.Instance();
            if (player == null) return null;
            var unlocked = new List<MountOption>();
            foreach (var row in sheet)
            {
                if (row.RowId == 0 || !player->IsMountUnlocked(row.RowId)) continue;
                var name = row.Singular.ExtractText().Trim();
                if (name.Length != 0) unlocked.Add(new(row.RowId, name));
            }
            return unlocked;
        }
        catch (Exception ex)
        {
            if (mountChoices?.DataAvailable == true) log.Debug(ex, "Mount list currently unavailable");
            return null;
        }
    }

    private unsafe string? UnlockedMountName(uint id)
    {
        try
        {
            if (CharacterContentId == 0 || !data.GetExcelSheet<Mount>().TryGetRow(id, out var mount))
                return null;
            var player = NativePlayerState.Instance();
            if (player == null || !player->IsMountUnlocked(id)) return null;
            var name = mount.Singular.ExtractText().Trim();
            return name.Length == 0 ? null : name;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Cannot resolve preferred mount {MountId}", id);
            return null;
        }
    }

    private bool MountSummonInProgress() => condition[ConditionFlag.Mounting] || condition[ConditionFlag.Mounting71] ||
        objects.LocalPlayer is { IsCasting: true, CastActionType: (byte)ActionType.Mount };

    private static unsafe bool RequestAvailableMount(uint mountId)
    {
        var manager = ActionManager.Instance();
        return manager != null && manager->GetActionStatus(ActionType.Mount, mountId) == 0 &&
               manager->UseAction(ActionType.Mount, mountId);
    }

    private bool TryRequestSentinelMount(out string requestedMount, out string diagnostic, bool allowPreferredMount)
    {
        var preference = config.GetPreferredMount(CharacterContentId);
        var request = HuntProgressPolicy.SelectMount(preference, allowPreferredMount,
            UnlockedMountName, RequestAvailableMount, () => UseGeneralAction(9));
        requestedMount = request.ActionName;
        diagnostic = request.Reason;
        log.Information("Mount request for {Mark}: preference ID={Preference}, action={Mount}, accepted={Accepted}, reason={Reason}",
            current?.CreatureName ?? "active hunt", preference, requestedMount, request.Accepted, diagnostic);
        return request.Accepted;
    }
}
