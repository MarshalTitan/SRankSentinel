using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace SRankSentinel;

internal sealed class LifestreamIpc(IDalamudPluginInterface pi, IPluginLog log)
{
    private readonly ICallGateSubscriber<bool> isBusy = pi.GetIpcSubscriber<bool>("Lifestream.IsBusy");
    private readonly ICallGateSubscriber<string, bool> changeWorld =
        pi.GetIpcSubscriber<string, bool>("Lifestream.ChangeWorld");

    private readonly ICallGateSubscriber<object> abort = pi.GetIpcSubscriber<object>("Lifestream.Abort");

    public bool HasRequiredIpc => isBusy.HasFunction && changeWorld.HasFunction;

    public LifestreamReadiness ReadReadiness()
    {
        if (!HasRequiredIpc)
            return LifestreamReadiness.Unavailable;
        try { return isBusy.InvokeFunc() ? LifestreamReadiness.Busy : LifestreamReadiness.Idle; }
        catch { return LifestreamReadiness.Unavailable; }
    }

    public bool TryAbort()
    {
        if (!abort.HasAction)
            return false;
        try
        {
            abort.InvokeAction();
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Lifestream.Abort IPC failed");
            return false;
        }
    }

    public bool TryChangeWorld(string world)
    {
        try { return changeWorld.InvokeFunc(world); }
        catch (Exception ex)
        {
            log.Warning(ex, "Lifestream.ChangeWorld IPC failed for {World}", world);
            return false;
        }
    }
}
