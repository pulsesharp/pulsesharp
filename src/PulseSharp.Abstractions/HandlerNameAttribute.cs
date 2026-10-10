namespace PulseSharp;

/// <summary>
/// Sets the stable name of an event handler class. Without it, the class name is used.
/// </summary>
/// <remarks>
/// PulseSharp stores how far each handler has got under this name. Pin it before you rename
/// or move the class, and the stored progress is kept.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class HandlerNameAttribute : Attribute
{
    /// <summary>
    /// Sets the stable name of the handler.
    /// </summary>
    /// <param name="name">The name, for example <c>update-stock</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or only whitespace.</exception>
    public HandlerNameAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>The stable name of the handler.</summary>
    public string Name { get; }
}
