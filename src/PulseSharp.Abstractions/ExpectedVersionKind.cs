namespace PulseSharp;

/// <summary>
/// The kind of check an <see cref="ExpectedVersion"/> makes.
/// </summary>
public enum ExpectedVersionKind
{
    /// <summary>No check. The event is always added.</summary>
    Any = 0,

    /// <summary>The stream must not have any events yet.</summary>
    NoStream = 1,

    /// <summary>The stream must already have at least one event.</summary>
    StreamExists = 2,

    /// <summary>The last event of the stream must be at exactly <see cref="ExpectedVersion.Position"/>.</summary>
    Exactly = 3
}
