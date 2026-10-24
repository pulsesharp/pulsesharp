namespace PulseSharp;

/// <summary>
/// Settings most apps never need to change. The defaults suit desktop apps, web apps and workers alike.
/// </summary>
public sealed class PulseSharpAdvanced
{
    internal PulseSharpAdvanced()
    {
    }

    /// <summary>
    /// How often handlers and jobs check for new work when nothing wakes them up. The default is 1 second.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How many streams one handler works on at the same time. Events of one stream are always handled
    /// in order, one at a time. The default is 8.
    /// </summary>
    public int MaxParallelStreams { get; set; } = 8;

    /// <summary>
    /// How many jobs this app instance runs at the same time. The default is 4.
    /// </summary>
    public int MaxParallelJobs { get; set; } = 4;

    /// <summary>
    /// How long one handler may take for one event. When the time is up, the handler's cancellation token is cancelled; if the
    /// handler has not stopped 10 seconds later, PulseSharp stops waiting, counts the attempt as failed and retries as usual,
    /// so a handler that hangs can never block its stream for good. The default is 5 minutes; <see langword="null"/> sets no limit.
    /// </summary>
    public TimeSpan? HandlerTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long one run of a job may take. It works like <see cref="HandlerTimeout"/>: the job is cancelled, then abandoned,
    /// and the attempt counts as failed. The default is 1 hour; <see langword="null"/> sets no limit.
    /// </summary>
    public TimeSpan? JobTimeout { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How long to keep jobs that succeeded or were cancelled. After that, the app instance that runs jobs deletes them,
    /// so the job table does not grow forever. Failed jobs are kept until they are retried. The default is 7 days;
    /// <see langword="null"/> keeps every job.
    /// </summary>
    public TimeSpan? KeepFinishedJobs { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// The JSON options used to store events and jobs. The default uses <see cref="System.Text.Json.JsonSerializerDefaults.Web"/>.
    /// For trimming and Native AOT, set options whose <c>TypeInfoResolver</c> is your source-generated <c>JsonSerializerContext</c>.
    /// </summary>
    public System.Text.Json.JsonSerializerOptions? JsonOptions { get; set; }
}
