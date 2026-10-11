using System.Collections.Concurrent;

namespace PulseSharp.Control;

internal enum ControlAction
{
    Retry = 0,
    Skip = 1,
    Rewind = 2
}

internal sealed class ControlRequest(ControlAction action, string? stream)
{
    public ControlAction Action { get; } = action;

    public string? Stream { get; } = stream;

    public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class ControlRequests
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<ControlRequest>> _queues = new(StringComparer.Ordinal);

    public void Add(string handlerName, ControlRequest request)
    {
        _queues.GetOrAdd(handlerName, _ => new ConcurrentQueue<ControlRequest>()).Enqueue(request);
    }

    public bool TryTake(string handlerName, out ControlRequest request)
    {
        if (_queues.TryGetValue(handlerName, out var queue) && queue.TryDequeue(out var next))
        {
            request = next;
            return true;
        }

        request = null!;
        return false;
    }
}
