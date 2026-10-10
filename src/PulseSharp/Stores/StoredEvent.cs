namespace PulseSharp.Stores;

internal sealed record StoredEvent(
    long GlobalPosition,
    Guid EventId,
    string Stream,
    long StreamPosition,
    string EventName,
    object Data,
    string Origin,
    DateTimeOffset EmittedAt
);
