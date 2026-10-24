using System.Diagnostics;
using PulseSharp.Stores;

namespace PulseSharp.Delivery;

/// <summary>
/// The progress of a handler that is not saved yet. Instead of one commit per handled event, the runner saves it in
/// groups: when 100 events or 200 milliseconds have gone by, and at the end of every round, on a failure and on stop.
/// After a crash, the events handled since the last save (never more than 100) are delivered again.
/// </summary>
internal sealed class CursorBatch
{
    private const int MaxUnsavedEvents = 100;
    private static readonly TimeSpan MaxUnsavedTime = TimeSpan.FromMilliseconds(200);

    private readonly object _gate = new();
    private readonly Dictionary<string, HandlerCursor> _latestByStream = new(StringComparer.Ordinal);
    private int _unsavedEvents;
    private long _lastTaken = Stopwatch.GetTimestamp();

    /// <summary>Remembers the latest progress of a stream. Returns true when it is time to save.</summary>
    public bool Add(HandlerCursor cursor)
    {
        lock (_gate)
        {
            _latestByStream[cursor.Stream] = cursor;
            _unsavedEvents++;
            return _unsavedEvents >= MaxUnsavedEvents || Stopwatch.GetElapsedTime(_lastTaken) >= MaxUnsavedTime;
        }
    }

    /// <summary>Forgets the unsaved progress of a stream, because a newer cursor is saved on its own.</summary>
    public void Discard(string stream)
    {
        lock (_gate)
        {
            _latestByStream.Remove(stream);
        }
    }

    public List<HandlerCursor> TakeAll()
    {
        lock (_gate)
        {
            var cursors = _latestByStream.Values.ToList();
            _latestByStream.Clear();
            _unsavedEvents = 0;
            _lastTaken = Stopwatch.GetTimestamp();
            return cursors;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _latestByStream.Clear();
            _unsavedEvents = 0;
        }
    }
}
