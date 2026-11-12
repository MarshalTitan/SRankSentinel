namespace SRankSentinel;

internal enum LifestreamReadiness { Unavailable, Idle, Busy }

internal enum LifestreamTravelStep
{
    DependencyUnavailable, WaitForTravel, TravelStillBusyAfterTimeout, WaitForArrival, WaitForPlayer,
    ReturnToHub, RequestAccepted, RequestRejected, RetryScheduled, Arrived, Unresolved,
}

// One session per hunt. Acceptance is an IPC acknowledgement, never proof of arrival.
internal sealed class LifestreamTravel(VisitWorld destination, DateTime startedUtc, TimeSpan timeout)
{
    internal const int MaximumAttempts = 2;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    internal VisitWorld Destination { get; } = destination;
    internal int Attempts { get; private set; }
    internal bool AwaitingArrival { get; private set; }
    private DateTime deadlineUtc = startedUtc + timeout;
    private DateTime earliestRequestUtc = startedUtc;

    internal LifestreamTravelStep Tick(DateTime now, LifestreamReadiness provider,
        uint actualWorldId, bool interactable, bool gameTravelBusy, bool atHub, Func<string, bool> request)
    {
        if (provider == LifestreamReadiness.Unavailable)
            return LifestreamTravelStep.DependencyUnavailable;
        if (provider == LifestreamReadiness.Busy || gameTravelBusy)
            return now >= deadlineUtc ? LifestreamTravelStep.TravelStillBusyAfterTimeout :
                LifestreamTravelStep.WaitForTravel;

        if (actualWorldId == Destination.Id)
        {
            if (!interactable)
                return now >= deadlineUtc ? LifestreamTravelStep.Unresolved : LifestreamTravelStep.WaitForPlayer;
            AwaitingArrival = false;
            return LifestreamTravelStep.Arrived;
        }

        if (AwaitingArrival)
        {
            if (now < deadlineUtc)
                return LifestreamTravelStep.WaitForArrival;
            AwaitingArrival = false;
            if (Attempts >= MaximumAttempts)
                return LifestreamTravelStep.Unresolved;
            SetRetryWindow(now);
            return LifestreamTravelStep.RetryScheduled;
        }

        if (now < earliestRequestUtc)
            return LifestreamTravelStep.WaitForPlayer;
        if (Attempts >= MaximumAttempts || now >= deadlineUtc)
            return LifestreamTravelStep.Unresolved;
        if (!interactable)
            return LifestreamTravelStep.WaitForPlayer;
        if (!atHub)
            return LifestreamTravelStep.ReturnToHub;

        Attempts++;
        if (request(Destination.Name))
        {
            AwaitingArrival = true;
            deadlineUtc = now + timeout;
            return LifestreamTravelStep.RequestAccepted;
        }

        SetRetryWindow(now);
        return LifestreamTravelStep.RequestRejected;
    }

    internal void ResumeFromHub(DateTime now) => SetRetryWindow(now);

    private void SetRetryWindow(DateTime now)
    {
        earliestRequestUtc = now + RetryDelay;
        deadlineUtc = earliestRequestUtc + timeout;
    }
}
