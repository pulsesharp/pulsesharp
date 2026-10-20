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
    /// in order, one at a time. The default is 4.
    /// </summary>
    public int MaxParallelStreams { get; set; } = 4;

    /// <summary>
    /// How many jobs this app instance runs at the same time. The default is 4.
    /// </summary>
    public int MaxParallelJobs { get; set; } = 4;

    /// <summary>
    /// How long a running job stays claimed without a heartbeat. The runner renews the claim every poll interval;
    /// when an app instance stops unexpectedly, its jobs are picked up again after this time. The default is 5 minutes.
    /// </summary>
    public TimeSpan JobLease { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long one app instance keeps a handler without renewing its claim. Only the holder delivers that handler's
    /// events; when an instance stops unexpectedly, another takes over after this time. The default is 30 seconds.
    /// </summary>
    public TimeSpan HandlerLease { get; set; } = TimeSpan.FromSeconds(30);

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
