using System.Text.Json;
using System.Text.Json.Serialization;
using SentinelCore.Diagnostics;

namespace SRankSentinel;

internal enum ProvingEvent { Enabled, Disabled, DomainState, OperationStarted, ProbeDestinationPending, ProbeDestinationSet, ProbeDestinationRejected, ProbeStarted, ProbeCancelled, ProbeArrived, ProbeCleared, Halted, Exported, ParkingLandingHandoff }
internal sealed record ProvingObservation(DateTimeOffset Timestamp, ProvingEvent Event, string DomainState, Guid? OperationId);

/// <summary>Consumer-only evidence; deliberately accepts enum states rather than log/chat/config text.</summary>
internal sealed class NavigationProvingJournal
{
    private readonly Queue<ProvingObservation> events = new();
    public Guid InstanceId { get; } = Guid.NewGuid();
    public DateTimeOffset CreatedUtc { get; } = DateTimeOffset.UtcNow;
    public int OperationsStarted { get; private set; }
    public void Record(ProvingEvent kind, Enum state, Guid? operation = null)
    {
        if (kind == ProvingEvent.OperationStarted) OperationsStarted++;
        while (events.Count >= 256) events.Dequeue();
        events.Enqueue(new(DateTimeOffset.UtcNow, kind, state.ToString(), operation));
    }
    public string Export(NavigationDiagnostics diagnostics, bool enabled, Enum state)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return JsonSerializer.Serialize(new {
            Schema = "sentinel.navigation.v1",
            Entries = diagnostics.Snapshot(),
            ProvingSession = new {
                InstanceId, CreatedUtc,
                PluginVersion = typeof(NavigationProvingJournal).Assembly.GetName().Version?.ToString(),
                Enabled = enabled, DomainState = state.ToString(), OperationsStarted,
                SharedOperationObserved = OperationsStarted > 0,
                Observations = events.ToArray()
            }
        }, options);
    }
}
