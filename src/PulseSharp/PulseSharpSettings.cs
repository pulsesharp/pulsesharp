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
    IReadOnlyList<Subscription> Subscriptions,
    IReadOnlyDictionary<string, JobRegistration> Jobs
)
{
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);

    public TimeSpan RetryDelayFor(int attempt)
    {
        var delay = RetryDelay * Math.Pow(2, attempt - 1);
        return delay < MaxRetryDelay ? delay : MaxRetryDelay;
    }
}
