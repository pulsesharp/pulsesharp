using System.Data.Common;

namespace PulseSharp.Stores;

/// <summary>
/// Where PulseSharp keeps events, handler cursors and jobs. Implement it to support another database.
/// </summary>
/// <remarks>
/// A store only saves and reads: ordering, retries, leases and delivery live in the engine.
/// Event and job data arrive already serialized, so a store never deals with .NET types.
/// Every method must be safe to call from several threads at once.
/// </remarks>
public interface IPulseStore
{
    /// <summary>
    /// Prepares the store, for example by creating or upgrading its tables. PulseSharp calls it once when the app
    /// starts, before any handler or job runs. It must be safe to call more than once.
    /// </summary>
    /// <param name="cancellationToken">Cancels the preparation.</param>
    /// <returns>A task that completes when the store is ready.</returns>
    Task Initialize(CancellationToken cancellationToken);

    /// <summary>
    /// Saves an event at the end of its stream, after checking <paramref name="expectedVersion"/> atomically.
    /// </summary>
    /// <param name="newEvent">The event to save.</param>
    /// <param name="expectedVersion">What the stream must look like for the save to succeed.</param>
    /// <param name="transaction">The caller's transaction to save the event in, or <see langword="null"/>. Stores that can't use it ignore it.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>The saved event with its global and stream positions.</returns>
    /// <exception cref="ExpectedVersionMismatchException">The stream does not match <paramref name="expectedVersion"/>.</exception>
    Task<StoredEvent> Append(
        NewEvent newEvent,
        ExpectedVersion expectedVersion,
        DbTransaction? transaction,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Reads events after a global position, in global order, limited to some event names.
    /// </summary>
    /// <param name="globalPosition">Only events after this position are returned. 0 reads from the start.</param>
    /// <param name="eventNames">Only events with one of these names are returned.</param>
    /// <param name="maxCount">The maximum number of events to return.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The events, ordered by global position.</returns>
    Task<IReadOnlyList<StoredEvent>> ReadAfter(
        long globalPosition,
        IReadOnlySet<string> eventNames,
        int maxCount,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Loads every cursor of one handler.
    /// </summary>
    /// <param name="handlerName">The handler.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The cursors, one per stream.</returns>
    Task<IReadOnlyList<HandlerCursor>> LoadCursors(string handlerName, CancellationToken cancellationToken);

    /// <summary>
    /// Saves a cursor, replacing the one with the same handler and stream.
    /// </summary>
    /// <param name="cursor">The cursor.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>A task that completes when the cursor is saved.</returns>
    Task SaveCursor(HandlerCursor cursor, CancellationToken cancellationToken);

    /// <summary>
    /// Saves a new job in the <see cref="JobState.Pending"/> state.
    /// </summary>
    /// <param name="newJob">The job.</param>
    /// <param name="transaction">The caller's transaction to save the job in, or <see langword="null"/>. Stores that can't use it ignore it.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>The saved job.</returns>
    Task<StoredJob> AddJob(NewJob newJob, DbTransaction? transaction, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically claims the next job to run: a pending or retrying job whose <c>RunAt</c> has come (earliest first,
    /// then oldest first), or a running job whose lease has expired. The claimed job becomes running, its attempt count
    /// grows by one, and the lease belongs to <paramref name="workerId"/> until <paramref name="now"/> + <paramref name="lease"/>.
    /// </summary>
    /// <param name="jobNames">Only jobs with one of these names can be claimed.</param>
    /// <param name="workerId">The worker that claims the job.</param>
    /// <param name="now">The current time.</param>
    /// <param name="lease">How long the claim lasts without renewal.</param>
    /// <param name="cancellationToken">Cancels the claim.</param>
    /// <returns>The claimed job, or <see langword="null"/> when no job is due.</returns>
    Task<StoredJob?> ClaimNextJob(
        IReadOnlySet<string> jobNames,
        string workerId,
        DateTimeOffset now,
        TimeSpan lease,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Extends the lease of a running job, when <paramref name="workerId"/> still holds it.
    /// </summary>
    /// <param name="jobId">The job.</param>
    /// <param name="workerId">The worker.</param>
    /// <param name="leaseUntil">The new end of the lease.</param>
    /// <param name="cancellationToken">Cancels the renewal.</param>
    /// <returns>Whether the worker still holds the lease, and whether a cancel was requested.</returns>
    Task<LeaseStatus> RenewLease(Guid jobId, string workerId, DateTimeOffset leaseUntil, CancellationToken cancellationToken);

    /// <summary>
    /// Saves the progress of a running job held by <paramref name="workerId"/>.
    /// </summary>
    /// <param name="jobId">The job.</param>
    /// <param name="workerId">The worker.</param>
    /// <param name="done">Items finished.</param>
    /// <param name="total">Items in total.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>A task that completes when the progress is saved.</returns>
    Task SaveProgress(Guid jobId, string workerId, int done, int total, CancellationToken cancellationToken);

    /// <summary>
    /// Marks a running job held by <paramref name="workerId"/> as succeeded and ends its lease.
    /// </summary>
    /// <param name="jobId">The job.</param>
    /// <param name="workerId">The worker.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>A task that completes when the job is saved.</returns>
    Task CompleteJob(Guid jobId, string workerId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Records a failed attempt of a running job held by <paramref name="workerId"/> and ends its lease.
    /// </summary>
    /// <param name="jobId">The job.</param>
    /// <param name="workerId">The worker.</param>
    /// <param name="error">The error.</param>
    /// <param name="retryAt">When to try again (the job becomes retrying), or <see langword="null"/> to mark it failed.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>A task that completes when the job is saved.</returns>
    Task FailJobAttempt(
        Guid jobId,
        string workerId,
        string error,
        DateTimeOffset? retryAt,
        DateTimeOffset now,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Marks a running job held by <paramref name="workerId"/> as cancelled and ends its lease.
    /// </summary>
    /// <param name="jobId">The job.</param>
    /// <param name="workerId">The worker.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>A task that completes when the job is saved.</returns>
    Task CancelRunningJob(Guid jobId, string workerId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Hands a running job held by <paramref name="workerId"/> back without counting the attempt (the app is stopping).
    /// </summary>
    /// <param name="jobId">The job.</param>
    /// <param name="workerId">The worker.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>A task that completes when the job is saved.</returns>
    Task ReleaseJob(Guid jobId, string workerId, CancellationToken cancellationToken);

    /// <summary>
    /// Reads one job.
    /// </summary>
    /// <param name="jobId">The job.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The job, or <see langword="null"/>.</returns>
    Task<StoredJob?> GetJob(Guid jobId, CancellationToken cancellationToken);

    /// <summary>
    /// Lists jobs, newest first.
    /// </summary>
    /// <param name="state">Only jobs in this state, or <see langword="null"/> for all.</param>
    /// <param name="limit">The maximum number of jobs.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The jobs.</returns>
    Task<IReadOnlyList<StoredJob>> GetJobs(JobState? state, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Cancels a pending or retrying job at once, or flags a running job for cancellation.
    /// </summary>
    /// <param name="jobId">The job.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns><see langword="true"/> when the job was pending, retrying or running.</returns>
    Task<bool> RequestCancel(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Makes a failed or cancelled job pending again, with a fresh attempt count and progress.
    /// </summary>
    /// <param name="jobId">The job.</param>
    /// <param name="now">The current time, used as the new run time.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns><see langword="true"/> when the job was failed or cancelled.</returns>
    Task<bool> RetryJob(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken);
}
