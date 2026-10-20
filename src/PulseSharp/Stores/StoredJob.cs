namespace PulseSharp.Stores;

/// <summary>
/// A job to save. The engine has already serialized its data.
/// </summary>
/// <param name="JobId">The globally unique id of the job (UUIDv7).</param>
/// <param name="JobName">The stored name of the job type.</param>
/// <param name="Data">The serialized job (UTF-8 JSON).</param>
/// <param name="EnqueuedAt">When the job was enqueued.</param>
/// <param name="RunAt">The earliest time the job may run.</param>
public sealed record NewJob(Guid JobId, string JobName, byte[] Data, DateTimeOffset EnqueuedAt, DateTimeOffset RunAt);

/// <summary>
/// A saved job, with its state and lease.
/// </summary>
/// <param name="JobId">The job id.</param>
/// <param name="JobName">The stored name of the job type.</param>
/// <param name="Data">The serialized job (UTF-8 JSON).</param>
/// <param name="State">The state.</param>
/// <param name="Attempts">How many times the job has been claimed.</param>
/// <param name="Done">Reported progress: items finished.</param>
/// <param name="Total">Reported progress: items in total.</param>
/// <param name="LastError">The error of the last failed attempt.</param>
/// <param name="EnqueuedAt">When the job was enqueued.</param>
/// <param name="RunAt">The earliest time the job runs, or runs again.</param>
/// <param name="StartedAt">When the latest attempt started.</param>
/// <param name="FinishedAt">When the job reached a final state.</param>
/// <param name="LeaseOwner">The worker holding the job while it runs.</param>
/// <param name="LeaseUntil">When the lease ends unless renewed.</param>
/// <param name="CancelRequested">Whether a cancel was requested while the job runs.</param>
public sealed record StoredJob(
    Guid JobId,
    string JobName,
    byte[] Data,
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
    /// <summary>
    /// Whether the job can be claimed at <paramref name="now"/>: due and waiting, or running with an expired lease.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <returns><see langword="true"/> when the job can be claimed.</returns>
    public bool IsClaimableAt(DateTimeOffset now)
    {
        var isDue = State is JobState.Pending or JobState.Retrying && RunAt <= now;
        var isAbandoned = State == JobState.Running && LeaseUntil < now;
        return isDue || isAbandoned;
    }

    /// <summary>
    /// Whether <paramref name="workerId"/> holds the job while it runs.
    /// </summary>
    /// <param name="workerId">The worker.</param>
    /// <returns><see langword="true"/> when the job is running and leased by the worker.</returns>
    public bool IsHeldBy(string workerId)
    {
        return State == JobState.Running && LeaseOwner == workerId;
    }

    internal JobInfo ToJobInfo()
    {
        return new JobInfo(JobId, JobName, State, Attempts, Done, Total, LastError, EnqueuedAt, RunAt, StartedAt, FinishedAt);
    }
}

/// <summary>
/// The answer to a lease renewal.
/// </summary>
/// <param name="Held">Whether the worker still holds the lease.</param>
/// <param name="CancelRequested">Whether a cancel was requested for the job.</param>
public readonly record struct JobLeaseStatus(bool Held, bool CancelRequested);
