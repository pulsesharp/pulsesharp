namespace PulseSharp.Stores;

internal sealed record NewJob(Guid JobId, string JobName, object Data, DateTimeOffset EnqueuedAt, DateTimeOffset RunAt);

internal sealed record StoredJob(
    Guid Id,
    string JobName,
    object Data,
    JobState State,
    int Attempts,
    int Done,
    int Total,
    string? LastError,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset RunAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? LeaseOwner,
    DateTimeOffset? LeaseUntil,
    bool CancelRequested
)
{
    public bool IsClaimableAt(DateTimeOffset now)
    {
        var isDue = State is JobState.Pending or JobState.Retrying && RunAt <= now;
        var isAbandoned = State == JobState.Running && LeaseUntil < now;
        return isDue || isAbandoned;
    }

    public bool IsHeldBy(string workerId)
    {
        return State == JobState.Running && LeaseOwner == workerId;
    }

    public JobInfo ToJobInfo()
    {
        return new JobInfo(Id, JobName, State, Attempts, Done, Total, LastError, EnqueuedAt, RunAt, StartedAt, FinishedAt);
    }
}

internal readonly record struct LeaseStatus(bool Held, bool CancelRequested);
