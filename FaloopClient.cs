using Dalamud.Plugin.Services;
using System.Globalization;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace SRankSentinel;

internal enum FaloopEventAction
{
    Spawn,
    Death,
}

internal sealed record FaloopFeedEvent(
    FaloopEventAction Action,
    string MobSlug,
    string WorldSlug,
    string? ZoneSlug,
    int PoiId,
    string RawPoiKey,
    float DirectMapX,
    float DirectMapY,
    string? RawLocation,
    string RawCoordinateData,
    int Instance,
    DateTime OccurredAtUtc);

internal sealed record FaloopAuthenticationResult(bool Success, string SessionId, string Error)
{
    public static FaloopAuthenticationResult Failed(string error) => new(false, string.Empty, error);
    public static FaloopAuthenticationResult Authenticated(string sessionId) => new(true, sessionId, string.Empty);
}

/// <summary>
/// Minimal native Engine.IO v4 / Socket.IO client for Faloop's authenticated message feed.
/// Account passwords are used only by AuthenticateAsync and are never retained by this class.
/// </summary>
internal sealed class FaloopClient : IDisposable
{
    private static readonly Uri FeedUri =
        new("wss://faloop.app/comms/socket.io/?EIO=4&transport=websocket");

    private readonly IPluginLog log;
    private readonly object sync = new();
    private readonly Dictionary<string, DateTime> recentEvents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FaloopFeedEvent> spawnLocations = new(StringComparer.Ordinal);
    private CancellationTokenSource? runCancellation;
    private ClientWebSocket? activeSocket;
    private string status = "Disabled";
    private bool connected;
    private DateTime lastMessageUtc = DateTime.MinValue;
    private DateTime lastEventUtc = DateTime.MinValue;

    public FaloopClient(IPluginLog pluginLog) => log = pluginLog;

    public event Action<FaloopFeedEvent>? EventReceived;
    public event Action? SessionRejected;

    public bool IsConnected
    {
        get { lock (sync) return connected; }
    }

    public string Status
    {
        get { lock (sync) return status; }
    }

    public DateTime LastMessageUtc
    {
        get { lock (sync) return lastMessageUtc; }
    }

    public DateTime LastEventUtc
    {
        get { lock (sync) return lastEventUtc; }
    }

