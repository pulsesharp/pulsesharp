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
