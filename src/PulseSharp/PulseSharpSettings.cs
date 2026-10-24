using PulseSharp.Delivery;
using PulseSharp.Jobs;

namespace PulseSharp;

internal sealed record PulseSharpSettings(
    string Node,
    int MaxAttempts,
    TimeSpan RetryDelay,
    TimeSpan PollInterval,
    int MaxParallelStreams,
    int MaxParallelJobs,
    TimeSpan JobLease,
    TimeSpan HandlerLease,
    TimeSpan? KeepFinishedJobs,
    TimeSpan? HandlerTimeout,
    TimeSpan? JobTimeout,
    IReadOnlyList<Subscription> Subscriptions,
    IReadOnlyDictionary<string, JobRegistration> Jobs
)
{
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A claim is renewed every poll interval, so it only runs out when its holder stopped renewing: 10 missed renewals
    /// (at least 10 seconds) for a handler, 30 (at least 30 seconds) for a job.
    /// </summary>
    public static TimeSpan LeaseFor(TimeSpan pollInterval, int missedRenewals)
    {
        var lease = pollInterval * missedRenewals;
        var minimum = TimeSpan.FromSeconds(missedRenewals);
        return lease > minimum ? lease : minimum;
    }

    public TimeSpan RetryDelayFor(int attempt)
    {
        var delay = RetryDelay * Math.Pow(2, attempt - 1);
        return delay < MaxRetryDelay ? delay : MaxRetryDelay;
    }
}
