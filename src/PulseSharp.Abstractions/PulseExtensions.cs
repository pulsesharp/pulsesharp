using System.Reflection;

namespace PulseSharp;

/// <summary>
/// Short ways to emit events and enqueue jobs.
/// </summary>
public static class PulseExtensions
{
    /// <summary>
    /// Saves an event to the stream named after its type (for example <c>SaleCompleted</c>), so events of one type
    /// reach each handler in the order they were emitted.
    /// </summary>
    /// <typeparam name="TEvent">The event type. The stream name is the class name, or the name in <see cref="EventNameAttribute"/>.</typeparam>
    /// <param name="pulse">The pulse.</param>
    /// <param name="event">The event.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>The id of the event and its position in the stream.</returns>
    /// <remarks>
    /// While one event keeps failing in a handler, later events of the same type wait for it. When events are
    /// independent (one sale has nothing to do with another), give each item its own stream instead:
    /// <c>pulse.Emit("Sale-" + sale.Id, sale)</c>.
    /// </remarks>
    /// <example>
    /// <code>
    /// await pulse.Emit(new SaleCompleted(saleId, 25m));
    /// </code>
    /// </example>
    public static Task<EmitResult> Emit<TEvent>(this IPulse pulse, TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(pulse);
        return pulse.Emit(DefaultStream<TEvent>.Name, @event, cancellationToken);
    }

    /// <summary>
    /// Saves a job that runs after a delay.
    /// </summary>
    /// <typeparam name="TJob">The job type.</typeparam>
    /// <param name="pulse">The pulse.</param>
    /// <param name="job">The job.</param>
    /// <param name="delay">How long to wait before the job may run.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>The id of the job.</returns>
    /// <example>
    /// <code>
    /// await pulse.EnqueueIn(new SendReminder(customerId), TimeSpan.FromHours(1));
    /// </code>
    /// </example>
    public static Task<Guid> EnqueueIn<TJob>(this IPulse pulse, TJob job, TimeSpan delay, CancellationToken cancellationToken = default)
        where TJob : notnull
    {
        ArgumentNullException.ThrowIfNull(pulse);
        return pulse.Enqueue(job, new EnqueueOptions { RunAt = DateTimeOffset.UtcNow + delay }, cancellationToken);
    }

    /// <summary>
    /// Saves a job that runs at a given time.
    /// </summary>
    /// <typeparam name="TJob">The job type.</typeparam>
    /// <param name="pulse">The pulse.</param>
    /// <param name="job">The job.</param>
    /// <param name="runAt">The earliest time the job may run.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>The id of the job.</returns>
    /// <example>
    /// <code>
    /// await pulse.EnqueueAt(new SendDailyReport(), tomorrowAtNine);
    /// </code>
    /// </example>
    public static Task<Guid> EnqueueAt<TJob>(this IPulse pulse, TJob job, DateTimeOffset runAt, CancellationToken cancellationToken = default)
        where TJob : notnull
    {
        ArgumentNullException.ThrowIfNull(pulse);
        return pulse.Enqueue(job, new EnqueueOptions { RunAt = runAt }, cancellationToken);
    }

    /// <summary>
    /// Saves several events to one stream in one commit: all of them, in the order given, or none.
    /// </summary>
    /// <param name="pulse">The pulse.</param>
    /// <param name="stream">The stream the events belong to.</param>
    /// <param name="events">The events. They may be of different types.</param>
    /// <returns>The id and stream position of the last event.</returns>
    /// <example>
    /// <code>
    /// await pulse.EmitAll("Menu", new ProductAdded("tea", 2.50m), new ProductAdded("coffee", 3.20m));
    /// </code>
    /// </example>
    public static Task<EmitResult> EmitAll(this IPulse pulse, StreamName stream, params object[] events)
    {
        ArgumentNullException.ThrowIfNull(pulse);
        return pulse.EmitAll(stream, (IReadOnlyList<object>)events);
    }

    private static class DefaultStream<TEvent>
    {
        public static readonly StreamName Name = StreamName.From(
            typeof(TEvent).GetCustomAttribute<EventNameAttribute>()?.Name ?? typeof(TEvent).Name
        );
    }
}
