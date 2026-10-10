namespace PulseSharp;

/// <summary>
/// Sets the stored name of an event type. Without it, the class name is used.
/// </summary>
/// <remarks>
/// Stored events are matched to their C# type by this name. Pin it before you rename
/// or move the class, and stored events still load.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
public sealed class EventNameAttribute : Attribute
{
    /// <summary>
    /// Sets the stored name of the event type.
    /// </summary>
    /// <param name="name">The name, for example <c>sale-completed</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or only whitespace.</exception>
    public EventNameAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>The stored name of the event type.</summary>
    public string Name { get; }
}
