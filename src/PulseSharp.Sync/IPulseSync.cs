namespace PulseSharp.Sync;

/// <summary>
/// Shows how sync is going and starts a sync on request, for example from a "Sync now" button.
/// </summary>
public interface IPulseSync
{
    /// <summary>
    /// The result of the last sync.
    /// </summary>
    SyncStatus Status { get; }

    /// <summary>
    /// Syncs at once instead of waiting for the next interval. Never throws when the server can't be reached:
    /// the returned status says what happened.
    /// </summary>
    /// <param name="cancellationToken">Cancels the sync.</param>
    /// <returns>The status after the sync.</returns>
    Task<SyncStatus> SyncNow(CancellationToken cancellationToken = default);

    /// <summary>
    /// Raised whenever the status changes: after every sync, and when events are saved or sent so that
    /// <see cref="SyncStatus.Waiting"/> changes. It runs on a background thread, so a UI must switch to its own thread.
    /// An exception in a subscriber is logged and does not affect sync.
    /// </summary>
    event Action<SyncStatus>? StatusChanged;
}

/// <summary>
/// The result of the last sync.
/// </summary>
/// <param name="Online">Whether the last sync reached the server and finished.</param>
/// <param name="LastSyncedAt">When a sync last finished, or <see langword="null"/> when none has yet.</param>
/// <param name="LastError">Why the last sync failed, or <see langword="null"/> when it succeeded.</param>
/// <param name="Waiting">How many events saved on this device the server has not confirmed yet. It also counts events that were saved while the app was offline or before it was last started.</param>
public sealed record SyncStatus(bool Online, DateTimeOffset? LastSyncedAt, string? LastError, int Waiting = 0);
