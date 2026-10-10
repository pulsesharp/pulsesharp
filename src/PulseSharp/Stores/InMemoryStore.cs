namespace PulseSharp.Stores;

internal sealed class InMemoryStore : IPulseStore
{
    private readonly object _gate = new();
    private readonly List<StoredEvent> _events = [];
    private readonly Dictionary<string, long> _streamPositions = new(StringComparer.Ordinal);
    private readonly Dictionary<(string HandlerName, string Stream), Cursor> _cursors = [];

    public Task<StoredEvent> Append(NewEvent newEvent, ExpectedVersion expectedVersion, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _streamPositions.TryGetValue(newEvent.Stream, out var currentPosition);
            if (!Matches(expectedVersion, currentPosition))
            {
                throw new ExpectedVersionMismatchException(newEvent.Stream, expectedVersion, currentPosition);
            }

            var stored = new StoredEvent(
                GlobalPosition: _events.Count + 1,
                EventId: newEvent.EventId,
                Stream: newEvent.Stream,
                StreamPosition: currentPosition + 1,
                EventName: newEvent.EventName,
                Data: newEvent.Data,
                Origin: newEvent.Origin,
                EmittedAt: newEvent.EmittedAt
            );

            _events.Add(stored);
            _streamPositions[newEvent.Stream] = stored.StreamPosition;
            return Task.FromResult(stored);
        }
    }

    public Task<IReadOnlyList<StoredEvent>> ReadAfter(
        long globalPosition,
        IReadOnlySet<string> eventNames,
        int maxCount,
        CancellationToken cancellationToken
    )
    {
        var found = new List<StoredEvent>();
        lock (_gate)
        {
            for (var index = (int)globalPosition; index < _events.Count && found.Count < maxCount; index++)
            {
                var stored = _events[index];
                if (eventNames.Contains(stored.EventName))
                {
                    found.Add(stored);
                }
            }
        }

        return Task.FromResult<IReadOnlyList<StoredEvent>>(found);
    }

    public Task<IReadOnlyList<Cursor>> LoadCursors(string handlerName, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<Cursor> cursors = _cursors.Values.Where(cursor => cursor.HandlerName == handlerName).ToList();
            return Task.FromResult(cursors);
        }
    }

    public Task SaveCursor(Cursor cursor, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _cursors[(cursor.HandlerName, cursor.Stream)] = cursor;
        }

        return Task.CompletedTask;
    }

    private static bool Matches(ExpectedVersion expectedVersion, long currentPosition)
    {
        return expectedVersion.Kind switch
        {
            ExpectedVersionKind.NoStream => currentPosition == 0,
            ExpectedVersionKind.StreamExists => currentPosition > 0,
            ExpectedVersionKind.Exactly => currentPosition == expectedVersion.Position,
            _ => true
        };
    }
}
