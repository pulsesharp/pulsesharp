namespace PulseSharp;

/// <summary>
/// The entry point for emitting events and enqueuing jobs.
/// </summary>
public interface IPulse
{
    /// <summary>
    /// Saves an event to a stream. Once this method returns, the event is stored and every
    /// handler of <typeparamref name="TEvent"/> will receive it.
    /// </summary>
    /// <typeparam name="TEvent">The event type. Its stored name is the class name, or the name in <see cref="EventNameAttribute"/>.</typeparam>
    /// <param name="stream">The stream the event belongs to.</param>
    /// <param name="event">The event.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>The id of the event and its position in the stream.</returns>
    Task<EmitResult> Emit<TEvent>(StreamName stream, TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : notnull;

    /// <summary>
    /// Saves an event to a stream, with an expected-version check or inside a database transaction.
    /// </summary>
    /// <typeparam name="TEvent">The event type. Its stored name is the class name, or the name in <see cref="EventNameAttribute"/>.</typeparam>
    /// <param name="stream">The stream the event belongs to.</param>
    /// <param name="event">The event.</param>
    /// <param name="options">The expected version and the optional transaction.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>The id of the event and its position in the stream.</returns>
    Task<EmitResult> Emit<TEvent>(StreamName stream, TEvent @event, EmitOptions options, CancellationToken cancellationToken = default)
        where TEvent : notnull;

    /// <summary>
    /// Saves several events to one stream in one commit: all of them are saved, in the order given, or none. This is also
    /// much faster than emitting them one by one. Every handler receives each event as usual.
    /// </summary>
    /// <param name="stream">The stream the events belong to.</param>
    /// <param name="events">The events. They may be of different types. There must be at least one.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>The id and stream position of the last event.</returns>
    /// <exception cref="ArgumentException">The list is empty or has a <see langword="null"/> event.</exception>
    Task<EmitResult> EmitAll(StreamName stream, IReadOnlyList<object> events, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves several events to one stream in one commit, with an expected-version check or inside a database transaction.
    /// The check and the transaction apply to the whole group.
    /// </summary>
    /// <param name="stream">The stream the events belong to.</param>
    /// <param name="events">The events. They may be of different types. There must be at least one.</param>
    /// <param name="options">The expected version (of the stream before the group) and the optional transaction.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>The id and stream position of the last event.</returns>
    /// <exception cref="ArgumentException">The list is empty or has a <see langword="null"/> event.</exception>
    /// <exception cref="ExpectedVersionMismatchException">The stream is not at the expected version. Nothing is saved.</exception>
    Task<EmitResult> EmitAll(StreamName stream, IReadOnlyList<object> events, EmitOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a job. One worker runs it with the <see cref="IJobHandler{TJob}"/> for <typeparamref name="TJob"/>.
    /// </summary>
    /// <typeparam name="TJob">The job type. Its stored name is the class name, or the name in <see cref="JobNameAttribute"/>.</typeparam>
    /// <param name="job">The job.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>The id of the job.</returns>
    Task<Guid> Enqueue<TJob>(TJob job, CancellationToken cancellationToken = default)
        where TJob : notnull;

    /// <summary>
    /// Saves a job that runs later, or saves it inside a database transaction.
    /// </summary>
    /// <typeparam name="TJob">The job type. Its stored name is the class name, or the name in <see cref="JobNameAttribute"/>.</typeparam>
    /// <param name="job">The job.</param>
    /// <param name="options">When to run the job, and the optional transaction.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>The id of the job.</returns>
    Task<Guid> Enqueue<TJob>(TJob job, EnqueueOptions options, CancellationToken cancellationToken = default)
        where TJob : notnull;
}
