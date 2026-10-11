using PulseSharp.Stores;

namespace PulseSharp.Delivery;

internal sealed class StreamState(string stream)
{
    public string Stream { get; } = stream;

    public Queue<StoredEvent> Pending { get; } = new();

    public long Position { get; set; }

    public CursorState Status { get; set; } = CursorState.Active;

    public int Attempts { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset? RetryAt { get; set; }

    public bool IsWaitingAt(DateTimeOffset now)
    {
        return Status == CursorState.Failed || (RetryAt is { } retryAt && retryAt > now);
    }

    public Cursor ToCursor(string handlerName)
    {
        return new Cursor(handlerName, Stream, Position, Status, Attempts, LastError, RetryAt);
    }

    public static StreamState From(Cursor cursor)
    {
        return new StreamState(cursor.Stream)
        {
            Position = cursor.Position,
            Status = cursor.Status,
            Attempts = cursor.Attempts,
            LastError = cursor.LastError,
            RetryAt = cursor.RetryAt
        };
    }
}
