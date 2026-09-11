using Dalamud.Game.Chat;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using SentinelHunts.Core;
using SentinelHunts.Services;

namespace SentinelHunts.Sources;

internal sealed class SonarSource : IDisposable
{
    private readonly IChatGui chat;
    private readonly GameState game;
    private readonly IPluginLog log;

    public SonarSource(IChatGui chat, GameState game, IPluginLog log)
    {
        this.chat = chat;
        this.game = game;
        this.log = log;
        chat.ChatMessage += OnChatMessage;
    }

    public event Action<HuntEvent>? EventReceived;
    public bool Enabled { get; set; } = true;
    public string Status { get; private set; } = "Listening for structured Sonar map links";

    public void Dispose() => chat.ChatMessage -= OnChatMessage;

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (!Enabled || !message.Sender.TextValue.Equals("Sonar", StringComparison.Ordinal))
            return;

        try
        {
            var mapLink = message.Message.Payloads.OfType<MapLinkPayload>().FirstOrDefault();
            if (mapLink is null)
                return;

            var territoryId = mapLink.TerritoryType.RowId;
            var text = message.Message.TextValue;
            if (!text.Contains("Rank S", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("Rank SS", StringComparison.OrdinalIgnoreCase))
                return;

            var definition = HuntCatalog.FindByTerritory(territoryId);
            if (definition is null)
                return;

            var world = game.ResolveWorld(message.Message.TextValue);
            if (world is null)
            {
                Status = $"Ignored {definition.Name}: no valid world in Sonar message";
                return;
            }

            var killed = text.Contains("was just killed", StringComparison.OrdinalIgnoreCase) ||
                         text.Contains("has been defeated", StringComparison.OrdinalIgnoreCase);
            var huntEvent = new HuntEvent(
                killed ? HuntEventKind.Killed : HuntEventKind.Reported,
                HuntSource.Sonar,
                null,
                world.Value.Id,
                world.Value.Name,
                territoryId,
                ParseInstance(text),
                definition.DataId,
                definition.Name,
                mapLink.XCoord,
                mapLink.YCoord,
                DateTimeOffset.UtcNow);
            EventReceived?.Invoke(huntEvent);
            Status = $"Last event: {definition.Name} on {world.Value.Name}";
        }
        catch (Exception exception)
        {
            Status = "Last Sonar message could not be parsed";
            log.Warning(exception, "Sentinel Hunts could not parse a Sonar message");
        }
    }

    private static byte ParseInstance(string text)
    {
        for (byte instance = 1; instance <= 9; instance++)
            if (text.Contains((char)(0xE0B0 + instance)))
                return instance;
        return 1;
    }
}
