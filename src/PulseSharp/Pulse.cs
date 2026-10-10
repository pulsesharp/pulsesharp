using PulseSharp.Delivery;
using PulseSharp.Naming;
using PulseSharp.Stores;

namespace PulseSharp;

internal sealed class Pulse(
    IPulseStore store,
    EventNames eventNames,
    WakeUpSignal signal,
    PulseSharpSettings settings,
    TimeProvider time
) : IPulse
{
    private static readonly EmitOptions DefaultEmitOptions = new();
    private static readonly EnqueueOptions DefaultEnqueueOptions = new();

    public Task<EmitResult> Emit<TEvent>(StreamName stream, TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : notnull
    {
        return Emit(stream, @event, DefaultEmitOptions, cancellationToken);
    }

    public async Task<EmitResult> Emit<TEvent>(
        StreamName stream,
        TEvent @event,
        EmitOptions options,
        CancellationToken cancellationToken = default
    )
        where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(options);

        var streamName = stream.Value();
        if (string.IsNullOrWhiteSpace(streamName))
        {
            throw new ArgumentException("The stream name must not be empty.", nameof(stream));
        }

        var now = time.GetUtcNow();
        var newEvent = new NewEvent(
            EventIds.NewVersion7(now),
            streamName,
            eventNames.NameOf(@event.GetType()),
            @event,
            settings.Node,
            now
        );

        var stored = await store.Append(newEvent, options.ExpectedVersion, cancellationToken).ConfigureAwait(false);
        signal.Notify();
        return new EmitResult(stored.EventId, stored.StreamPosition);
    }

    public Task<Guid> Enqueue<TJob>(TJob job, CancellationToken cancellationToken = default)
        where TJob : notnull
    {
        return Enqueue(job, DefaultEnqueueOptions, cancellationToken);
    }

    public Task<Guid> Enqueue<TJob>(TJob job, EnqueueOptions options, CancellationToken cancellationToken = default)
        where TJob : notnull
    {
        throw new NotSupportedException("Jobs arrive in PulseSharp 0.2. This preview delivers events only.");
    }
}
