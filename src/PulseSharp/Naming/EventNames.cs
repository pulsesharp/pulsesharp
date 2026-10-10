using System.Collections.Concurrent;
using System.Reflection;

namespace PulseSharp.Naming;

internal sealed class EventNames
{
    private readonly ConcurrentDictionary<Type, string> _namesByType = new();
    private readonly ConcurrentDictionary<string, Type> _typesByName = new(StringComparer.Ordinal);

    public string NameOf(Type eventType)
    {
        if (_namesByType.TryGetValue(eventType, out var known))
        {
            return known;
        }

        var name = eventType.GetCustomAttribute<EventNameAttribute>()?.Name ?? eventType.Name;
        var owner = _typesByName.GetOrAdd(name, eventType);
        if (owner != eventType)
        {
            throw new InvalidOperationException(
                $"The event name '{name}' is used by both {owner.FullName} and {eventType.FullName}. " +
                $"Give one of them a unique name with [EventName(\"...\")]."
            );
        }

        _namesByType[eventType] = name;
        return name;
    }
}
