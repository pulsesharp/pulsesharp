using System.Reflection;

namespace PulseSharp.Testing;

/// <summary>
/// Creates an <see cref="EventContext"/> for calling a handler class directly in a test.
/// </summary>
/// <example>
/// <code>
/// await handler.Handle(sale, TestEventContext.For(sale), CancellationToken.None);
/// </code>
/// </example>
public static class TestEventContext
{
    /// <summary>
    /// Creates a context for an event, with sensible values for everything you don't pass.
    /// </summary>
    /// <typeparam name="TEvent">The event type.</typeparam>
    /// <param name="event">The event the handler receives.</param>
    /// <param name="stream">The stream name. The default is the event's name, like <c>pulse.Emit(event)</c> uses.</param>
    /// <param name="attempt">The attempt number: 1 for the first try, 2 for the first retry. The default is 1.</param>
    /// <param name="streamPosition">The event's position in its stream. The default is 1.</param>
    /// <param name="handlerName">The handler's name. The default is <c>test-handler</c>.</param>
    /// <param name="origin">The node that emitted the event. The default is <c>test</c>.</param>
    /// <returns>The context.</returns>
    public static EventContext For<TEvent>(
        TEvent @event,
        string? stream = null,
        int attempt = 1,
        long streamPosition = 1,
        string handlerName = "test-handler",
        string origin = "test"
    )
        where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(streamPosition, 1);

        var type = @event.GetType();
        var eventName = type.GetCustomAttribute<EventNameAttribute>()?.Name ?? type.Name;
        return new EventContext
        {
            EventId = Guid.NewGuid(),
            EventName = eventName,
            Stream = stream ?? eventName,
            StreamPosition = streamPosition,
            Origin = origin,
            EmittedAt = DateTimeOffset.UtcNow,
            HandlerName = handlerName,
            Attempt = attempt
        };
    }
}
