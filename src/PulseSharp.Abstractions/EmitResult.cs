namespace PulseSharp;

/// <summary>
/// The result of emitting an event.
/// </summary>
/// <param name="EventId">The globally unique id of the event.</param>
/// <param name="StreamPosition">The position of the event in its stream, starting at 1.</param>
public readonly record struct EmitResult(Guid EventId, long StreamPosition);
