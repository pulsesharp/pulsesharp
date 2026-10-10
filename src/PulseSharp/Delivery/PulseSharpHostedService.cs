using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PulseSharp.Stores;

namespace PulseSharp.Delivery;

internal sealed class PulseSharpHostedService(
    PulseSharpSettings settings,
    IPulseStore store,
    WakeUpSignal signal,
    IServiceProvider services,
    TimeProvider time,
    ILoggerFactory loggerFactory
) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var logger = loggerFactory.CreateLogger("PulseSharp");
        var runs = settings.Subscriptions
            .Select(subscription => new SubscriptionRunner(subscription, store, signal, settings, services, time, logger))
            .Select(runner => runner.Run(stoppingToken));

        return Task.WhenAll(runs);
    }
}
