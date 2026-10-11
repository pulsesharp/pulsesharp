using Microsoft.Extensions.Logging;
using PulseSharp.Control;
using PulseSharp.Stores;

namespace PulseSharp.Delivery;

internal sealed class SubscriptionRunner(
    Subscription subscription,
    IPulseStore store,
    WakeUpSignal signal,
    ControlRequests requests,
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
            await ApplyControlRequests(cancellationToken).ConfigureAwait(false);
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

    private async Task ApplyControlRequests(CancellationToken cancellationToken)
    {
        while (requests.TryTake(subscription.Name, out var request))
        {
            try
            {
                await Apply(request, cancellationToken).ConfigureAwait(false);
                request.Completion.TrySetResult();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                request.Completion.TrySetCanceled(cancellationToken);
                throw;
            }
            catch (Exception exception)
            {
                request.Completion.TrySetException(exception);
            }
        }
    }

    private Task Apply(ControlRequest request, CancellationToken cancellationToken)
    {
        return request.Action switch
        {
            ControlAction.Retry => RetryStream(request.Stream!, cancellationToken),
            ControlAction.Skip => SkipWaitingEvent(request.Stream!, cancellationToken),
            _ => RewindAllStreams(cancellationToken)
        };
    }

    private Task RetryStream(string stream, CancellationToken cancellationToken)
    {
        var state = ExistingStateOf(stream);
        ClearFailure(state);
        return SaveCursor(state, cancellationToken);
    }

    private Task SkipWaitingEvent(string stream, CancellationToken cancellationToken)
    {
        var state = ExistingStateOf(stream);
        if (!state.Pending.TryDequeue(out var skipped))
        {
            throw new InvalidOperationException($"Handler '{subscription.Name}' has no waiting event in stream '{stream}'.");
        }

        state.Position = skipped.StreamPosition;
        ClearFailure(state);
        return SaveCursor(state, cancellationToken);
    }

    private async Task RewindAllStreams(CancellationToken cancellationToken)
    {
        _scanPosition = 0;
        foreach (var state in _streams.Values)
        {
            state.Pending.Clear();
            state.Position = 0;
            ClearFailure(state);
            await SaveCursor(state, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<DateTimeOffset?> DeliverPendingStreams(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var ready = new List<StreamState>();
        DateTimeOffset? nextRetryAt = null;

        foreach (var state in _streams.Values)
        {
            if (state.Pending.Count == 0)
            {
                continue;
            }

            if (state.IsWaitingAt(now))
            {
                nextRetryAt = Earliest(nextRetryAt, state.RetryAt);
                continue;
            }

            ready.Add(state);
        }

        var retries = await DeliverStreams(ready, cancellationToken).ConfigureAwait(false);
        foreach (var retryAt in retries)
        {
            nextRetryAt = Earliest(nextRetryAt, retryAt);
        }

        return nextRetryAt;
    }

    private async Task<DateTimeOffset?[]> DeliverStreams(List<StreamState> ready, CancellationToken cancellationToken)
    {
        var retries = new DateTimeOffset?[ready.Count];
        if (ready.Count == 1)
        {
            retries[0] = await DeliverStream(ready[0], cancellationToken).ConfigureAwait(false);
            return retries;
        }

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = settings.MaxParallelStreams,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(Enumerable.Range(0, ready.Count), options, async (index, token) =>
        {
            retries[index] = await DeliverStream(ready[index], token).ConfigureAwait(false);
        }).ConfigureAwait(false);

        return retries;
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
            ClearFailure(state);
            await SaveCursor(state, CancellationToken.None).ConfigureAwait(false);
        }

        return null;
    }

    private Task SaveCursor(StreamState state, CancellationToken cancellationToken)
    {
        return store.SaveCursor(state.ToCursor(subscription.Name), cancellationToken);
    }

    private StreamState ExistingStateOf(string stream)
    {
        if (!_streams.TryGetValue(stream, out var state))
        {
            throw new InvalidOperationException($"Handler '{subscription.Name}' has no cursor for stream '{stream}'.");
        }

        return state;
    }

    private static void ClearFailure(StreamState state)
    {
        state.Status = CursorState.Active;
        state.Attempts = 0;
        state.LastError = null;
        state.RetryAt = null;
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
            state.Status = CursorState.Failed;
            state.RetryAt = null;
            Log.HandlerStopped(logger, failure, subscription.Name, stored.EventId, stored.Stream, state.Attempts);
            await store.SaveCursor(state.ToCursor(subscription.Name), CancellationToken.None).ConfigureAwait(false);
            return null;
        }

        var retryAt = time.GetUtcNow() + settings.RetryDelayFor(state.Attempts);
        state.Status = CursorState.Retrying;
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
        await store.SaveCursor(state.ToCursor(subscription.Name), CancellationToken.None).ConfigureAwait(false);
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
