namespace PulseSharp;

/// <summary>
/// Lets an app or an operator see and steer handlers: list their cursors, retry or skip a failed event,
/// or rewind a handler so it handles every event again.
/// </summary>
public interface IPulseControl
{
    /// <summary>
    /// Lists the cursors of one handler, or of all handlers.
    /// </summary>
    /// <param name="handlerName">The handler, or <see langword="null"/> for all handlers.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The cursors, ordered by handler and stream.</returns>
    /// <exception cref="ArgumentException">No handler has this name.</exception>
    Task<IReadOnlyList<HandlerCursor>> GetCursors(string? handlerName = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tries the stuck event of a stream again now, with a fresh attempt count.
    /// </summary>
    /// <param name="handlerName">The handler.</param>
    /// <param name="stream">The stream.</param>
    /// <param name="cancellationToken">Stops waiting for the handler to apply the request.</param>
    /// <returns>A task that completes when the handler has applied the request.</returns>
    /// <exception cref="ArgumentException">No handler has this name.</exception>
    /// <exception cref="InvalidOperationException">The handler has no cursor for this stream.</exception>
    Task Retry(string handlerName, string stream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Skips the stuck event of a stream: the handler never handles it, and continues with the next event.
    /// </summary>
    /// <param name="handlerName">The handler.</param>
    /// <param name="stream">The stream.</param>
    /// <param name="cancellationToken">Stops waiting for the handler to apply the request.</param>
    /// <returns>A task that completes when the handler has applied the request.</returns>
    /// <exception cref="ArgumentException">No handler has this name.</exception>
    /// <exception cref="InvalidOperationException">The stream has no waiting event for this handler.</exception>
    Task Skip(string handlerName, string stream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rewinds a handler to the start, so it handles every stored event again, in order.
    /// Use it after fixing a bug in a handler, or to rebuild data the handler writes.
    /// </summary>
    /// <param name="handlerName">The handler.</param>
    /// <param name="cancellationToken">Stops waiting for the handler to apply the request.</param>
    /// <returns>A task that completes when the handler has applied the request.</returns>
    /// <exception cref="ArgumentException">No handler has this name.</exception>
    Task Rewind(string handlerName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one job.
    /// </summary>
    /// <param name="jobId">The id returned by <see cref="IPulse.Enqueue{TJob}(TJob, CancellationToken)"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The job, or <see langword="null"/> when no job has this id.</returns>
    Task<JobInfo?> GetJob(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists jobs, newest first.
    /// </summary>
    /// <param name="state">Only jobs in this state, or <see langword="null"/> for all jobs.</param>
    /// <param name="limit">The maximum number of jobs to return. The default is 100.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The jobs.</returns>
    Task<IReadOnlyList<JobInfo>> GetJobs(JobState? state = null, int limit = 100, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a job. A waiting job is cancelled at once. A running job gets a cancel request: its handler's
    /// <see cref="CancellationToken"/> is cancelled within about one poll interval.
    /// </summary>
    /// <param name="jobId">The job.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns><see langword="true"/> when the job was waiting or running; <see langword="false"/> when it does not exist or has already finished.</returns>
    Task<bool> CancelJob(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a failed or cancelled job again, with a fresh attempt count.
    /// </summary>
    /// <param name="jobId">The job.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns><see langword="true"/> when the job will run again; <see langword="false"/> when it does not exist or is not failed or cancelled.</returns>
    Task<bool> RetryJob(Guid jobId, CancellationToken cancellationToken = default);
}
