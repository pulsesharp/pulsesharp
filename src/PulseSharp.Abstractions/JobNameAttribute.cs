namespace PulseSharp;

/// <summary>
/// Sets the stored name of a job type. Without it, the class name is used.
/// </summary>
/// <remarks>
/// Stored jobs are matched to their C# type by this name. Pin it before you rename
/// or move the class, and waiting jobs still run.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
public sealed class JobNameAttribute : Attribute
{
    /// <summary>
    /// Sets the stored name of the job type.
    /// </summary>
    /// <param name="name">The name, for example <c>import-products</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or only whitespace.</exception>
    public JobNameAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>The stored name of the job type.</summary>
    public string Name { get; }
}
