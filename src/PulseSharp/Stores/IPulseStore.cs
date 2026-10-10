namespace PulseSharp.Stores;

internal interface IPulseStore
{
    Task<StoredEvent> Append(NewEvent newEvent, ExpectedVersion expectedVersion, CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredEvent>> ReadAfter(
        long globalPosition,
        IReadOnlySet<string> eventNames,
        int maxCount,
        CancellationToken cancellationToken
    );

    Task<IReadOnlyList<Cursor>> LoadCursors(string handlerName, CancellationToken cancellationToken);

    Task SaveCursor(Cursor cursor, CancellationToken cancellationToken);
}
