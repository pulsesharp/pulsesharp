using PulseSharp.Delivery;
using PulseSharp.Stores;

namespace PulseSharp.Control;

internal sealed class PulseControl(
    PulseSharpSettings settings,
    IPulseStore store,
    WakeUpSignal signal,
    TimeProvider time
) : IPulseControl
{
    private const int StreamPageSize = 100;

    public async Task<JobInfo?> GetJob(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await store.GetJob(jobId, cancellationToken).ConfigureAwait(false);
        return job?.ToJobInfo();
    }

    public async Task<IReadOnlyList<JobInfo>> GetJobs(JobState? state = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var jobs = await store.GetJobs(state, limit, cancellationToken).ConfigureAwait(false);
        return jobs.Select(job => job.ToJobInfo()).ToList();
    }

    public Task<bool> CancelJob(Guid jobId, CancellationToken cancellationToken = default)
    {
        return store.RequestCancel(jobId, time.GetUtcNow(), cancellationToken);
    }

    public async Task<bool> RetryJob(Guid jobId, CancellationToken cancellationToken = default)
    {
        var willRun = await store.RetryJob(jobId, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (willRun)
        {
            signal.Notify();
        }

        return willRun;
    }

    public async Task<IReadOnlyList<HandlerCursor>> GetCursors(string? handlerName = null, CancellationToken cancellationToken = default)
    {
        var handlerNames = handlerName is null
            ? settings.Subscriptions.Select(subscription => subscription.Name).ToList()
            : [KnownHandler(handlerName).Name];

        var cursors = new List<HandlerCursor>();
        foreach (var name in handlerNames)
        {
            cursors.AddRange(await store.LoadCursors(name, cancellationToken).ConfigureAwait(false));
        }

        return cursors
            .OrderBy(cursor => cursor.HandlerName, StringComparer.Ordinal)
            .ThenBy(cursor => cursor.Stream, StringComparer.Ordinal)
            .ToList();
    }

    public async Task Retry(string handlerName, string stream, CancellationToken cancellationToken = default)
    {
        var cursor = await ExistingCursor(KnownHandler(handlerName), stream, cancellationToken).ConfigureAwait(false);
        await Change([Cleared(cursor)], cursor.HandlerName, cancellationToken).ConfigureAwait(false);
    }

    public async Task Skip(string handlerName, string stream, CancellationToken cancellationToken = default)
    {
        var subscription = KnownHandler(handlerName);
        var cursor = await ExistingCursor(subscription, stream, cancellationToken).ConfigureAwait(false);
        var waiting = await NextHandledEvent(subscription, cursor, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Handler '{handlerName}' has no waiting event in stream '{stream}'.");

        await Change([Cleared(cursor) with { Position = waiting.StreamPosition }], subscription.Name, cancellationToken).ConfigureAwait(false);
    }

    public async Task Rewind(string handlerName, CancellationToken cancellationToken = default)
    {
        var subscription = KnownHandler(handlerName);
        var cursors = await store.LoadCursors(subscription.Name, cancellationToken).ConfigureAwait(false);
        var rewound = cursors.Select(cursor => Cleared(cursor) with { Position = 0 }).ToList();
        await Change(rewound, subscription.Name, cancellationToken).ConfigureAwait(false);
    }

    private async Task Change(IReadOnlyList<HandlerCursor> cursors, string handlerName, CancellationToken cancellationToken)
    {
        await store.ChangeCursors(handlerName, cursors, cancellationToken).ConfigureAwait(false);
        signal.Notify();
    }

    private async Task<HandlerCursor> ExistingCursor(Subscription subscription, string stream, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stream);
        var cursors = await store.LoadCursors(subscription.Name, cancellationToken).ConfigureAwait(false);
        return cursors.FirstOrDefault(cursor => cursor.Stream == stream)
            ?? throw new InvalidOperationException($"Handler '{subscription.Name}' has no cursor for stream '{stream}'.");
    }

    private async Task<StoredEvent?> NextHandledEvent(Subscription subscription, HandlerCursor cursor, CancellationToken cancellationToken)
    {
        var after = cursor.Position;
        while (true)
        {
            var page = await store.ReadStream(cursor.Stream, after, StreamPageSize, cancellationToken).ConfigureAwait(false);
            var handled = page.FirstOrDefault(stored => subscription.Handles(stored.EventName));
            if (handled is not null || page.Count < StreamPageSize)
            {
                return handled;
            }

            after = page[^1].StreamPosition;
        }
    }

    private Subscription KnownHandler(string handlerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handlerName);
        return settings.Subscriptions.FirstOrDefault(subscription => subscription.Name == handlerName)
            ?? throw new ArgumentException($"No handler is named '{handlerName}'.", nameof(handlerName));
    }

    private static HandlerCursor Cleared(HandlerCursor cursor)
    {
        return cursor with { State = CursorState.Active, Attempts = 0, LastError = null, RetryAt = null };
    }
}
