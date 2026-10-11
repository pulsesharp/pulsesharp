namespace PulseSharp.Stores;

internal interface IPulseStore
{
    Task<StoredEvent> Append(NewEvent newEvent, ExpectedVersion expectedVersion, CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredEvent>> ReadAfter(
        long globalPosition,
        IReadOnlySet<string> eventNames,
        int maxCount,
        CancellationToken cancellationToken
    );

    Task<IReadOnlyList<Cursor>> LoadCursors(string handlerName, CancellationToken cancellationToken);

    Task SaveCursor(Cursor cursor, CancellationToken cancellationToken);

    Task<StoredJob> AddJob(NewJob newJob, CancellationToken cancellationToken);

    Task<StoredJob?> ClaimNextJob(
        IReadOnlySet<string> jobNames,
        string workerId,
        DateTimeOffset now,
        TimeSpan lease,
        CancellationToken cancellationToken
    );

    Task<LeaseStatus> RenewLease(Guid jobId, string workerId, DateTimeOffset leaseUntil, CancellationToken cancellationToken);

    Task SaveProgress(Guid jobId, string workerId, int done, int total, CancellationToken cancellationToken);

    Task CompleteJob(Guid jobId, string workerId, DateTimeOffset now, CancellationToken cancellationToken);

    Task FailJobAttempt(
        Guid jobId,
        string workerId,
        string error,
        DateTimeOffset? retryAt,
        DateTimeOffset now,
        CancellationToken cancellationToken
    );

    Task CancelRunningJob(Guid jobId, string workerId, DateTimeOffset now, CancellationToken cancellationToken);

    Task ReleaseJob(Guid jobId, string workerId, CancellationToken cancellationToken);

    Task<StoredJob?> GetJob(Guid jobId, CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredJob>> GetJobs(JobState? state, int limit, CancellationToken cancellationToken);

    Task<bool> RequestCancel(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken);

    Task<bool> RetryJob(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken);
}
