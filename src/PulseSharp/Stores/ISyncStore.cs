namespace PulseSharp.Stores;

/// <summary>
/// A store that can also take part in sync: it saves events that were emitted on another node, finds the events to
/// send to another node, and remembers how far each remote node has got.
/// </summary>
/// <remarks>
/// Positions are local to each node. Event ids are global, so a store skips an event whose id it already has.
/// </remarks>
public interface ISyncStore : IPulseStore
{
    /// <summary>
    /// Saves events that were emitted on another node, in one atomic step, keeping their id, origin and emit time.
    /// Each event gets the next global position and the next position in its stream on this node. An event whose id
    /// is already in the store is skipped, so sending the same batch twice is harmless.
    /// </summary>
    /// <param name="events">The events, in the order they should be saved.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>The events that were new, in order, with their positions on this node.</returns>
    Task<IReadOnlyList<StoredEvent>> AppendReplicated(IReadOnlyList<NewEvent> events, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the events that match a query, in global order.
    /// </summary>
    /// <param name="query">Which events to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The events, ordered by global position.</returns>
    Task<IReadOnlyList<StoredEvent>> ReadForSync(SyncQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Counts the events that match a query, in the same way <see cref="ReadForSync"/> selects them, but without reading
    /// their data. <see cref="SyncQuery.MaxCount"/> caps the count. This is how a device knows how many events are waiting.
    /// </summary>
    /// <param name="query">Which events to count.</param>
    /// <param name="cancellationToken">Cancels the count.</param>
    /// <returns>The number of matching events, at most <see cref="SyncQuery.MaxCount"/>.</returns>
    Task<int> CountForSync(SyncQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Loads how far sync has got with a remote node.
    /// </summary>
    /// <param name="remote">The name of the remote node.</param>
    /// <param name="direction">Whether to load the push or the pull position.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// For a push, the last global position on this node that was sent. For a pull, the last global position on the
    /// remote node that was received. 0 when nothing has been synced yet.
    /// </returns>
    Task<long> LoadSyncPosition(string remote, SyncDirection direction, CancellationToken cancellationToken);

    /// <summary>
    /// Saves how far sync has got with a remote node, replacing the earlier position.
    /// </summary>
    /// <param name="remote">The name of the remote node.</param>
    /// <param name="direction">Whether this is the push or the pull position.</param>
    /// <param name="position">The position.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>A task that completes when the position is saved.</returns>
    Task SaveSyncPosition(string remote, SyncDirection direction, long position, CancellationToken cancellationToken);
}
