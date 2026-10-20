namespace PulseSharp.Stores;

/// <summary>
/// Optional for stores: tells this app instance at once when another instance saved new events, jobs or cursor
/// changes, so handlers and jobs start right away. Without it, other instances find new work by polling, within about
/// one <c>Advanced.PollInterval</c>.
/// </summary>
/// <remarks>
/// The engine calls <see cref="ListenForChanges"/> once when the app starts. The store should keep listening until the
/// token is cancelled, and reconnect on its own after a failure; polling covers the gap.
/// </remarks>
public interface INotifyingStore
{
    /// <summary>
    /// Listens for changes made by any app instance, and calls <paramref name="changed"/> for each one, until
    /// <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <param name="changed">Called when something changed. Cheap and safe to call often.</param>
    /// <param name="cancellationToken">Stops listening when the app stops.</param>
    /// <returns>A task that completes when listening has stopped.</returns>
    Task ListenForChanges(Action changed, CancellationToken cancellationToken);
}
