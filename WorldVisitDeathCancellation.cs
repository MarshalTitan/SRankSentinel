namespace SRankSentinel;

internal readonly record struct WorldVisitQueueStatus(
    bool Visible, bool RouteMatches, bool CancelEnabled, string Source, string Destination);

internal readonly record struct WorldVisitCancellationObservation(
    LifestreamReadiness Provider, uint WorldId, bool AtHub, bool Interactable,
    bool BetweenAreas, bool Waiting, bool Readying, WorldVisitQueueStatus Queue);

internal enum WorldVisitDeathStep
{
    Waiting, AbortAttempted, CancelAttempted, Zoning, TimedOut, SettledAtSource, SettledAtDestination,
}

// This session never starts travel. It can only stop a positively owned request and release
// the confirmed death back to the normal Ul'dah queue once both travel owners are clear.
internal sealed class WorldVisitDeathCancellation(uint sourceId, uint destinationId, bool ownsRequest, DateTime startedUtc)
{
    internal static readonly TimeSpan ActionWindow = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(500);
    private const int MaximumAttempts = 2;
    private readonly DateTime actionDeadlineUtc = startedUtc + ActionWindow;
    private DateTime? clearSinceUtc;
    private uint settledWorldId;
    private DateTime nextActionUtc = startedUtc;
    private int abortAttempts;
    private int cancelAttempts;

    internal uint SourceId { get; } = sourceId;
    internal bool AbortSent { get; private set; }
    internal bool CancelSent { get; private set; }

    internal WorldVisitDeathStep Tick(DateTime now, WorldVisitCancellationObservation state,
        Func<bool> abort, Func<bool> cancel)
    {
        var atSourceHub = state.AtHub && state.WorldId != 0 &&
            (state.WorldId == SourceId || !ownsRequest);
        var atDestination = state.WorldId != 0 && state.WorldId == destinationId;
        if (state.Provider == LifestreamReadiness.Idle && state.Interactable && !state.BetweenAreas &&
            !state.Waiting && !state.Readying && !state.Queue.Visible && (atSourceHub || atDestination))
        {
            if (settledWorldId != state.WorldId)
            {
                settledWorldId = state.WorldId;
                clearSinceUtc = now;
            }
            clearSinceUtc ??= now;
            if (now - clearSinceUtc.Value >= SettleDelay)
                return atDestination ? WorldVisitDeathStep.SettledAtDestination : WorldVisitDeathStep.SettledAtSource;
            return WorldVisitDeathStep.Waiting;
        }
        clearSinceUtc = null;

        var canCancel = state.Waiting && state.Queue.Visible && state.Queue.RouteMatches && state.Queue.CancelEnabled;
        if (state.BetweenAreas || (state.Readying && !canCancel) || atDestination)
            return WorldVisitDeathStep.Zoning;

        // A timeout ends attempts, never ownership protection. Allow the old visit to finish
        // or be canceled manually before starting the next hunt.
        if (now >= actionDeadlineUtc)
            return WorldVisitDeathStep.TimedOut;
        if (!ownsRequest || !atSourceHub || state.Provider == LifestreamReadiness.Unavailable ||
            (state.Queue.Visible && !state.Queue.RouteMatches) || now < nextActionUtc)
            return WorldVisitDeathStep.Waiting;

        if (!AbortSent && (state.Provider == LifestreamReadiness.Busy || state.Waiting) &&
            abortAttempts < MaximumAttempts)
        {
            abortAttempts++;
            AbortSent = abort();
            nextActionUtc = AbortSent ? now : now + RetryDelay;
            return WorldVisitDeathStep.AbortAttempted;
        }

        if (AbortSent && !CancelSent && canCancel && state.Provider == LifestreamReadiness.Idle &&
            cancelAttempts < MaximumAttempts)
        {
            cancelAttempts++;
            CancelSent = cancel();
            nextActionUtc = now + RetryDelay;
            return WorldVisitDeathStep.CancelAttempted;
        }

        return WorldVisitDeathStep.Waiting;
    }
}
