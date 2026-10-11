namespace PulseSharp.Stores;

internal sealed record Cursor(
    string HandlerName,
    string Stream,
    long Position,
    CursorState Status,
    int Attempts,
    string? LastError,
    DateTimeOffset? RetryAt
);
