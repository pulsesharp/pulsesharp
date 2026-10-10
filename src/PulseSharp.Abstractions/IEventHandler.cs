namespace PulseSharp;

/// <summary>
/// Reacts to events of type <typeparamref name="TEvent"/>.
/// </summary>
/// <remarks>
/// Each handler has a stable name: the class name, or the name in <see cref="HandlerNameAttribute"/>.
/// PulseSharp stores how far each handler has got under that name, so pin the name before renaming the class.
/// Delivery is at-least-once: after a crash at the wrong moment a handler can see the same event again,
/// so make handlers safe to repeat.
/// </remarks>
/// <typeparam name="TEvent">The event type.</typeparam>
public interface IEventHandler<in TEvent>
    where TEvent : notnull
{
    /// <summary>
    /// Handles one event. Events of one stream arrive in order, one at a time.
    /// Throwing an exception makes PulseSharp retry the event later.
    /// </summary>
    /// <param name="event">The event.</param>
    /// <param name="context">Where the event came from and which attempt this is.</param>
    /// <param name="cancellationToken">Signals that the app is stopping.</param>
    /// <returns>A task that completes when the event is handled.</returns>
    Task Handle(TEvent @event, EventContext context, CancellationToken cancellationToken);
}
