namespace PulseSharp.Stores;

/// <summary>
/// An event to save. The engine has already serialized its data.
/// </summary>
/// <param name="EventId">The globally unique id of the event (UUIDv7).</param>
/// <param name="Stream">The stream name.</param>
/// <param name="EventName">The stored name of the event type.</param>
/// <param name="Data">The serialized event (UTF-8 JSON).</param>
/// <param name="Origin">The node that emitted the event.</param>
/// <param name="EmittedAt">When the event was emitted.</param>
public sealed record NewEvent(
    Guid EventId,
    string Stream,
    string EventName,
    byte[] Data,
    string Origin,
    DateTimeOffset EmittedAt
);

/// <summary>
/// A saved event.
/// </summary>
/// <param name="GlobalPosition">The position in the whole store, starting at 1. Later events have higher positions.</param>
/// <param name="EventId">The globally unique id of the event.</param>
/// <param name="Stream">The stream name.</param>
/// <param name="StreamPosition">The position in the stream, starting at 1.</param>
/// <param name="EventName">The stored name of the event type.</param>
/// <param name="Data">The serialized event (UTF-8 JSON).</param>
/// <param name="Origin">The node that emitted the event.</param>
/// <param name="EmittedAt">When the event was emitted.</param>
public sealed record StoredEvent(
    long GlobalPosition,
    Guid EventId,
    string Stream,
    long StreamPosition,
    string EventName,
    byte[] Data,
    string Origin,
    DateTimeOffset EmittedAt
);
