using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using SentinelHunts.Core;
using SentinelHunts.Services;
using System.Globalization;

namespace SentinelHunts.Sources;

internal sealed class HuntAlertsSource : IDisposable
{
    private readonly ICallGateSubscriber<HuntTrainMessageDto, object> subscriber;
    private readonly GameState game;
    private readonly IPluginLog log;
    private bool configuredEnabled = true;
    private bool schemaCompatible = true;

    public HuntAlertsSource(IDalamudPluginInterface pi, GameState game, IPluginLog log)
    {
        this.game = game;
        this.log = log;
        subscriber = pi.GetIpcSubscriber<HuntTrainMessageDto, object>("HuntAlerts.OnHuntTrainMessageReceived");
        try
        {
            subscriber.Subscribe(OnMessage);
            Status = "Subscribed to HuntAlerts IPC";
        }
        catch (Exception exception)
        {
            Status = "HuntAlerts IPC unavailable";
            log.Debug(exception, "Sentinel Hunts could not subscribe to HuntAlerts IPC");
        }
    }

    public event Action<HuntEvent>? EventReceived;
    public bool Enabled
    {
        get => configuredEnabled && schemaCompatible;
        set => configuredEnabled = value;
    }
    public string Status { get; private set; }

    public void Dispose()
    {
        try { subscriber.Unsubscribe(OnMessage); }
        catch { }
    }

    private void OnMessage(HuntTrainMessageDto payload)
    {
        if (!Enabled || payload is null)
            return;

        try
        {
            var killed = payload.huntType.Contains("kill", StringComparison.OrdinalIgnoreCase) ||
                         payload.huntType.Contains("dead", StringComparison.OrdinalIgnoreCase);
            var isSRank = payload.huntType.Equals("srank", StringComparison.OrdinalIgnoreCase) ||
                          payload.huntType.Contains("s_rank", StringComparison.OrdinalIgnoreCase);
            if (!killed && !isSRank)
                return;

            var territoryId = payload.startTerritoryTypeId != 0
                ? payload.startTerritoryTypeId
                : game.ResolveTerritoryFromAetheryteName(payload.startLocation);
            var definition = HuntCatalog.Resolve(territoryId, payload.creatureName);
            if (definition is null)
                return;

            var hasCoordinates = TryCoordinates(payload, out var mapX, out var mapY);
            if (!killed && !hasCoordinates)
                return;

            var world = game.ResolveWorld(payload.huntWorld);
            if (world is null)
            {
                Status = $"Ignored {definition.Name}: HuntAlerts world was invalid";
                return;
            }

            var instance = (byte)Math.Clamp(payload.instance, 1, 9);
            EventReceived?.Invoke(new HuntEvent(
                killed ? HuntEventKind.Killed : HuntEventKind.Reported,
                HuntSource.HuntAlerts,
                payload.reportId,
                world.Value.Id,
                world.Value.Name,
                territoryId,
                instance,
                definition.DataId,
                definition.Name,
                mapX,
                mapY,
                DateTimeOffset.UtcNow));
            Status = $"Last event: {definition.Name} on {world.Value.Name}";
        }
        catch (Exception exception)
        {
            Status = "HuntAlerts schema mismatch; adapter paused";
            schemaCompatible = false;
            log.Warning(exception, "Sentinel Hunts disabled HuntAlerts after an incompatible IPC payload");
        }
    }

    private static bool TryCoordinates(HuntTrainMessageDto payload, out float mapX, out float mapY)
    {
        mapX = payload.mapLocationX;
        mapY = payload.mapLocationY;
        if (mapX is > 0 and <= 50 && mapY is > 0 and <= 50)
            return true;

        var pieces = payload.locationCoords.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return pieces.Length >= 2 &&
               float.TryParse(pieces[0], NumberStyles.Float, CultureInfo.InvariantCulture, out mapX) &&
               float.TryParse(pieces[1], NumberStyles.Float, CultureInfo.InvariantCulture, out mapY) &&
               mapX is > 0 and <= 50 && mapY is > 0 and <= 50;
    }
}

// Includes the current public fields plus the legacy fields used by earlier HuntAlerts builds.
// Keeping schema handling inside this adapter prevents IPC drift from leaking into orchestration.
internal sealed class HuntTrainMessageDto
{
    public string huntType { get; set; } = string.Empty;
    public string huntWorld { get; set; } = string.Empty;
    public string creatureName { get; set; } = string.Empty;
    public string startLocation { get; set; } = string.Empty;
    public string locationCoords { get; set; } = string.Empty;
    public string reportId { get; set; } = string.Empty;
    public uint startTerritoryTypeId { get; set; }
    public int instance { get; set; } = 1;
    public float mapLocationX { get; set; }
    public float mapLocationY { get; set; }
}
