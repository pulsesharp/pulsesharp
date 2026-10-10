namespace PulseSharp.Delivery;

internal sealed class WakeUpSignal
{
    private TaskCompletionSource _current = NewSource();

    public Task Next
    {
        get
        {
            return Volatile.Read(ref _current).Task;
        }
    }

    public void Notify()
    {
        var previous = Interlocked.Exchange(ref _current, NewSource());
        previous.TrySetResult();
    }

    private static TaskCompletionSource NewSource()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
