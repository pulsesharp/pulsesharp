using System.Data.Common;

namespace PulseSharp;

/// <summary>
/// Options for <see cref="IPulse.Enqueue{TJob}(TJob, EnqueueOptions, CancellationToken)"/>.
/// </summary>
public sealed class EnqueueOptions
{
    /// <summary>
    /// The earliest time the job may run. When it is not set, the job runs as soon as a worker is free.
    /// </summary>
    public DateTimeOffset? RunAt { get; init; }

    /// <summary>
    /// A database transaction to save the job in. The job is then saved together with your own data,
    /// and runs only after the transaction commits. Stores that do not use a database ignore it.
    /// </summary>
    public DbTransaction? Transaction { get; init; }
}
