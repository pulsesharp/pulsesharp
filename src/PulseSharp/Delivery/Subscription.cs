namespace PulseSharp.Delivery;

internal sealed class Subscription(string name, object source)
{
    private readonly Dictionary<string, EventInvoker> _invokers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _eventNames = new(StringComparer.Ordinal);

    public string Name { get; } = name;

    public object Source { get; } = source;

    public IReadOnlySet<string> EventNames
    {
        get
        {
            return _eventNames;
        }
    }

    public bool Handles(string eventName)
    {
        return _eventNames.Contains(eventName);
    }

    public void Add(string eventName, EventInvoker invoker)
    {
        _invokers[eventName] = invoker;
        _eventNames.Add(eventName);
    }

    public EventInvoker InvokerFor(string eventName)
    {
        return _invokers[eventName];
    }
}
