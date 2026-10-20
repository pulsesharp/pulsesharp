using System.Collections.Concurrent;
using System.Reflection;

namespace PulseSharp.Naming;

internal sealed class TypeNames
{
    private readonly ConcurrentDictionary<Type, string> _namesByType = new();
    private readonly ConcurrentDictionary<string, Type> _typesByName = new(StringComparer.Ordinal);
    private readonly string _kind;
    private readonly string _attribute;
    private readonly Func<Type, string?> _nameFromAttribute;

    private TypeNames(string kind, string attribute, Func<Type, string?> nameFromAttribute)
    {
        _kind = kind;
        _attribute = attribute;
        _nameFromAttribute = nameFromAttribute;
    }

    public static TypeNames ForEvents()
    {
        return new TypeNames("event", "EventName", type => type.GetCustomAttribute<EventNameAttribute>()?.Name);
    }

    public static TypeNames ForJobs()
    {
        return new TypeNames("job", "JobName", type => type.GetCustomAttribute<JobNameAttribute>()?.Name);
    }

    public Type? TypeOf(string name)
    {
        return _typesByName.GetValueOrDefault(name);
    }

    public string NameOf(Type type)
    {
        if (_namesByType.TryGetValue(type, out var known))
        {
            return known;
        }

        var name = _nameFromAttribute(type) ?? type.Name;
        var owner = _typesByName.GetOrAdd(name, type);
        if (owner != type)
        {
            throw new InvalidOperationException(
                $"The {_kind} name '{name}' is used by both {owner.FullName} and {type.FullName}. " +
                $"Give one of them a unique name with [{_attribute}(\"...\")]."
            );
        }

        _namesByType[type] = name;
        return name;
    }
}

internal sealed record PulseNames(TypeNames Events, TypeNames Jobs);
