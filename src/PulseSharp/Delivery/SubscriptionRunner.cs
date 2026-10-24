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
    private readonly string _workerId = WorkerIds.Create(settings.Node);
    private readonly CursorBatch _unsaved = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private long _scanPosition;
    private long? _revision;
    private CancellationTokenSource? _delivery;

    public async Task Run(CancellationToken cancellationToken)
    {
        await Task.Yield();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var wakeUp = signal.Next;
                var nextRetryAt = await DeliverWhileLeased(cancellationToken).ConfigureAwait(false);
                await WaitForWork(wakeUp, nextRetryAt, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseLease().ConfigureAwait(false);
        }
    }

    private async Task<DateTimeOffset?> DeliverWhileLeased(CancellationToken cancellationToken)
    {
        try
        {
            if (!await KeepLease(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            using var delivery = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var heartbeatStop = new CancellationTokenSource();
            _delivery = delivery;
            var heartbeat = RenewWhileDelivering(delivery, heartbeatStop.Token);

            try
            {
                return await DeliverAvailableEvents(delivery.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Forget();
                return null;
            }
            finally
            {
                _delivery = null;
                await heartbeatStop.CancelAsync().ConfigureAwait(false);
                await heartbeat.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Log.RunnerError(logger, exception, subscription.Name);
            Forget();
            return null;
        }
    }

    private async Task<bool> KeepLease(CancellationToken cancellationToken)
    {
        var lease = await store
            .LeaseHandler(subscription.Name, _workerId, time.GetUtcNow(), settings.HandlerLease, cancellationToken)
            .ConfigureAwait(false);

        if (!lease.Held && WorkerIds.IsGoneFromThisMachine(lease.Owner))
        {
            Log.HandlerTakenOver(logger, subscription.Name, lease.Owner!);
            await store.ReleaseHandler(subscription.Name, lease.Owner!, cancellationToken).ConfigureAwait(false);
            lease = await store
                .LeaseHandler(subscription.Name, _workerId, time.GetUtcNow(), settings.HandlerLease, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!lease.Held)
        {
            Forget();
            return false;
        }

        while (_revision != lease.Revision)
        {
            Forget();
            await LoadCursors(cancellationToken).ConfigureAwait(false);
            _revision = lease.Revision;

            lease = await store
                .LeaseHandler(subscription.Name, _workerId, time.GetUtcNow(), settings.HandlerLease, cancellationToken)
                .ConfigureAwait(false);

            if (!lease.Held)
            {
                Forget();
                return false;
            }
        }

        return true;
    }

    private async Task RenewWhileDelivering(CancellationTokenSource delivery, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(settings.PollInterval, time, stop).ConfigureAwait(false);
                var lease = await store
                    .LeaseHandler(subscription.Name, _workerId, time.GetUtcNow(), settings.HandlerLease, stop)
                    .ConfigureAwait(false);

                if (!lease.Held || lease.Revision != _revision)
                {
                    await delivery.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return;
        }
    }

    private async Task ReleaseLease()
    {
        if (_revision is null)
        {
            return;
        }

        try
        {
            await store.ReleaseHandler(subscription.Name, _workerId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.RunnerError(logger, exception, subscription.Name);
        }
    }

    private void Forget()
    {
        _revision = null;
        _scanPosition = 0;
        _streams.Clear();
        _unsaved.Clear();
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
        await ReadNewEvents(cancellationToken).ConfigureAwait(false);
        return await DeliverPendingStreams(cancellationToken).ConfigureAwait(false);
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

        try
        {
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
        finally
        {
            await FlushCursors().ConfigureAwait(false);
        }
    }

    private async Task<DateTimeOffset?> DeliverStream(StreamState state, CancellationToken cancellationToken)
    {
        while (state.Pending.TryPeek(out var stored))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var failure = await TryHandle(stored, state, cancellationToken).ConfigureAwait(false);
            if (failure is not null)
            {
                return await RecordFailure(stored, state, failure, cancellationToken).ConfigureAwait(false);
            }

            state.Pending.Dequeue();
            state.Position = stored.StreamPosition;
            ClearFailure(state);

            if (_unsaved.Add(state.ToCursor(subscription.Name)))
            {
                await FlushCursors().ConfigureAwait(false);
            }
        }

        return null;
    }

    private async Task FlushCursors()
    {
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var cursors = _unsaved.TakeAll();
            if (cursors.Count == 0)
            {
                return;
            }

            var saved = cursors.Count == 1
                ? await store.SaveCursor(cursors[0], _revision ?? -1, CancellationToken.None).ConfigureAwait(false)
                : await store.SaveCursors(cursors, _revision ?? -1, CancellationToken.None).ConfigureAwait(false);

            if (!saved && _delivery is { } delivery)
            {
                await delivery.CancelAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task SaveCursor(StreamState state)
    {
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _unsaved.Discard(state.Stream);
            var saved = await store
                .SaveCursor(state.ToCursor(subscription.Name), _revision ?? -1, CancellationToken.None)
                .ConfigureAwait(false);

            if (!saved && _delivery is { } delivery)
            {
                await delivery.CancelAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _saveGate.Release();
        }
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
            await TimeLimit
                .Run(
                    token => invoke(services, stored.Data, context, token),
                    settings.HandlerTimeout,
                    settings.PollInterval * 10,
                    $"Handler '{subscription.Name}'",
                    "Advanced.HandlerTimeout",
                    time,
                    cancellationToken
                )
                .ConfigureAwait(false);
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
            await SaveCursor(state).ConfigureAwait(false);
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
        await SaveCursor(state).ConfigureAwait(false);
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
