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
    /// Reads the events of one stream after a stream position, in stream order.
    /// </summary>
    /// <param name="stream">The stream name.</param>
    /// <param name="afterStreamPosition">Only events after this position are returned. 0 reads from the start.</param>
    /// <param name="maxCount">The maximum number of events to return.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The events, ordered by stream position.</returns>
    Task<IReadOnlyList<StoredEvent>> ReadStream(
        string stream,
        long afterStreamPosition,
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
    /// Saves a cursor, replacing the one with the same handler and stream, but only when the handler's revision is
    /// still <paramref name="revision"/>. A stale writer (a handler that lost its lease, or a change made by
    /// <see cref="IPulseControl"/> in the meantime) is rejected.
    /// </summary>
    /// <param name="cursor">The cursor.</param>
    /// <param name="revision">The handler revision the writer is working with (see <see cref="LeaseHandler"/>).</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns><see langword="true"/> when the cursor was saved; <see langword="false"/> when the revision has changed.</returns>
    Task<bool> SaveCursor(HandlerCursor cursor, long revision, CancellationToken cancellationToken);

    /// <summary>
    /// Takes or renews the lease of a handler, so that only one app instance delivers its events at a time.
    /// The lease is granted when nobody holds it, when it expired, or when <paramref name="workerId"/> already holds it.
    /// </summary>
    /// <param name="handlerName">The handler.</param>
    /// <param name="workerId">The runner asking for the lease.</param>
    /// <param name="now">The current time.</param>
    /// <param name="duration">How long the lease lasts without renewal.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Whether the lease is held, and the handler's current revision.</returns>
    Task<HandlerLeaseStatus> LeaseHandler(
        string handlerName,
        string workerId,
        DateTimeOffset now,
        TimeSpan duration,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Gives up the lease of a handler held by <paramref name="workerId"/>, so another instance can take it at once.
    /// </summary>
    /// <param name="handlerName">The handler.</param>
    /// <param name="workerId">The runner giving up the lease.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the lease is released.</returns>
    Task ReleaseHandler(string handlerName, string workerId, CancellationToken cancellationToken);

    /// <summary>
    /// Changes cursors of a handler on behalf of <see cref="IPulseControl"/>, atomically: increases the handler's
    /// revision by one (creating the handler's record when needed) and saves <paramref name="cursors"/> with the new
    /// revision, in one transaction. Writes made with an older revision are rejected from then on, and the lease holder
    /// reloads its cursors.
    /// </summary>
    /// <param name="handlerName">The handler.</param>
    /// <param name="cursors">The cursors to save. They all belong to <paramref name="handlerName"/>.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>The new revision.</returns>
    Task<long> ChangeCursors(string handlerName, IReadOnlyList<HandlerCursor> cursors, CancellationToken cancellationToken);

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
