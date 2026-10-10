namespace PulseSharp.Stores;

internal sealed record NewEvent(
    Guid EventId,
    string Stream,
    string EventName,
    object Data,
    string Origin,
    DateTimeOffset EmittedAt
);
