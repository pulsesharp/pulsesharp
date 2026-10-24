using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PulseSharp.Jobs;
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
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await store.Initialize(cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var logger = loggerFactory.CreateLogger("PulseSharp");
        Log.Ready(logger, store.GetType().Name, settings.Node, settings.Subscriptions.Count, settings.Jobs.Count);
        var runs = settings.Subscriptions
            .Select(subscription => new SubscriptionRunner(subscription, store, signal, settings, services, time, logger))
            .Select(runner => runner.Run(stoppingToken))
            .ToList();

        if (settings.Jobs.Count > 0)
        {
            runs.Add(new JobRunner(store, signal, settings, services, time, logger).Run(stoppingToken));
        }

        if (store is INotifyingStore notifying)
        {
            runs.Add(Listen(notifying, logger, stoppingToken));
        }

        return Task.WhenAll(runs);
    }

    private async Task Listen(INotifyingStore notifying, ILogger logger, CancellationToken stoppingToken)
    {
        await Task.Yield();

        try
        {
            await notifying.ListenForChanges(signal.Notify, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log.ListenerError(logger, exception);
        }
    }
}
