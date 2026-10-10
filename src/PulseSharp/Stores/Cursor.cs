namespace PulseSharp.Stores;

internal sealed record Cursor(
    string HandlerName,
    string Stream,
    long Position,
    CursorStatus Status,
    int Attempts,
    string? LastError,
    DateTimeOffset? RetryAt
);
