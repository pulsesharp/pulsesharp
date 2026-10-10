namespace PulseSharp;

/// <summary>
/// Runs jobs of type <typeparamref name="TJob"/>. Each job type has exactly one handler.
/// </summary>
/// <remarks>
/// If the app stops while a job is running, the job runs again later, so make handlers safe to repeat.
/// </remarks>
/// <typeparam name="TJob">The job type.</typeparam>
public interface IJobHandler<in TJob>
    where TJob : notnull
{
    /// <summary>
    /// Runs one job. Throwing an exception makes PulseSharp retry the job later.
    /// </summary>
    /// <param name="job">The job.</param>
    /// <param name="context">Reports progress and tells which attempt this is.</param>
    /// <param name="cancellationToken">Signals that the job was cancelled or that the app is stopping.</param>
    /// <returns>A task that completes when the job is done.</returns>
    Task Handle(TJob job, JobContext context, CancellationToken cancellationToken);
}
