namespace PulseSharp;

/// <summary>
/// Information about the event a handler is receiving.
/// </summary>
public sealed class EventContext
{
    /// <summary>The globally unique id of the event. It stays the same when the event is synced to another node.</summary>
    public required Guid EventId { get; init; }

    /// <summary>The stored name of the event type.</summary>
    public required string EventName { get; init; }

    /// <summary>The name of the stream the event belongs to.</summary>
    public required string Stream { get; init; }

    /// <summary>The position of the event in its stream, starting at 1.</summary>
    public required long StreamPosition { get; init; }

    /// <summary>The node that emitted the event, for example <c>till-1</c> or <c>cloud</c>.</summary>
    public required string Origin { get; init; }

    /// <summary>When the event was emitted.</summary>
    public required DateTimeOffset EmittedAt { get; init; }

    /// <summary>The stable name of the handler receiving the event.</summary>
    public required string HandlerName { get; init; }

    /// <summary>The attempt number, starting at 1. Higher numbers mean earlier attempts failed.</summary>
    public required int Attempt { get; init; }
}
