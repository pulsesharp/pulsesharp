using PulseSharp.Delivery;
using PulseSharp.Stores;

namespace PulseSharp.Control;

internal sealed class PulseControl(
    PulseSharpSettings settings,
    IPulseStore store,
    ControlRequests requests,
    WakeUpSignal signal,
    TimeProvider time
) : IPulseControl
{
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
            : [KnownHandler(handlerName)];

        var cursors = new List<HandlerCursor>();
        foreach (var name in handlerNames)
        {
            var stored = await store.LoadCursors(name, cancellationToken).ConfigureAwait(false);
            cursors.AddRange(stored);
        }

        return cursors
            .OrderBy(cursor => cursor.HandlerName, StringComparer.Ordinal)
            .ThenBy(cursor => cursor.Stream, StringComparer.Ordinal)
            .ToList();
    }

    public Task Retry(string handlerName, string stream, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stream);
        return Send(handlerName, new ControlRequest(ControlAction.Retry, stream), cancellationToken);
    }

    public Task Skip(string handlerName, string stream, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stream);
        return Send(handlerName, new ControlRequest(ControlAction.Skip, stream), cancellationToken);
    }

    public Task Rewind(string handlerName, CancellationToken cancellationToken = default)
    {
        return Send(handlerName, new ControlRequest(ControlAction.Rewind, null), cancellationToken);
    }

    private async Task Send(string handlerName, ControlRequest request, CancellationToken cancellationToken)
    {
        requests.Add(KnownHandler(handlerName), request);
        signal.Notify();
        await request.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private string KnownHandler(string handlerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handlerName);
        if (!settings.Subscriptions.Any(subscription => subscription.Name == handlerName))
        {
            throw new ArgumentException($"No handler is named '{handlerName}'.", nameof(handlerName));
        }

        return handlerName;
    }
}
