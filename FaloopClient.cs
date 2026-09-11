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
    FutureTiming,
    LocationUpdate,
}

internal sealed record FaloopFeedEvent(
    FaloopEventAction Action,
    string EventType,
    string EventSubType,
    string RawAction,
    string EventId,
    string MobSlug,
    string WorldSlug,
    string? ZoneSlug,
    string RawMapId,
    int PoiId,
    string RawPoiKey,
    float DirectMapX,
    float DirectMapY,
    string? RawLocation,
    string RawCoordinateData,
    int Instance,
    DateTime OccurredAtUtc,
    bool HasAuthoritativeTimestamp,
    string TimestampSource);

internal sealed record FaloopLocationEnrichmentResult(
    bool Success,
    FaloopFeedEvent Event,
    string Detail)
{
    public static FaloopLocationEnrichmentResult Resolved(FaloopFeedEvent feedEvent, string detail) =>
        new(true, feedEvent, detail);

    public static FaloopLocationEnrichmentResult Pending(FaloopFeedEvent feedEvent, string detail) =>
        new(false, feedEvent, detail);
}

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
    private DateTime lastRawFeedMessageUtc = DateTime.MinValue;
    private DateTime lastEventUtc = DateTime.MinValue;
    private string lastRawEventSummary = "No Faloop feed message received yet";
    private string lastRejectedEventReason = "None";
    private string activeSessionId = string.Empty;

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

    public DateTime LastRawFeedMessageUtc
    {
        get { lock (sync) return lastRawFeedMessageUtc; }
    }

    public string LastRawEventSummary
    {
        get { lock (sync) return lastRawEventSummary; }
    }

    public string LastRejectedEventReason
    {
        get { lock (sync) return lastRejectedEventReason; }
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
            activeSessionId = sessionId.Trim();
        }

        _ = Task.Run(() => RunReconnectLoopAsync(sessionId.Trim(), cancellation.Token));
    }

    /// <summary>
    /// Resolves location-less lightweight spawn notifications against Faloop's authenticated
    /// datacenter snapshot. The snapshot pairs the current mob/world window with its latest
    /// sighting, which carries the authoritative zone POI even when mobworldspawn does not.
    /// </summary>
    public async Task<FaloopLocationEnrichmentResult> TryEnrichLocationAsync(
        FaloopFeedEvent feedEvent,
        string dataCenterSlug,
        CancellationToken cancellationToken = default)
    {
        string sessionId;
        lock (sync) sessionId = activeSessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
            return FaloopLocationEnrichmentResult.Pending(feedEvent, "no authenticated session is available");
        if (string.IsNullOrWhiteSpace(dataCenterSlug))
            return FaloopLocationEnrichmentResult.Pending(feedEvent, "the target datacenter could not be resolved");

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://faloop.app");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://faloop.app/");
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "SRankSentinel/0.7");

            using var refresh = await client.PostAsJsonAsync(
                "https://faloop.app/api/auth/user/refresh",
                new Dictionary<string, object?> { ["sessionId"] = sessionId },
                cancellationToken).ConfigureAwait(false);
            if (!refresh.IsSuccessStatusCode)
                return FaloopLocationEnrichmentResult.Pending(feedEvent,
                    $"session refresh returned HTTP {(int)refresh.StatusCode}");
            using var refreshJson = JsonDocument.Parse(
                await refresh.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (!TryReadAuthData(refreshJson.RootElement, out _, out var token))
                return FaloopLocationEnrichmentResult.Pending(feedEvent, "session refresh returned no access token");

            var slug = new string(dataCenterSlug.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://faloop.app/api/app/data-center/{Uri.EscapeDataString(slug)}");
            request.Headers.TryAddWithoutValidation("Authorization", token);
            request.Headers.TryAddWithoutValidation("Origin", "https://faloop.app");
            request.Headers.TryAddWithoutValidation("Referer", "https://faloop.app/");
            request.Headers.TryAddWithoutValidation("User-Agent", "SRankSentinel/0.7");
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return FaloopLocationEnrichmentResult.Pending(feedEvent,
                    $"datacenter state returned HTTP {(int)response.StatusCode}");

            var stateText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(stateText) || stateText.TrimStart().StartsWith('<'))
                return FaloopLocationEnrichmentResult.Pending(feedEvent,
                    $"datacenter state returned {(string.IsNullOrWhiteSpace(mediaType) ? "an unknown content type" : mediaType)} instead of JSON");

            using var stateJson = JsonDocument.Parse(stateText);
            return TryResolveFromDatacenterState(stateJson.RootElement, feedEvent, out var enriched, out var detail)
                ? FaloopLocationEnrichmentResult.Resolved(enriched, detail)
                : FaloopLocationEnrichmentResult.Pending(feedEvent, detail);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FaloopLocationEnrichmentResult.Pending(feedEvent, "datacenter state request timed out");
        }
        catch (Exception ex)
        {
            log.Warning("Faloop location enrichment failed for {Mob}/{World}: {Error}",
                feedEvent.MobSlug, feedEvent.WorldSlug, ex.Message);
            return FaloopLocationEnrichmentResult.Pending(feedEvent, "datacenter state request failed");
        }
    }

    public void Stop(string reason = "Disabled")
    {
        lock (sync)
        {
            runCancellation?.Cancel();
            activeSocket?.Abort();
            connected = false;
            status = reason;
            activeSessionId = string.Empty;
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
                            SetStatus(true, "Faloop socket authenticated; waiting for feed traffic");
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
                if (TryParseFeedEvent(nested.RootElement, out var nestedEvent, out var nestedRejection))
                    Publish(nestedEvent);
                else
                    RecordRejectedEvent(nested.RootElement, nestedRejection);
                return;
            }

            if (TryParseFeedEvent(payload, out var feedEvent, out var rejection))
                Publish(feedEvent);
            else
                RecordRejectedEvent(payload, rejection);
        }
        catch (JsonException ex)
        {
            log.Debug("Ignored malformed Faloop message: {Error}", ex.Message);
        }
    }

    public void RecordRelevantHuntEvent(FaloopFeedEvent feedEvent)
    {
        if (feedEvent.Action == FaloopEventAction.Spawn)
            RememberSpawn(feedEvent);

        lock (sync)
        {
            lastRawFeedMessageUtc = DateTime.UtcNow;
            lastRawEventSummary = DescribeFeedEvent(feedEvent);
            lastEventUtc = DateTime.UtcNow;
            lastRejectedEventReason = "None";
            status = "Faloop feed healthy; relevant current-DC S/SS events received";
        }
        log.Debug(
            "Relevant current-DC Faloop hunt event recognized: type={Type}/{SubType}, action={Action}/{RawAction}, mark={Mark}, world={World}, eventId={EventId}",
            feedEvent.EventType, string.IsNullOrWhiteSpace(feedEvent.EventSubType) ? "(missing)" : feedEvent.EventSubType,
            feedEvent.Action, feedEvent.RawAction, feedEvent.MobSlug, feedEvent.WorldSlug,
            string.IsNullOrWhiteSpace(feedEvent.EventId) ? "(missing)" : feedEvent.EventId);
    }

    private void RecordRejectedEvent(JsonElement payload, string reason)
    {
        if (!LooksLikeHuntEvent(payload))
            return;
        var summary = DescribeRawEvent(payload);
        log.Debug("Ignored non-actionable raw Faloop event: {Summary}; reason={Reason}", summary, reason);
    }

    private void Publish(FaloopFeedEvent feedEvent)
    {
        feedEvent = FillMissingDeathLocation(feedEvent);

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

    private static bool TryParseFeedEvent(
        JsonElement root,
        out FaloopFeedEvent feedEvent,
        out string rejectionReason)
    {
        feedEvent = null!;
        rejectionReason = string.Empty;
        if (root.ValueKind != JsonValueKind.Object || !TryString(root, "type", out var type) ||
            !root.TryGetProperty("data", out var eventData) || eventData.ValueKind != JsonValueKind.Object)
        {
            rejectionReason = "payload did not contain an object type/data envelope";
            return false;
        }

        type = type.Trim().ToLowerInvariant();
        var rawAction = type;
        FaloopEventAction? action = type switch
        {
            "mobworldspawn" => FaloopEventAction.Spawn,
            "mobworldkill" => FaloopEventAction.Death,
            _ => null,
        };
        if (action is null && IsFutureTimingAction(type))
            action = FaloopEventAction.FutureTiming;
        if (action is null && type == "mob" && TryString(eventData, "action", out var actionText))
        {
            rawAction = actionText.Trim().ToLowerInvariant();
            action = rawAction switch
            {
                "spawn" => FaloopEventAction.Spawn,
                // A sighting_set updates the map marker but does not prove the S rank is
                // currently spawned. Faloop permits sightings while the spawn window is still
                // in the future, so this can only enrich a separately tracked live spawn.
                "sighting_set" => FaloopEventAction.LocationUpdate,
                "spawn_location" => FaloopEventAction.Spawn,
                "sighting" => FaloopEventAction.LocationUpdate,
                "death" => FaloopEventAction.Death,
                _ => null,
            };
            if (action is null && IsFutureTimingAction(rawAction))
                action = FaloopEventAction.FutureTiming;
        }
        if (action is null)
        {
            rejectionReason = type == "mob" && TryString(eventData, "action", out var unsupportedAction)
                ? $"unsupported mob action '{unsupportedAction}'"
                : $"unsupported event type '{type}'";
            return false;
        }

        var eventSubType = FirstString(root, "subType", "subtype");
        var eventId = FirstPrimitiveString(root, "spawnId", "reportId", "eventId", "windowId");
        if (string.IsNullOrWhiteSpace(eventId))
            eventId = FirstPrimitiveString(eventData, "spawnId", "reportId", "eventId", "windowId", "sightingId");
        if (string.IsNullOrWhiteSpace(eventId) &&
            TryFindPrimitiveNamedProperty(eventData,
                ["spawnId", "reportId", "eventId", "windowId", "sightingId"],
                out var nestedEventId, out _))
            eventId = ElementText(nestedEventId);

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
        {
            rejectionReason = $"recognized {type} event was missing mark or world identity";
            return false;
        }

        var inner = eventData.TryGetProperty("data", out var nested) && nested.ValueKind == JsonValueKind.Object
            ? nested
            : default;
        var zone = inner.ValueKind == JsonValueKind.Object ? FirstString(inner, "zoneId2", "zoneId") : string.Empty;
        if (string.IsNullOrWhiteSpace(zone))
            zone = FirstString(eventData, "zoneId2", "zoneId");
        if (string.IsNullOrWhiteSpace(zone) &&
            TryFindNamedProperty(eventData, ["zoneId2", "zoneId"], out var nestedZone, out _))
            zone = ElementText(nestedZone);
        var rawMapId = TryFindNamedProperty(eventData, ["mapId", "mapId2"], out var mapId, out var mapPath)
            ? $"{mapPath}={ElementText(mapId)}"
            : "(missing)";

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
        var hasAuthoritativeTimestamp = false;
        var timestampSource = "receipt time";
        var timeText = action == FaloopEventAction.Death
            ? FirstString(eventData, "killedAt", "timestamp", "spawnedAt")
            : inner.ValueKind == JsonValueKind.Object
                ? FirstString(inner, "timestamp", "spawnedAt")
                : string.Empty;
        if (string.IsNullOrWhiteSpace(timeText))
            timeText = FirstString(eventData, "timestamp", "spawnedAt");
        if (!string.IsNullOrWhiteSpace(timeText))
            timestampSource = "event timestamp";
        else if (TryFindPrimitiveNamedProperty(eventData,
                     action == FaloopEventAction.Death
                         ? ["killedAt", "occurredAt", "timestamp", "spawnedAt", "createdAt"]
                         : ["occurredAt", "timestamp", "spawnedAt", "createdAt", "sightedAt"],
                     out var nestedTime, out var nestedTimePath))
        {
            timeText = ElementText(nestedTime);
            timestampSource = nestedTimePath;
        }
        if (DateTimeOffset.TryParse(timeText, out var timestamp))
        {
            occurred = timestamp.UtcDateTime;
            hasAuthoritativeTimestamp = true;
        }

        feedEvent = new FaloopFeedEvent(action.Value, type, eventSubType, rawAction, eventId,
            mob.Trim(), world.Trim(), string.IsNullOrWhiteSpace(zone) ? null : zone.Trim(), rawMapId,
            poiId, rawPoiKey,
            directMapX, directMapY, rawLocation,
            coordinateEvidence.Count == 0 ? "(none)" : string.Join("; ", coordinateEvidence),
            Math.Max(0, instance), occurred, hasAuthoritativeTimestamp, timestampSource);
        return true;
    }

    private static bool IsFutureTimingAction(string action)
    {
        var normalized = new string(action.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return normalized.Contains("timing", StringComparison.Ordinal) ||
               normalized.Contains("timer", StringComparison.Ordinal) ||
               normalized.Contains("window", StringComparison.Ordinal) ||
               normalized.Contains("availability", StringComparison.Ordinal) ||
               normalized.Contains("availableat", StringComparison.Ordinal) ||
               normalized.Contains("cooldown", StringComparison.Ordinal) ||
               normalized.Contains("maintenance", StringComparison.Ordinal) ||
               normalized.Contains("resettime", StringComparison.Ordinal);
    }

    private static bool LooksLikeHuntEvent(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !TryString(root, "type", out var type))
            return false;
        type = type.Trim().ToLowerInvariant();
        return type is "mob" or "mobworldspawn" or "mobworldkill" || IsFutureTimingAction(type);
    }

    private static string DescribeRawEvent(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return $"type=(non-object {root.ValueKind})";
        var type = FirstString(root, "type");
        if (string.IsNullOrWhiteSpace(type))
            type = "(missing)";
        if (!root.TryGetProperty("data", out var eventData) || eventData.ValueKind != JsonValueKind.Object)
            return $"type={type}, action=(missing), mark=(missing), world=(missing)";
        var action = FirstString(eventData, "action");
        if (string.IsNullOrWhiteSpace(action))
            action = "(missing)";
        var mob = string.Empty;
        var world = string.Empty;
        if (eventData.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Object)
        {
            mob = FirstString(id, "mobId", "mobId2");
            world = FirstString(id, "worldId", "worldId2");
        }
        if (string.IsNullOrWhiteSpace(mob))
            mob = FirstString(eventData, "mobId2", "mobId");
        if (string.IsNullOrWhiteSpace(world))
            world = FirstString(eventData, "worldId2", "worldId");
        return $"type={type}, action={action}, mark={(string.IsNullOrWhiteSpace(mob) ? "(missing)" : mob)}, world={(string.IsNullOrWhiteSpace(world) ? "(missing)" : world)}";
    }

    private static string DescribeFeedEvent(FaloopFeedEvent feedEvent) =>
        $"type={feedEvent.EventType}, action={feedEvent.RawAction}, mark={feedEvent.MobSlug}, world={feedEvent.WorldSlug}";

    private static bool TryResolveFromDatacenterState(
        JsonElement root,
        FaloopFeedEvent feedEvent,
        out FaloopFeedEvent enriched,
        out string detail)
    {
        enriched = feedEvent;
        detail = "no matching active Faloop window/sighting is available yet";
        if (!TryGetPropertyIgnoreCase(root, "data", out var dataElement) ||
            !TryGetPropertyIgnoreCase(dataElement, "status", out var statusElement) ||
            !TryGetPropertyIgnoreCase(statusElement, "windows", out var windowsElement) ||
            windowsElement.ValueKind != JsonValueKind.Array ||
            !TryGetPropertyIgnoreCase(statusElement, "sightings", out var sightingsElement) ||
            sightingsElement.ValueKind != JsonValueKind.Array)
        {
            detail = "Faloop datacenter state did not contain windows and sightings arrays";
            return false;
        }

        JsonElement? matchingWindow = null;
        DateTime matchingStartedAt = DateTime.MinValue;
        string matchingWindowId = string.Empty;
        var matchingWindowUsesEventId = false;
        foreach (var window in windowsElement.EnumerateArray())
        {
            if (window.ValueKind != JsonValueKind.Object ||
                !SlugEquals(FirstString(window, "mobId2", "mobId"), feedEvent.MobSlug) ||
                !SlugEquals(FirstString(window, "worldId2", "worldId"), feedEvent.WorldSlug) ||
                !InstanceMatches(window, feedEvent.Instance))
                continue;

            var candidateWindowId = FindCorrelationId(window);
            var candidateUsesEventId = !string.IsNullOrWhiteSpace(feedEvent.EventId) &&
                                       !string.IsNullOrWhiteSpace(candidateWindowId) &&
                                       candidateWindowId.Equals(feedEvent.EventId, StringComparison.OrdinalIgnoreCase);
            var startedAt = ReadTimestamp(window, "startedAt", "spawnedAt", "timestamp");
            if (matchingWindow is null || candidateUsesEventId && !matchingWindowUsesEventId ||
                candidateUsesEventId == matchingWindowUsesEventId && startedAt > matchingStartedAt)
            {
                matchingWindow = window;
                matchingStartedAt = startedAt;
                matchingWindowId = candidateWindowId;
                matchingWindowUsesEventId = candidateUsesEventId;
            }
        }
        if (matchingWindow is null)
            return false;

        // A lightweight mob/report notification frequently has no timestamp. In that case
        // OccurredAtUtc is only the local receipt time and may be hours after Faloop opened the
        // current spawn window. Reject only when an explicit event timestamp demonstrably
        // predates a newer window; never compare a window start to a synthetic receipt time.
        if (feedEvent.HasAuthoritativeTimestamp && matchingStartedAt != DateTime.MinValue &&
            matchingStartedAt - feedEvent.OccurredAtUtc > TimeSpan.FromMinutes(15))
        {
            detail = $"matching Faloop window is newer than the event ({matchingStartedAt:O} vs {feedEvent.OccurredAtUtc:O})";
            return false;
        }

        if (TryBuildEnrichedEventFromStateLocation(
                matchingWindow.Value, feedEvent, matchingWindowId, "datacenter.status.windows",
                out enriched, out var windowLocationDetail))
        {
            detail = $"matched active window directly ({windowLocationDetail})";
            return true;
        }

        JsonElement? bestSighting = null;
        DateTime bestSightedAt = DateTime.MinValue;
        var bestIsPreviousLocation = true;
        string bestSightingId = string.Empty;
        foreach (var sighting in sightingsElement.EnumerateArray())
        {
            if (sighting.ValueKind != JsonValueKind.Object ||
                !SlugEquals(FirstString(sighting, "mobId2", "mobId"), feedEvent.MobSlug) ||
                !SlugEquals(FirstString(sighting, "worldId2", "worldId"), feedEvent.WorldSlug) ||
                !InstanceMatches(sighting, feedEvent.Instance) ||
                !HasUsableLocationEvidence(sighting))
                continue;

            var sightedAt = ReadTimestamp(sighting, "sightedAt", "timestamp", "createdAt");
            // The current location can be reported long after the spawn window opened. Only
            // discard sightings that positively belong to the previous window.
            if (matchingStartedAt != DateTime.MinValue && sightedAt != DateTime.MinValue &&
                sightedAt < matchingStartedAt - TimeSpan.FromMinutes(1))
                continue;

            var candidateSightingId = FindCorrelationId(sighting);
            if (!string.IsNullOrWhiteSpace(feedEvent.EventId) &&
                !string.IsNullOrWhiteSpace(candidateSightingId) &&
                !candidateSightingId.Equals(feedEvent.EventId, StringComparison.OrdinalIgnoreCase) &&
                matchingWindowUsesEventId)
                continue;

            var isPreviousLocation = TryGetPropertyIgnoreCase(sighting, "prevLocation", out var previous) &&
                                     previous.ValueKind == JsonValueKind.True;
            // Prefer a positively reported current location over Faloop's previous-location
            // placeholder. If only the placeholder exists, it is still the same location that
            // Faloop exposes for the active window and is preferable to waiting forever.
            if (bestSighting is null || !isPreviousLocation && bestIsPreviousLocation ||
                isPreviousLocation == bestIsPreviousLocation && sightedAt > bestSightedAt)
            {
                bestSighting = sighting;
                bestSightedAt = sightedAt;
                bestIsPreviousLocation = isPreviousLocation;
                bestSightingId = candidateSightingId;
            }
        }
        if (bestSighting is null)
        {
            detail = "matching active Faloop window has no usable location sighting yet";
            return false;
        }

        if (!TryBuildEnrichedEventFromStateLocation(
                bestSighting.Value, feedEvent,
                string.IsNullOrWhiteSpace(bestSightingId) ? matchingWindowId : bestSightingId,
                "datacenter.status.sightings", out enriched, out var sightingDetail))
        {
            detail = "matching active Faloop sighting contained no usable coordinate or POI";
            return false;
        }

        var locationKind = bestIsPreviousLocation ? "previous-location placeholder" : "current location report";
        detail = $"matched active window to {locationKind} ({sightingDetail})";
        return true;
    }

    private static bool HasUsableLocationEvidence(JsonElement element) =>
        TryFindPoi(element, out var poiId, out _) && poiId > 0 ||
        TryFindDirectMapCoordinates(element, out _, out _, out _) ||
        TryFindLocation(element, out _, out _);

    private static bool TryBuildEnrichedEventFromStateLocation(
        JsonElement locationElement,
        FaloopFeedEvent feedEvent,
        string correlationId,
        string sourcePrefix,
        out FaloopFeedEvent enriched,
        out string detail)
    {
        enriched = feedEvent;
        detail = string.Empty;
        var poiId = 0;
        var rawPoi = "(missing)";
        TryFindPoi(locationElement, out poiId, out rawPoi);

        var directMapX = 0f;
        var directMapY = 0f;
        var directPath = string.Empty;
        TryFindDirectMapCoordinates(locationElement, out directMapX, out directMapY, out directPath);

        string? rawLocation = null;
        var locationPath = string.Empty;
        if (TryFindLocation(locationElement, out var foundLocation, out locationPath))
            rawLocation = foundLocation;
        if (poiId <= 0 && !IsUsableMapCoordinate(directMapX, directMapY) &&
            string.IsNullOrWhiteSpace(rawLocation))
            return false;

        var zone = FirstString(locationElement, "zoneId2", "zoneId");
        if (string.IsNullOrWhiteSpace(zone) &&
            TryFindNamedProperty(locationElement, ["zoneId2", "zoneId"], out var nestedZone, out _))
            zone = ElementText(nestedZone);
        if (string.IsNullOrWhiteSpace(zone))
            zone = feedEvent.ZoneSlug ?? string.Empty;

        var rawMapId = feedEvent.RawMapId;
        if (TryFindNamedProperty(locationElement, ["mapId", "mapId2"], out var mapId, out var mapPath))
            rawMapId = $"{sourcePrefix}:{mapPath}={ElementText(mapId)}";

        var evidence = new List<string>();
        if (feedEvent.RawCoordinateData != "(none)")
            evidence.Add(feedEvent.RawCoordinateData);
        if (poiId > 0)
            evidence.Add($"{sourcePrefix} POI {poiId}");
        if (IsUsableMapCoordinate(directMapX, directMapY))
            evidence.Add($"{sourcePrefix}:{directPath}=({directMapX.ToString("0.###", CultureInfo.InvariantCulture)},{directMapY.ToString("0.###", CultureInfo.InvariantCulture)})");
        if (!string.IsNullOrWhiteSpace(rawLocation))
            evidence.Add($"{sourcePrefix}:{locationPath}={rawLocation}");

        enriched = feedEvent with
        {
            EventId = string.IsNullOrWhiteSpace(feedEvent.EventId) ? correlationId : feedEvent.EventId,
            ZoneSlug = string.IsNullOrWhiteSpace(zone) ? feedEvent.ZoneSlug : zone,
            RawMapId = rawMapId,
            PoiId = poiId > 0 ? poiId : feedEvent.PoiId,
            RawPoiKey = poiId > 0 ? $"{sourcePrefix}:{rawPoi}" : feedEvent.RawPoiKey,
            DirectMapX = IsUsableMapCoordinate(directMapX, directMapY) ? directMapX : feedEvent.DirectMapX,
            DirectMapY = IsUsableMapCoordinate(directMapX, directMapY) ? directMapY : feedEvent.DirectMapY,
            RawLocation = string.IsNullOrWhiteSpace(rawLocation) ? feedEvent.RawLocation : rawLocation,
            RawCoordinateData = evidence.Count == 0 ? "(none)" : string.Join("; ", evidence),
        };
        detail = poiId > 0
            ? $"POI {poiId}, correlationId={DisplayId(correlationId)}"
            : $"direct/nested coordinates, correlationId={DisplayId(correlationId)}";
        return true;
    }

    private static string FindCorrelationId(JsonElement element)
    {
        if (TryFindPrimitiveNamedProperty(element,
                ["spawnId", "reportId", "eventId", "windowId"], out var id, out _))
            return ElementText(id);
        return string.Empty;
    }

    private static string DisplayId(string id) => string.IsNullOrWhiteSpace(id) ? "(missing)" : id;

    private static bool InstanceMatches(JsonElement element, int requestedInstance)
    {
        if (requestedInstance <= 1)
            return true;
        if (!TryFindNamedProperty(element, ["zoneInstance", "instance"], out var instanceElement, out _) ||
            !TryInt(instanceElement, out var candidateInstance) || candidateInstance <= 0)
            return true;
        return candidateInstance == requestedInstance;
    }

    private static bool SlugEquals(string left, string right) =>
        NormalizeSlug(left).Equals(NormalizeSlug(right), StringComparison.Ordinal);

    private static string NormalizeSlug(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static DateTime ReadTimestamp(JsonElement element, params string[] names)
    {
        var text = FirstString(element, names);
        return DateTimeOffset.TryParse(text, out var value) ? value.UtcDateTime : DateTime.MinValue;
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
        else if (element.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            var raw = element.GetRawText();
            location = raw.Length <= 512 ? raw : raw[..512] + "...";
        }
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

    private static bool TryFindPrimitiveNamedProperty(
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
            if (names.Contains(property.Name, StringComparer.OrdinalIgnoreCase) &&
                property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            {
                value = property.Value;
                path = $"{currentPath}.{property.Name}";
                return true;
            }
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object &&
                TryFindPrimitiveNamedProperty(property.Value, names, out value, out path,
                    $"{currentPath}.{property.Name}", depth + 1))
                return true;
            if (property.Value.ValueKind != JsonValueKind.Array)
                continue;
            var index = 0;
            foreach (var item in property.Value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object &&
                    TryFindPrimitiveNamedProperty(item, names, out value, out path,
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

    private static string FirstPrimitiveString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetPropertyIgnoreCase(element, name, out var value) &&
                value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                return ElementText(value);
        }
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
