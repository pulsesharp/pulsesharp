namespace PulseSharp;

/// <summary>
/// How far one handler has got in one stream.
/// </summary>
/// <param name="HandlerName">The stable name of the handler.</param>
/// <param name="Stream">The stream name.</param>
/// <param name="Position">The position of the last event the handler finished, or 0 when it has finished none.</param>
/// <param name="State">Whether the handler is active, retrying or failed in this stream.</param>
/// <param name="Attempts">How many times the current event has failed.</param>
/// <param name="LastError">The last error, when the current event has failed.</param>
/// <param name="RetryAt">When the next attempt is due, while retrying.</param>
public sealed record HandlerCursor(
    string HandlerName,
    string Stream,
    long Position,
    CursorState State,
    int Attempts,
    string? LastError,
    DateTimeOffset? RetryAt
);
