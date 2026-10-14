namespace PulseSharp;

/// <summary>
/// The name of a stream: an ordered list of events. Events of one stream reach each handler in order.
/// </summary>
/// <remarks>
/// By convention a stream name is <c>{Category}-{Id}</c>, for example <c>Sale-7f3a</c>.
/// Derive one record per kind of stream, or use <see cref="From(string)"/> for a plain name.
/// </remarks>
/// <example>
/// <code>
/// public sealed record SaleStream(Guid SaleId) : StreamName
/// {
///     public override string Value()
///     {
///         return $"Sale-{SaleId}";
///     }
/// }
/// </code>
/// </example>
public abstract record StreamName
{
    /// <summary>
    /// Returns the stream name as it is stored.
    /// </summary>
    /// <returns>The stream name.</returns>
    public abstract string Value();

    /// <summary>
    /// Creates a stream name from a plain string.
    /// </summary>
    /// <param name="value">The stream name, for example <c>Sale-7f3a</c>.</param>
    /// <returns>The stream name.</returns>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or only whitespace.</exception>
    public static StreamName From(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new PlainStreamName(value);
    }

    /// <summary>
    /// Lets a plain string be used as a stream name, for example <c>pulse.Emit("Sale-" + id, sale)</c>.
    /// </summary>
    /// <param name="value">The stream name.</param>
    /// <returns>The stream name.</returns>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or only whitespace.</exception>
    public static implicit operator StreamName(string value)
    {
        return From(value);
    }

    /// <summary>
    /// Returns the stream name as it is stored.
    /// </summary>
    /// <returns>The stream name.</returns>
    public sealed override string ToString()
    {
        return Value();
    }

    private sealed record PlainStreamName(string Name) : StreamName
    {
        public override string Value()
        {
            return Name;
        }
    }
}
