namespace PulseSharp;

/// <summary>
/// Information about the running job, and a way to report its progress.
/// </summary>
public abstract class JobContext
{
    /// <summary>The id returned by <see cref="IPulse.Enqueue{TJob}(TJob, CancellationToken)"/>.</summary>
    public abstract Guid JobId { get; }

    /// <summary>The stored name of the job type.</summary>
    public abstract string JobName { get; }

    /// <summary>The attempt number, starting at 1. Higher numbers mean earlier attempts failed.</summary>
    public abstract int Attempt { get; }

    /// <summary>
    /// Saves the job's progress so that any app instance can show it. Call it every few items, not for every item.
    /// </summary>
    /// <param name="done">How many items are finished.</param>
    /// <param name="total">How many items there are in total.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>A task that completes when the progress is saved.</returns>
    public abstract Task Report(int done, int total, CancellationToken cancellationToken = default);
}
