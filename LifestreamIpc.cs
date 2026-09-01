using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace SRankSentinel;

internal sealed class LifestreamIpc
{
    private const uint UldahAetheryteId = 9;

    private readonly ICallGateSubscriber<bool> isBusy;
    private readonly ICallGateSubscriber<string, bool> changeWorld;
    private readonly ICallGateSubscriber<uint, byte, bool> teleport;

    public LifestreamIpc(IDalamudPluginInterface pi)
    {
        isBusy = pi.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        changeWorld = pi.GetIpcSubscriber<string, bool>("Lifestream.ChangeWorld");
        teleport = pi.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport");
    }

    public bool IsBusySafe()
    {
        try { return isBusy.InvokeFunc(); }
        catch { return false; }
    }

    public bool TeleportToUldahSafe()
        => TeleportSafe(UldahAetheryteId);

    public bool ChangeWorldSafe(string world)
    {
        try { return changeWorld.InvokeFunc(world); }
        catch { return false; }
    }

    public bool TeleportSafe(uint aetheryteId)
    {
        try { return teleport.InvokeFunc(aetheryteId, 0); }
        catch { return false; }
    }
}
