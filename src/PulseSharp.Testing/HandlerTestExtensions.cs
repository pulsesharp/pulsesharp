using System.Reflection;

namespace PulseSharp.Testing;

/// <summary>
/// Calls event and job handlers directly in a test, without building a context or passing a token.
/// </summary>
public static class HandlerTestExtensions
{
    /// <summary>
    /// Calls an event handler with an event, as the engine would.
    /// </summary>
    /// <typeparam name="TEvent">The event type.</typeparam>
    /// <param name="handler">The handler.</param>
    /// <param name="event">The event.</param>
    /// <param name="stream">The stream name. The default is the event's name, like <c>pulse.Emit(event)</c> uses.</param>
    /// <param name="attempt">The attempt number: 1 for the first try, 2 for the first retry. The default is 1.</param>
    /// <param name="cancellationToken">Cancels the handler.</param>
    /// <returns>A task that completes when the handler is done.</returns>
    /// <example>
    /// <code>
    /// await handler.Handle(new SaleCompleted(1, 25m));
    /// </code>
    /// </example>
    public static Task Handle<TEvent>(
        this IEventHandler<TEvent> handler,
        TEvent @event,
        string? stream = null,
        int attempt = 1,
        CancellationToken cancellationToken = default
    )
        where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);

        var type = @event.GetType();
        var eventName = type.GetCustomAttribute<EventNameAttribute>()?.Name ?? type.Name;
        var context = new EventContext
        {
            EventId = Guid.NewGuid(),
            EventName = eventName,
            Stream = stream ?? eventName,
            StreamPosition = 1,
            Origin = "test",
            EmittedAt = DateTimeOffset.UtcNow,
            HandlerName = handler.GetType().GetCustomAttribute<HandlerNameAttribute>()?.Name ?? handler.GetType().Name,
            Attempt = attempt
        };

        return handler.Handle(@event, context, cancellationToken);
    }

    /// <summary>
    /// Calls a job handler with a job, as the engine would, and returns the context with the progress it reported.
    /// </summary>
    /// <typeparam name="TJob">The job type.</typeparam>
    /// <param name="handler">The handler.</param>
    /// <param name="job">The job.</param>
    /// <param name="attempt">The attempt number: 1 for the first try, 2 for the first retry. The default is 1.</param>
    /// <param name="cancellationToken">Cancels the handler.</param>
    /// <returns>The context, with the progress the handler reported.</returns>
    /// <example>
    /// <code>
    /// var context = await handler.Handle(new ExportReport(today));
    /// Assert.Equal((3, 3), context.LastProgress);
    /// </code>
    /// </example>
    public static async Task<FakeJobContext> Handle<TJob>(
        this IJobHandler<TJob> handler,
        TJob job,
        int attempt = 1,
        CancellationToken cancellationToken = default
    )
        where TJob : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(job);

        var type = job.GetType();
        var context = new FakeJobContext(type.GetCustomAttribute<JobNameAttribute>()?.Name ?? type.Name, attempt);
        await handler.Handle(job, context, cancellationToken).ConfigureAwait(false);
        return context;
    }
}
