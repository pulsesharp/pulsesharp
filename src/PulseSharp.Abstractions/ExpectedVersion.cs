namespace PulseSharp;

/// <summary>
/// What the stream must look like for an emit to succeed. Use it to make sure nobody else
/// changed the stream since you read it (optimistic concurrency).
/// </summary>
/// <remarks>
/// The default value is <see cref="Any"/>: no check.
/// </remarks>
public readonly record struct ExpectedVersion
{
    /// <summary>No check. The event is always added.</summary>
    public static readonly ExpectedVersion Any = new(ExpectedVersionKind.Any, 0);

    /// <summary>The stream must not have any events yet.</summary>
    public static readonly ExpectedVersion NoStream = new(ExpectedVersionKind.NoStream, 0);

    /// <summary>The stream must already have at least one event.</summary>
    public static readonly ExpectedVersion StreamExists = new(ExpectedVersionKind.StreamExists, 0);

    private ExpectedVersion(ExpectedVersionKind kind, long position)
    {
        Kind = kind;
        Position = position;
    }

    /// <summary>The kind of check.</summary>
    public ExpectedVersionKind Kind { get; }

    /// <summary>For <see cref="ExpectedVersionKind.Exactly"/>, the position the last event must have. Otherwise 0.</summary>
    public long Position { get; }

    /// <summary>
    /// Checks this expectation against the stream as it is now. Stores call it while appending.
    /// </summary>
    /// <param name="currentPosition">The position of the last event in the stream, or 0 when the stream has no events.</param>
    /// <returns><see langword="true"/> when an event may be appended.</returns>
    public bool Allows(long currentPosition)
    {
        return Kind switch
        {
            ExpectedVersionKind.NoStream => currentPosition == 0,
            ExpectedVersionKind.StreamExists => currentPosition > 0,
            ExpectedVersionKind.Exactly => currentPosition == Position,
            _ => true
        };
    }

    /// <summary>
    /// The last event of the stream must be at exactly <paramref name="position"/>.
    /// </summary>
    /// <param name="position">The expected position of the last event, starting at 1.</param>
    /// <returns>The expected version.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="position"/> is less than 1.</exception>
    public static ExpectedVersion Exactly(long position)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(position, 1);
        return new ExpectedVersion(ExpectedVersionKind.Exactly, position);
    }
}