    public async Task<FaloopAuthenticationResult> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            return FaloopAuthenticationResult.Failed("Enter the Faloop username and password first.");

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://faloop.app");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://faloop.app/");
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "SRankSentinel/0.6");

            using var refresh = await client.PostAsJsonAsync(
                "https://faloop.app/api/auth/user/refresh",
                new Dictionary<string, object?> { ["sessionId"] = null },
                cancellationToken).ConfigureAwait(false);
            var refreshText = await refresh.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!refresh.IsSuccessStatusCode)
                return FaloopAuthenticationResult.Failed($"Faloop session request failed ({(int)refresh.StatusCode}).");

            using var refreshJson = JsonDocument.Parse(refreshText);
            if (!TryReadAuthData(refreshJson.RootElement, out var sessionId, out var token))
                return FaloopAuthenticationResult.Failed("Faloop did not return a usable session.");

            using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "https://faloop.app/api/auth/user/login")
            {
                Content = JsonContent.Create(new Dictionary<string, object?>
                {
                    ["username"] = username.Trim(),
                    ["password"] = password,
                    ["rememberMe"] = false,
                    ["sessionId"] = sessionId,
                }),
            };
            loginRequest.Headers.TryAddWithoutValidation("Authorization", token);
            loginRequest.Headers.TryAddWithoutValidation("Origin", "https://faloop.app");
            loginRequest.Headers.TryAddWithoutValidation("Referer", "https://faloop.app/login");
            loginRequest.Headers.TryAddWithoutValidation("User-Agent", "SRankSentinel/0.6");

            using var login = await client.SendAsync(loginRequest, cancellationToken).ConfigureAwait(false);
            var loginText = await login.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!login.IsSuccessStatusCode)
                return FaloopAuthenticationResult.Failed($"Faloop login failed ({(int)login.StatusCode}).");

            using var loginJson = JsonDocument.Parse(loginText);
            if (loginJson.RootElement.TryGetProperty("success", out var success) &&
                success.ValueKind == JsonValueKind.False)
                return FaloopAuthenticationResult.Failed("Faloop rejected the login.");

            return FaloopAuthenticationResult.Authenticated(sessionId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FaloopAuthenticationResult.Failed("Faloop login timed out.");
        }
        catch (Exception ex)
        {
            log.Warning("Faloop authentication failed: {Error}", ex.Message);
            return FaloopAuthenticationResult.Failed("Faloop authentication failed; see the plugin log for details.");
        }
    }

    public void Start(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            Stop("Enabled; authenticate to start the direct feed");
            return;
        }

        CancellationTokenSource cancellation;
        lock (sync)
        {
            runCancellation?.Cancel();
            runCancellation = cancellation = new CancellationTokenSource();
            connected = false;
            status = "Connecting to Faloop";
        }

        _ = Task.Run(() => RunReconnectLoopAsync(sessionId.Trim(), cancellation.Token));
    }

    public void Stop(string reason = "Disabled")
    {
        lock (sync)
        {
            runCancellation?.Cancel();
            activeSocket?.Abort();
            connected = false;
            status = reason;
        }
    }

    private async Task RunReconnectLoopAsync(string sessionId, CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAndListenAsync(sessionId, cancellationToken).ConfigureAwait(false);
                attempt = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (ex is UnauthorizedAccessException)
                {
                    SetStatus(false, "Faloop session expired; authenticate again");
                    log.Warning("Faloop rejected the saved authenticated session.");
                    try
                    {
                        SessionRejected?.Invoke();
                    }
                    catch (Exception callbackError)
                    {
                        log.Warning("Faloop session-rejection callback failed: {Error}", callbackError.Message);
                    }
                    break;
                }
                attempt++;
                var delay = Math.Min(60, 2 * (1 << Math.Min(attempt - 1, 5))) + Random.Shared.NextDouble();
                SetStatus(false, $"Faloop disconnected; retrying in {delay:0}s");
                log.Warning("Faloop feed disconnected (attempt {Attempt}): {Error}", attempt, ex.Message);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private async Task ConnectAndListenAsync(string sessionId, CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.SetRequestHeader("Origin", "https://faloop.app");
        socket.Options.SetRequestHeader("User-Agent", "SRankSentinel/0.6");
        lock (sync) activeSocket = socket;

        try
        {
            await socket.ConnectAsync(FeedUri, cancellationToken).ConfigureAwait(false);
            var openPacket = await ReceiveWithTimeoutAsync(socket, TimeSpan.FromSeconds(30), cancellationToken)
                .ConfigureAwait(false);
            if (!openPacket.StartsWith('0'))
                throw new InvalidDataException("Faloop did not send an Engine.IO open packet.");

            var heartbeatTimeout = ParseHeartbeatTimeout(openPacket);
            await SendAsync(socket, "40" + JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["sessionid"] = sessionId,
            }), cancellationToken).ConfigureAwait(false);

            var socketConnected = false;
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var packetGroup = await ReceiveWithTimeoutAsync(socket, heartbeatTimeout, cancellationToken)
                    .ConfigureAwait(false);
                lock (sync) lastMessageUtc = DateTime.UtcNow;

                foreach (var packet in packetGroup.Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (packet == "2")
                    {
                        await SendAsync(socket, "3", cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (packet.StartsWith("40", StringComparison.Ordinal))
                    {
                        if (!socketConnected)
                        {
                            socketConnected = true;
                            SetStatus(true, "Connected to Faloop authenticated feed");
                            await SendAsync(socket, "42[\"ack\"]", cancellationToken).ConfigureAwait(false);
                        }
                        continue;
                    }

                    if (packet.StartsWith("44", StringComparison.Ordinal))
                        throw new UnauthorizedAccessException("Faloop rejected the saved session; authenticate again.");
                    if (packet.StartsWith("41", StringComparison.Ordinal))
                        throw new WebSocketException("Faloop closed the Socket.IO session.");
                    if (packet.StartsWith("42", StringComparison.Ordinal) && socketConnected)
                        ProcessSocketEvent(packet[2..]);
                }
            }

            throw new WebSocketException("Faloop WebSocket closed.");
        }
        finally
        {
            lock (sync)
            {
                if (ReferenceEquals(activeSocket, socket))
                {
                    activeSocket = null;
                    connected = false;
                }
            }
        }
    }

    private void ProcessSocketEvent(string json)
    {
        try
        {
            using var eventJson = JsonDocument.Parse(json);
            var root = eventJson.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2 ||
                !string.Equals(root[0].GetString(), "message", StringComparison.Ordinal))
                return;

            var payload = root[1];
            if (payload.ValueKind == JsonValueKind.String)
            {
                var nestedText = payload.GetString();
                if (string.IsNullOrWhiteSpace(nestedText))
                    return;
                using var nested = JsonDocument.Parse(nestedText);
                if (TryParseFeedEvent(nested.RootElement, out var nestedEvent))
                    Publish(nestedEvent);
                return;
            }

            if (TryParseFeedEvent(payload, out var feedEvent))
                Publish(feedEvent);
        }
        catch (JsonException ex)
        {
            log.Debug("Ignored malformed Faloop message: {Error}", ex.Message);
        }
    }

    private void Publish(FaloopFeedEvent feedEvent)
    {
        feedEvent = FillMissingDeathLocation(feedEvent);
        if (feedEvent.Action == FaloopEventAction.Spawn)
            RememberSpawn(feedEvent);

        var now = DateTime.UtcNow;
        var fingerprint = $"{feedEvent.Action}|{feedEvent.WorldSlug}|{feedEvent.MobSlug}|" +
                          $"{feedEvent.ZoneSlug}|{feedEvent.Instance}|{feedEvent.PoiId}|" +
                          $"{feedEvent.OccurredAtUtc:O}";
        lock (sync)
        {
            foreach (var expired in recentEvents.Where(pair => now - pair.Value > TimeSpan.FromMinutes(15))
                         .Select(pair => pair.Key).ToArray())
                recentEvents.Remove(expired);
            if (recentEvents.ContainsKey(fingerprint))
                return;
            recentEvents[fingerprint] = now;
            lastEventUtc = now;
        }

        EventReceived?.Invoke(feedEvent);
    }

    private void RememberSpawn(FaloopFeedEvent feedEvent)
    {
        lock (sync)
        {
            spawnLocations[SpawnKey(feedEvent.WorldSlug, feedEvent.MobSlug, feedEvent.Instance)] = feedEvent;
            spawnLocations[SpawnKey(feedEvent.WorldSlug, feedEvent.MobSlug, 0)] = feedEvent;
        }
    }

    private FaloopFeedEvent FillMissingDeathLocation(FaloopFeedEvent feedEvent)
    {
        if (feedEvent.Action != FaloopEventAction.Death || !string.IsNullOrWhiteSpace(feedEvent.ZoneSlug))
            return feedEvent;
        lock (sync)
        {
            if (!spawnLocations.TryGetValue(SpawnKey(feedEvent.WorldSlug, feedEvent.MobSlug, feedEvent.Instance),
                    out var spawn) &&
                !spawnLocations.TryGetValue(SpawnKey(feedEvent.WorldSlug, feedEvent.MobSlug, 0), out spawn))
                return feedEvent;
            return feedEvent with
            {
                ZoneSlug = spawn.ZoneSlug,
                PoiId = spawn.PoiId,
                RawPoiKey = spawn.RawPoiKey,
                DirectMapX = spawn.DirectMapX,
                DirectMapY = spawn.DirectMapY,
                RawLocation = spawn.RawLocation,
                RawCoordinateData = spawn.RawCoordinateData,
                Instance = feedEvent.Instance > 0 ? feedEvent.Instance : spawn.Instance,
            };
        }
    }

    private static string SpawnKey(string world, string mob, int instance) =>
        $"{world.Trim().ToUpperInvariant()}|{mob.Trim().ToUpperInvariant()}|{instance}";

    private static bool TryParseFeedEvent(JsonElement root, out FaloopFeedEvent feedEvent)
    {
        feedEvent = null!;
        if (root.ValueKind != JsonValueKind.Object || !TryString(root, "type", out var type) ||
            !root.TryGetProperty("data", out var eventData) || eventData.ValueKind != JsonValueKind.Object)
            return false;

        FaloopEventAction? action = type switch
        {
            "mobworldspawn" => FaloopEventAction.Spawn,
            "mobworldkill" => FaloopEventAction.Death,
            _ => null,
        };
        if (action is null && type == "mob" && TryString(eventData, "action", out var actionText))
            action = actionText switch
            {
                "spawn" => FaloopEventAction.Spawn,
                "death" => FaloopEventAction.Death,
                _ => null,
            };
        if (action is null)
            return false;

        var mob = string.Empty;
        var world = string.Empty;
        if (eventData.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Object)
        {
            TryString(id, "mobId", out mob);
            TryString(id, "worldId", out world);
        }
        if (string.IsNullOrWhiteSpace(mob))
            mob = FirstString(eventData, "mobId2", "mobId");
        if (string.IsNullOrWhiteSpace(world))
            world = FirstString(eventData, "worldId2", "worldId");
        if (string.IsNullOrWhiteSpace(mob) || string.IsNullOrWhiteSpace(world))
            return false;

        var inner = eventData.TryGetProperty("data", out var nested) && nested.ValueKind == JsonValueKind.Object
            ? nested
            : default;
        var zone = inner.ValueKind == JsonValueKind.Object ? FirstString(inner, "zoneId2", "zoneId") : string.Empty;
        if (string.IsNullOrWhiteSpace(zone))
            zone = FirstString(eventData, "zoneId2", "zoneId");
        if (string.IsNullOrWhiteSpace(zone) &&
            TryFindNamedProperty(eventData, ["zoneId2", "zoneId"], out var nestedZone, out _))
            zone = ElementText(nestedZone);

        // Faloop currently emits both zonePoiIds:[id] and zonePoiId:id depending on the
        // report/recent-event path. Search the complete event data so a wrapper such as
        // spawn/data does not silently turn a valid POI into zero.
        var poiId = 0;
        var rawPoiKey = "(missing)";
        TryFindPoi(eventData, out poiId, out rawPoiKey);

        var directMapX = 0f;
        var directMapY = 0f;
        var coordinateEvidence = new List<string>();
        if (TryFindDirectMapCoordinates(eventData, out directMapX, out directMapY, out var mapCoordinatePath))
            coordinateEvidence.Add($"{mapCoordinatePath}=({directMapX.ToString("0.###", CultureInfo.InvariantCulture)}," +
                                   $"{directMapY.ToString("0.###", CultureInfo.InvariantCulture)})");
        string? rawLocation = null;
        if (TryFindLocation(eventData, out var location, out var locationPath))
        {
            rawLocation = location;
            coordinateEvidence.Add($"{locationPath}={location}");
        }

        var instance = 0;
        if (eventData.TryGetProperty("zoneInstance", out var instanceElement))
            TryInt(instanceElement, out instance);
        if (instance <= 0 && inner.ValueKind == JsonValueKind.Object &&
            inner.TryGetProperty("zoneInstance", out instanceElement))
            TryInt(instanceElement, out instance);
        if (instance <= 0 &&
            TryFindNamedProperty(eventData, ["zoneInstance", "instance"], out instanceElement, out _))
            TryInt(instanceElement, out instance);

        var occurred = DateTime.UtcNow;
        var timeText = action == FaloopEventAction.Death
            ? FirstString(eventData, "killedAt", "timestamp", "spawnedAt")
            : inner.ValueKind == JsonValueKind.Object
                ? FirstString(inner, "timestamp", "spawnedAt")
                : string.Empty;
        if (string.IsNullOrWhiteSpace(timeText))
            timeText = FirstString(eventData, "timestamp", "spawnedAt");
        if (DateTimeOffset.TryParse(timeText, out var timestamp))
            occurred = timestamp.UtcDateTime;

        feedEvent = new FaloopFeedEvent(action.Value, mob.Trim(), world.Trim(),
            string.IsNullOrWhiteSpace(zone) ? null : zone.Trim(), poiId, rawPoiKey,
            directMapX, directMapY, rawLocation,
            coordinateEvidence.Count == 0 ? "(none)" : string.Join("; ", coordinateEvidence),
            Math.Max(0, instance), occurred);
        return true;
    }

    private static bool TryFindPoi(JsonElement root, out int poiId, out string rawPoiKey)
    {
        poiId = 0;
        rawPoiKey = "(missing)";
        if (TryFindNamedProperty(root, ["zonePoiIds"], out var plural, out var pluralPath) &&
            plural.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var value in plural.EnumerateArray())
            {
                if (TryReadPoiValue(value, out poiId, out var raw))
                {
                    rawPoiKey = $"{pluralPath}[{index}]={raw}";
                    return true;
                }
                index++;
            }
        }
        else if (plural.ValueKind != JsonValueKind.Undefined &&
                 TryReadPoiValue(plural, out poiId, out var pluralRaw))
        {
            rawPoiKey = $"{pluralPath}={pluralRaw}";
            return true;
        }

        if (TryFindNamedProperty(root, ["zonePoiId", "poiId"], out var singular, out var singularPath) &&
            TryReadPoiValue(singular, out poiId, out var singularRaw))
        {
            rawPoiKey = $"{singularPath}={singularRaw}";
            return true;
        }
        return false;
    }

    private static bool TryReadPoiValue(JsonElement element, out int poiId, out string raw)
    {
        poiId = 0;
        raw = element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : element.GetRawText();
        if (TryInt(element, out poiId) && poiId > 0)
            return true;
        if (element.ValueKind == JsonValueKind.Object &&
            TryFindNamedProperty(element, ["id", "key"], out var nested, out _) &&
            TryReadPoiValue(nested, out poiId, out var nestedRaw))
        {
            raw = nestedRaw;
            return true;
        }

        var digits = new string(raw.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return digits.Length > 0 && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out poiId) &&
               poiId > 0;
    }

    private static bool TryFindDirectMapCoordinates(
        JsonElement root,
        out float mapX,
        out float mapY,
        out string path)
    {
        string[][] pairs =
        [
            ["mapLocationX", "mapLocationY"],
            ["mapX", "mapY"],
            ["xCoord", "yCoord"],
            ["coordinateX", "coordinateY"],
        ];
        foreach (var pair in pairs)
            if (TryFindNumberPair(root, pair[0], pair[1], out mapX, out mapY, out path) &&
                IsUsableMapCoordinate(mapX, mapY))
                return true;
        mapX = 0;
        mapY = 0;
        path = string.Empty;
        return false;
    }

    private static bool TryFindNumberPair(
        JsonElement element,
        string xName,
        string yName,
        out float x,
        out float y,
        out string path,
        string currentPath = "data",
        int depth = 0)
    {
        x = 0;
        y = 0;
        path = string.Empty;
        if (depth > 8 || element.ValueKind != JsonValueKind.Object)
            return false;

        if (TryGetPropertyIgnoreCase(element, xName, out var xElement) &&
            TryGetPropertyIgnoreCase(element, yName, out var yElement) &&
            TryFloat(xElement, out x) && TryFloat(yElement, out y))
        {
            path = $"{currentPath}.{xName}/{yName}";
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object &&
                TryFindNumberPair(property.Value, xName, yName, out x, out y, out path,
                    $"{currentPath}.{property.Name}", depth + 1))
                return true;
            if (property.Value.ValueKind != JsonValueKind.Array)
                continue;
            var index = 0;
            foreach (var item in property.Value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object &&
                    TryFindNumberPair(item, xName, yName, out x, out y, out path,
                        $"{currentPath}.{property.Name}[{index}]", depth + 1))
                    return true;
                index++;
            }
        }
        return false;
    }

    private static bool TryFindLocation(JsonElement root, out string location, out string path)
    {
        location = string.Empty;
        path = string.Empty;
        if (!TryFindNamedProperty(root, ["location"], out var element, out path))
            return false;
        if (element.ValueKind == JsonValueKind.String)
            location = element.GetString() ?? string.Empty;
        else if (element.ValueKind == JsonValueKind.Array && element.GetArrayLength() >= 2 &&
                 TryFloat(element[0], out var arrayX) && TryFloat(element[1], out var arrayY))
            location = $"{arrayX.ToString(CultureInfo.InvariantCulture)},{arrayY.ToString(CultureInfo.InvariantCulture)}";
        else if (element.ValueKind == JsonValueKind.Object &&
                 TryGetPropertyIgnoreCase(element, "x", out var xElement) &&
                 TryGetPropertyIgnoreCase(element, "y", out var yElement) &&
                 TryFloat(xElement, out var objectX) && TryFloat(yElement, out var objectY))
            location = $"{objectX.ToString(CultureInfo.InvariantCulture)},{objectY.ToString(CultureInfo.InvariantCulture)}";
        return !string.IsNullOrWhiteSpace(location);
    }

    private static bool TryFindNamedProperty(
        JsonElement element,
        string[] names,
        out JsonElement value,
        out string path,
        string currentPath = "data",
        int depth = 0)
    {
        value = default;
        path = string.Empty;
        if (depth > 8 || element.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var property in element.EnumerateObject())
        {
            if (names.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                value = property.Value;
                path = $"{currentPath}.{property.Name}";
                return true;
            }
        }
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object &&
                TryFindNamedProperty(property.Value, names, out value, out path,
                    $"{currentPath}.{property.Name}", depth + 1))
                return true;
            if (property.Value.ValueKind != JsonValueKind.Array)
                continue;
            var index = 0;
            foreach (var item in property.Value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object &&
                    TryFindNamedProperty(item, names, out value, out path,
                        $"{currentPath}.{property.Name}[{index}]", depth + 1))
                    return true;
                index++;
            }
        }
        return false;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        value = default;
        return false;
    }

    private static string ElementText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Number => element.GetRawText(),
        _ => string.Empty,
    };

    private static bool TryFloat(JsonElement element, out float value)
    {
        if (element.ValueKind == JsonValueKind.Number)
            return element.TryGetSingle(out value);
        if (element.ValueKind == JsonValueKind.String)
            return float.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        value = 0;
        return false;
    }

    private static bool IsUsableMapCoordinate(float x, float y) =>
        float.IsFinite(x) && float.IsFinite(y) && x >= 1f && x <= 50f && y >= 1f && y <= 50f;

    private static bool TryReadAuthData(JsonElement root, out string sessionId, out string token)
    {
        sessionId = string.Empty;
        token = string.Empty;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return false;
        TryString(data, "sessionId", out sessionId);
        TryString(data, "token", out token);
        return !string.IsNullOrWhiteSpace(sessionId) && !string.IsNullOrWhiteSpace(token);
    }

    private static string FirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (TryString(element, name, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        return string.Empty;
    }

    private static bool TryString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property))
            return false;
        value = property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.Number => property.GetRawText(),
            _ => string.Empty,
        };
        return value.Length > 0;
    }

    private static bool TryInt(JsonElement element, out int value)
    {
        if (element.ValueKind == JsonValueKind.Number)
            return element.TryGetInt32(out value);
        if (element.ValueKind == JsonValueKind.String)
            return int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        value = 0;
        return false;
    }

    private static TimeSpan ParseHeartbeatTimeout(string openPacket)
    {
        try
        {
            using var json = JsonDocument.Parse(openPacket[1..]);
            var root = json.RootElement;
            var interval = root.TryGetProperty("pingInterval", out var intervalValue)
                ? intervalValue.GetInt32()
                : 25000;
            var timeout = root.TryGetProperty("pingTimeout", out var timeoutValue)
                ? timeoutValue.GetInt32()
                : 20000;
            return TimeSpan.FromMilliseconds(Math.Clamp(interval + timeout + 10000, 15000, 120000));
        }
        catch
        {
            return TimeSpan.FromSeconds(60);
        }
    }

    private static async Task<string> ReceiveWithTimeoutAsync(
        ClientWebSocket socket,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        try
        {
            return await ReceiveAsync(socket, timeoutCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Faloop heartbeat timed out.");
        }
    }

    private static async Task<string> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                .ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("Faloop closed the WebSocket.");
            if (result.MessageType != WebSocketMessageType.Text)
                continue;
            stream.Write(buffer, 0, result.Count);
            if (stream.Length > 1024 * 1024)
                throw new InvalidDataException("Faloop message exceeded the 1 MiB safety limit.");
            if (result.EndOfMessage)
                return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
        }
    }

    private static Task SendAsync(ClientWebSocket socket, string text, CancellationToken cancellationToken) =>
        socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)),
            WebSocketMessageType.Text, true, cancellationToken);

    private void SetStatus(bool isConnected, string newStatus)
    {
        lock (sync)
        {
            connected = isConnected;
            status = newStatus;
        }
    }

    public void Dispose()
    {
        Stop("Disposed");
        lock (sync)
        {
            runCancellation?.Dispose();
            runCancellation = null;
            activeSocket?.Dispose();
            activeSocket = null;
        }
    }
}
