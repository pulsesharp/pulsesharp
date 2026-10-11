namespace PulseSharp;

/// <summary>
/// Lets an app or an operator see and steer handlers: list their cursors, retry or skip a failed event,
/// or rewind a handler so it handles every event again.
/// </summary>
public interface IPulseControl
{
    /// <summary>
    /// Lists the cursors of one handler, or of all handlers.
    /// </summary>
    /// <param name="handlerName">The handler, or <see langword="null"/> for all handlers.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The cursors, ordered by handler and stream.</returns>
    /// <exception cref="ArgumentException">No handler has this name.</exception>
    Task<IReadOnlyList<HandlerCursor>> GetCursors(string? handlerName = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tries the stuck event of a stream again now, with a fresh attempt count.
    /// </summary>
    /// <param name="handlerName">The handler.</param>
    /// <param name="stream">The stream.</param>
    /// <param name="cancellationToken">Stops waiting for the handler to apply the request.</param>
    /// <returns>A task that completes when the handler has applied the request.</returns>
    /// <exception cref="ArgumentException">No handler has this name.</exception>
    /// <exception cref="InvalidOperationException">The handler has no cursor for this stream.</exception>
    Task Retry(string handlerName, string stream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Skips the stuck event of a stream: the handler never handles it, and continues with the next event.
    /// </summary>
    /// <param name="handlerName">The handler.</param>
    /// <param name="stream">The stream.</param>
    /// <param name="cancellationToken">Stops waiting for the handler to apply the request.</param>
    /// <returns>A task that completes when the handler has applied the request.</returns>
    /// <exception cref="ArgumentException">No handler has this name.</exception>
    /// <exception cref="InvalidOperationException">The stream has no waiting event for this handler.</exception>
    Task Skip(string handlerName, string stream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rewinds a handler to the start, so it handles every stored event again, in order.
    /// Use it after fixing a bug in a handler, or to rebuild data the handler writes.
    /// </summary>
    /// <param name="handlerName">The handler.</param>
    /// <param name="cancellationToken">Stops waiting for the handler to apply the request.</param>
    /// <returns>A task that completes when the handler has applied the request.</returns>
    /// <exception cref="ArgumentException">No handler has this name.</exception>
    Task Rewind(string handlerName, CancellationToken cancellationToken = default);
}
