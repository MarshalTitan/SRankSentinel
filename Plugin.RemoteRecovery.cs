using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace SRankSentinel;

public sealed partial class Plugin
{
    private ICallGateProvider<string, string>? remoteRecoveryStatus;
    private ICallGateProvider<string, string>? remoteRecoverySkip;
    private ICallGateProvider<string, string>? remoteRecoveryReset;
    private bool remoteRecoveryDisposed;

    private void RegisterRemoteRecovery(IPlayerState playerState)
    {
        var controller = new RemoteRecoveryController(
            () => ReadRemoteRecoveryStatus(playerState),
            SkipCurrentKeepQueue, StopAndResetThroughUldah);
        try
        {
            remoteRecoveryStatus = pi.GetIpcProvider<string, string>("SRankSentinel.Remote.v1.Status");
            remoteRecoverySkip = pi.GetIpcProvider<string, string>("SRankSentinel.Remote.v1.SkipCurrentKeepQueue");
            remoteRecoveryReset = pi.GetIpcProvider<string, string>("SRankSentinel.Remote.v1.StopResetThroughUldah");
            remoteRecoveryStatus.RegisterFunc(key => controller.Handle(RemoteRecoveryOperation.Status, key, framework.IsInFrameworkUpdateThread));
            remoteRecoverySkip.RegisterFunc(key => controller.Handle(RemoteRecoveryOperation.Skip, key, framework.IsInFrameworkUpdateThread));
            remoteRecoveryReset.RegisterFunc(key => controller.Handle(RemoteRecoveryOperation.Reset, key, framework.IsInFrameworkUpdateThread));
        }
        catch
        {
            DisposeRemoteRecovery();
            log.Warning("Remote recovery IPC registration unavailable; existing local recovery controls remain available.");
        }
    }

    private void DisposeRemoteRecovery()
    {
        remoteRecoveryDisposed = true;
        try { remoteRecoveryStatus?.UnregisterFunc(); } catch { }
        try { remoteRecoverySkip?.UnregisterFunc(); } catch { }
        try { remoteRecoveryReset?.UnregisterFunc(); } catch { }
    }

    private RecoverySnapshot ReadRemoteRecoveryStatus(IPlayerState playerState)
    {
        var loaded = !remoteRecoveryDisposed;
        var loggedIn = playerState.IsLoaded && playerState.ContentId != 0;
        return new(
            loggedIn ? $"cid:{playerState.ContentId:X16}" : string.Empty,
            loggedIn ? RemoteRecoveryController.Bound(playerState.CharacterName, 80) : string.Empty,
            loaded, config.Enabled,
            loggedIn && !persistentQueueRestorePending && objects.LocalPlayer is not null &&
                !condition[ConditionFlag.BetweenAreas] && !condition[ConditionFlag.BetweenAreas51],
            state.ToString(), RemoteRecoveryController.Bound(status, 300),
            clientState.TerritoryType,
            current is null ? null : RemoteRecoveryController.Bound(current.CreatureName, 80),
            pendingAlerts.Count, state == SentinelState.ResetToUldah);
    }

    // Exact existing UI recovery semantics. Both UI and versioned IPC call these methods.
    private void SkipCurrentKeepQueue() =>
        FailCurrent("Current hunt skipped manually", HuntExitRequestSource.ManualSkip);

    private void StopAndResetThroughUldah()
    {
        pendingAlerts.Clear();
        PersistQueue();
        if (current is null)
            SetState(SentinelState.ResetToUldah,
                "Manual reset requested",
                HuntExitRequestSource.ManualSkip);
        else
            FailCurrent("Stopped manually", HuntExitRequestSource.ManualSkip);
    }
}
