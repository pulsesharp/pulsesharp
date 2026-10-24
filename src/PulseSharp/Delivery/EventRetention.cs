using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PulseSharp.Stores;

namespace PulseSharp.Delivery;

/// <summary>
/// Deletes old events that every handler has handled (and that were sent, when the app syncs), when
/// <c>Advanced.KeepEvents</c> is set. It runs about once an hour, in batches.
/// </summary>
internal sealed class EventRetention(
    IPulseStore store,
    PulseSharpSettings settings,
    IServiceProvider services,
    TimeProvider time,
    ILogger logger
)
{
    private const int BatchSize = 1000;
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(1);

    public async Task Run(CancellationToken stoppingToken)
    {
        await Task.Yield();
        if (settings.KeepEvents is not { } keep)
        {
            return;
        }

        var every = keep < CheckEvery ? keep : CheckEvery;
        if (every < settings.PollInterval)
        {
            every = settings.PollInterval;
        }

        var listeners = settings.Subscriptions
            .SelectMany(subscription => subscription.EventNames.Select(name => new EventListener(subscription.Name, name)))
            .ToList();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DeleteOldEvents(keep, listeners, stoppingToken).ConfigureAwait(false);
                await Task.Delay(every, time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                Log.EventCleanUpError(logger, exception);
                await WaitBeforeTryingAgain(every, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task WaitBeforeTryingAgain(TimeSpan every, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(every, time, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task DeleteOldEvents(TimeSpan keep, IReadOnlyList<EventListener> listeners, CancellationToken stoppingToken)
    {
        var sentUpTo = await OwnEventsSentUpTo(stoppingToken).ConfigureAwait(false);
        var before = time.GetUtcNow() - keep;
        var deleted = 0;
        int batch;
        do
        {
            batch = await store.DeleteHandledEvents(before, listeners, settings.Node, sentUpTo, BatchSize, stoppingToken).ConfigureAwait(false);
            deleted += batch;
        }
        while (batch == BatchSize);

        if (deleted > 0)
        {
            Log.EventsDeleted(logger, deleted);
        }
    }

    private async Task<long?> OwnEventsSentUpTo(CancellationToken stoppingToken)
    {
        long? lowest = null;
        foreach (var guard in services.GetServices<IRetentionGuard>())
        {
            if (await guard.OwnEventsSentUpTo(stoppingToken).ConfigureAwait(false) is { } position)
            {
                lowest = lowest is null ? position : Math.Min(lowest.Value, position);
            }
        }

        return lowest;
    }
}
