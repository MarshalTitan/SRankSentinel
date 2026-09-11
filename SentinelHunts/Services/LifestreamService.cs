using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace SentinelHunts.Services;

internal sealed class LifestreamService
{
    public const uint UldahAetheryteId = 9;
    public const uint UldahTerritoryId = 130;

    private readonly ICallGateSubscriber<bool> isBusy;
    private readonly ICallGateSubscriber<string, bool> changeWorld;
    private readonly ICallGateSubscriber<uint, byte, bool> teleport;
    private readonly ICallGateSubscriber<int, object> changeInstance;

    public LifestreamService(IDalamudPluginInterface pi)
    {
        isBusy = pi.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        changeWorld = pi.GetIpcSubscriber<string, bool>("Lifestream.ChangeWorld");
        teleport = pi.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport");
        changeInstance = pi.GetIpcSubscriber<int, object>("Lifestream.ChangeInstance");
    }

    public bool IsBusy
    {
        get
        {
            try { return isBusy.InvokeFunc(); }
            catch { return false; }
        }
    }

    public string Activity
    {
        get
        {
            try { return isBusy.InvokeFunc() ? "busy" : "ready (idle)"; }
            catch { return "unavailable"; }
        }
    }

    public bool ChangeWorld(string world)
    {
        try { return changeWorld.InvokeFunc(world); }
        catch { return false; }
    }

    public bool Teleport(uint aetheryteId)
    {
        try { return teleport.InvokeFunc(aetheryteId, 0); }
        catch { return false; }
    }

    public bool ChangeInstance(byte instance)
    {
        try
        {
            changeInstance.InvokeAction(instance);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
