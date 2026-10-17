using System.Text.Json;
using System.Text.Json.Serialization;

namespace SRankSentinel;

internal enum RemoteRecoveryOperation { Status, Skip, Reset }

internal sealed record RecoverySnapshot(
    [property: JsonIgnore] string CharacterKey,
    string Character, bool Loaded, bool Enabled, bool Ready,
    string State, string StatusText, uint Territory,
    string? ActiveHunt, int QueueCount, bool Recovering);

internal sealed record RemoteRecoveryResult(
    string Schema, string Version, bool Success, string Reason, RecoverySnapshot? Status);

/// <summary>Local, synchronous access boundary. No queued recovery and no arbitrary commands.</summary>
internal sealed class RemoteRecoveryController(
    Func<RecoverySnapshot> read, Action skip, Action reset)
{
    public const string Schema = "sentinel.srank.remote.v1";

    public string Handle(RemoteRecoveryOperation operation, string expectedCharacterKey, bool frameworkThread)
    {
        RecoverySnapshot? snapshot = null;
        try
        {
            if (!frameworkThread) return Reply(false, "FrameworkThreadRequired", null);
            snapshot = read();
            if (!snapshot.Loaded) return Reply(false, "Unavailable", snapshot);
            if (string.IsNullOrEmpty(expectedCharacterKey) ||
                !string.Equals(expectedCharacterKey, snapshot.CharacterKey, StringComparison.Ordinal))
                return Reply(false, "WrongCharacter", null);
            if (operation == RemoteRecoveryOperation.Status) return Reply(true, "Status", snapshot);
            if (!snapshot.Ready) return Reply(false, "CharacterNotReady", snapshot);
            if (!snapshot.Enabled) return Reply(false, "SentinelDisabled", snapshot);
            if (snapshot.Recovering) return Reply(false, "RecoveryAlreadyInProgress", snapshot);
            switch (operation)
            {
                case RemoteRecoveryOperation.Skip:
                    if (snapshot.ActiveHunt is null) return Reply(false, "NoActiveHunt", snapshot);
                    skip();
                    break;
                case RemoteRecoveryOperation.Reset:
                    reset();
                    break;
                default:
                    return Reply(false, "UnsupportedOperation", snapshot);
            }
            return Reply(true, operation == RemoteRecoveryOperation.Skip ? "SkipRequested" : "ResetRequested", read());
        }
        catch
        {
            // Native/provider exceptions never cross IPC or disclose credentials/exception text.
            return Reply(false, "RecoveryFailed", null);
        }
    }

    private static string Reply(bool success, string reason, RecoverySnapshot? snapshot)
        => JsonSerializer.Serialize(new RemoteRecoveryResult(Schema, typeof(RemoteRecoveryController).Assembly.GetName().Version?.ToString() ?? "unknown", success, reason, snapshot));

    internal static string Bound(string? value, int limit)
        => new((value ?? string.Empty).Where(c => !char.IsControl(c)).Take(limit).ToArray());
}
