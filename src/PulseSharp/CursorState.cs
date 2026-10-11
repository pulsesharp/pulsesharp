namespace PulseSharp;

/// <summary>
/// The state of a handler in one stream.
/// </summary>
public enum CursorState
{
    /// <summary>The handler is up to date or is handling new events.</summary>
    Active = 0,

    /// <summary>The last event failed and will be tried again at <see cref="HandlerCursor.RetryAt"/>.</summary>
    Retrying = 1,

    /// <summary>The handler gave up on an event. The stream is stopped for this handler until you retry or skip it.</summary>
    Failed = 2
}
