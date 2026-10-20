namespace PulseSharp;

/// <summary>
/// The state and progress of one job.
/// </summary>
/// <param name="JobId">The id returned by <see cref="IPulse.Enqueue{TJob}(TJob, CancellationToken)"/>.</param>
/// <param name="JobName">The stored name of the job type.</param>
/// <param name="State">The state of the job.</param>
/// <param name="Attempts">How many times the job has started.</param>
/// <param name="Done">The progress the handler last reported: items finished.</param>
/// <param name="Total">The progress the handler last reported: items in total, or 0 when unknown.</param>
/// <param name="LastError">The error of the last failed attempt.</param>
/// <param name="EnqueuedAt">When the job was enqueued.</param>
/// <param name="RunAt">The earliest time the job runs, or runs again after a failure.</param>
/// <param name="StartedAt">When the latest attempt started.</param>
/// <param name="FinishedAt">When the job succeeded, failed for good, or was cancelled.</param>
public sealed record JobInfo(
    Guid JobId,
    string JobName,
    JobState State,
    int Attempts,
    int Done,
    int Total,
    string? LastError,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset RunAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt
);
