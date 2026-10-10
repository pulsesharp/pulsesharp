namespace PulseSharp;

/// <summary>
/// Thrown by <see cref="IPulse.Emit{TEvent}(StreamName, TEvent, EmitOptions, CancellationToken)"/> when the stream
/// does not match <see cref="EmitOptions.ExpectedVersion"/>. Someone else changed the stream: read it again and retry.
/// </summary>
public sealed class ExpectedVersionMismatchException : Exception
{
    /// <summary>
    /// Creates the exception.
    /// </summary>
    /// <param name="stream">The stream name.</param>
    /// <param name="expected">The expected version that was not met.</param>
    /// <param name="actualPosition">The position of the last event in the stream, or 0 when the stream has no events.</param>
    public ExpectedVersionMismatchException(string stream, ExpectedVersion expected, long actualPosition)
        : base(CreateMessage(stream, expected, actualPosition))
    {
        Stream = stream;
        Expected = expected;
        ActualPosition = actualPosition;
    }

    /// <summary>The stream name.</summary>
    public string Stream { get; }

    /// <summary>The expected version that was not met.</summary>
    public ExpectedVersion Expected { get; }

    /// <summary>The position of the last event in the stream, or 0 when the stream has no events.</summary>
    public long ActualPosition { get; }

    private static string CreateMessage(string stream, ExpectedVersion expected, long actualPosition)
    {
        var expectation = expected.Kind switch
        {
            ExpectedVersionKind.NoStream => "no events",
            ExpectedVersionKind.StreamExists => "at least one event",
            ExpectedVersionKind.Exactly => $"its last event at position {expected.Position}",
            _ => "any state"
        };

        return $"Stream '{stream}' was expected to have {expectation}, but its last event is at position {actualPosition}.";
    }
}
