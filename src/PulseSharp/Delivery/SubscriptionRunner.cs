using Microsoft.Extensions.Logging;
using PulseSharp.Stores;

namespace PulseSharp.Delivery;

internal sealed class SubscriptionRunner(
    Subscription subscription,
    IPulseStore store,
    WakeUpSignal signal,
    PulseSharpSettings settings,
    IServiceProvider services,
    TimeProvider time,
    ILogger logger
)
{
    private const int ReadBatchSize = 500;

    private readonly Dictionary<string, StreamState> _streams = new(StringComparer.Ordinal);
    private long _scanPosition;

    public async Task Run(CancellationToken cancellationToken)
    {
        await Task.Yield();

        try
        {
            await LoadCursors(cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                var wakeUp = signal.Next;
                var nextRetryAt = await DeliverAvailableEvents(cancellationToken).ConfigureAwait(false);
                await WaitForWork(wakeUp, nextRetryAt, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
    }

    private async Task LoadCursors(CancellationToken cancellationToken)
    {
        var cursors = await store.LoadCursors(subscription.Name, cancellationToken).ConfigureAwait(false);
        foreach (var cursor in cursors)
        {
            _streams[cursor.Stream] = StreamState.From(cursor);
        }
    }

    private async Task<DateTimeOffset?> DeliverAvailableEvents(CancellationToken cancellationToken)
    {
        try
        {
            await ReadNewEvents(cancellationToken).ConfigureAwait(false);
            return await DeliverPendingStreams(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Log.RunnerError(logger, exception, subscription.Name);
            return null;
        }
    }

    private async Task ReadNewEvents(CancellationToken cancellationToken)
    {
        while (true)
        {
            var batch = await store
                .ReadAfter(_scanPosition, subscription.EventNames, ReadBatchSize, cancellationToken)
                .ConfigureAwait(false);

            foreach (var stored in batch)
            {
                _scanPosition = stored.GlobalPosition;
                var state = StateOf(stored.Stream);
                if (stored.StreamPosition > state.Position)
                {
                    state.Pending.Enqueue(stored);
                }
            }

            if (batch.Count < ReadBatchSize)
            {
                return;
            }
        }
    }

    private async Task<DateTimeOffset?> DeliverPendingStreams(CancellationToken cancellationToken)
    {
        DateTimeOffset? nextRetryAt = null;

        foreach (var state in _streams.Values)
        {
            if (state.Pending.Count == 0)
            {
                continue;
            }

            if (state.IsWaitingAt(time.GetUtcNow()))
            {
                nextRetryAt = Earliest(nextRetryAt, state.RetryAt);
                continue;
            }

            var retryAt = await DeliverStream(state, cancellationToken).ConfigureAwait(false);
            nextRetryAt = Earliest(nextRetryAt, retryAt);
        }

        return nextRetryAt;
    }

    private async Task<DateTimeOffset?> DeliverStream(StreamState state, CancellationToken cancellationToken)
    {
        while (state.Pending.TryPeek(out var stored))
        {
            var failure = await TryHandle(stored, state, cancellationToken).ConfigureAwait(false);
            if (failure is not null)
            {
                return await RecordFailure(stored, state, failure, cancellationToken).ConfigureAwait(false);
            }

            state.Pending.Dequeue();
            state.Position = stored.StreamPosition;
            state.Status = CursorStatus.Active;
            state.Attempts = 0;
            state.LastError = null;
            state.RetryAt = null;
            await store.SaveCursor(state.ToCursor(subscription.Name), cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<Exception?> TryHandle(StoredEvent stored, StreamState state, CancellationToken cancellationToken)
    {
        var context = new EventContext
        {
            EventId = stored.EventId,
            EventName = stored.EventName,
            Stream = stored.Stream,
            StreamPosition = stored.StreamPosition,
            Origin = stored.Origin,
            EmittedAt = stored.EmittedAt,
            HandlerName = subscription.Name,
            Attempt = state.Attempts + 1
        };

        try
        {
            var invoke = subscription.InvokerFor(stored.EventName);
            await invoke(services, stored.Data, context, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private async Task<DateTimeOffset?> RecordFailure(
        StoredEvent stored,
        StreamState state,
        Exception failure,
        CancellationToken cancellationToken
    )
    {
        state.Attempts++;
        state.LastError = failure.ToString();

        if (state.Attempts >= settings.MaxAttempts)
        {
            state.Status = CursorStatus.Failed;
            state.RetryAt = null;
            Log.HandlerStopped(logger, failure, subscription.Name, stored.EventId, stored.Stream, state.Attempts);
            await store.SaveCursor(state.ToCursor(subscription.Name), cancellationToken).ConfigureAwait(false);
            return null;
        }

        var retryAt = time.GetUtcNow() + settings.RetryDelayFor(state.Attempts);
        state.Status = CursorStatus.Retrying;
        state.RetryAt = retryAt;
        Log.HandlerWillRetry(
            logger,
            failure,
            subscription.Name,
            stored.EventId,
            stored.Stream,
            state.Attempts,
            settings.MaxAttempts,
            retryAt
        );
        await store.SaveCursor(state.ToCursor(subscription.Name), cancellationToken).ConfigureAwait(false);
        return retryAt;
    }

    private async Task WaitForWork(Task wakeUp, DateTimeOffset? nextRetryAt, CancellationToken cancellationToken)
    {
        var timeout = TimeUntilNextCheck(nextRetryAt);
        if (timeout <= TimeSpan.Zero)
        {
            return;
        }

        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(timeout, time, delayCancellation.Token);
        await Task.WhenAny(wakeUp, delay).ConfigureAwait(false);
        await delayCancellation.CancelAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private TimeSpan TimeUntilNextCheck(DateTimeOffset? nextRetryAt)
    {
        if (nextRetryAt is not { } retryAt)
        {
            return settings.PollInterval;
        }

        var untilRetry = retryAt - time.GetUtcNow();
        return untilRetry < settings.PollInterval ? untilRetry : settings.PollInterval;
    }

    private StreamState StateOf(string stream)
    {
        if (!_streams.TryGetValue(stream, out var state))
        {
            state = new StreamState(stream);
            _streams.Add(stream, state);
        }

        return state;
    }

    private static DateTimeOffset? Earliest(DateTimeOffset? first, DateTimeOffset? second)
    {
        if (first is null)
        {
            return second;
        }

        if (second is null)
        {
            return first;
        }

        return first < second ? first : second;
    }
}
