namespace PulseSharp.Stores;

/// <summary>
/// Selects the events to send to another node.
/// </summary>
/// <param name="AfterPosition">Only events after this global position are returned. 0 reads from the start.</param>
/// <param name="Origin">Only events emitted by this node, or <see langword="null"/> for any node.</param>
/// <param name="ExceptOrigin">No events emitted by this node, or <see langword="null"/> to allow every node.</param>
/// <param name="StreamPrefixes">Only events whose stream name starts with one of these prefixes. Empty means every stream.</param>
/// <param name="MaxCount">The maximum number of events to return.</param>
public sealed record SyncQuery(
    long AfterPosition,
    string? Origin,
    string? ExceptOrigin,
    IReadOnlyList<string> StreamPrefixes,
    int MaxCount
);
