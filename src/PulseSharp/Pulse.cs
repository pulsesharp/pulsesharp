using PulseSharp.Delivery;
using PulseSharp.Naming;
using PulseSharp.Stores;

namespace PulseSharp;

internal sealed class Pulse(
    IPulseStore store,
    PulseNames names,
    PulseJson json,
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
            names.Events.NameOf(@event.GetType()),
            json.Serialize(@event),
            settings.Node,
            now
        );

        var stored = await store.Append(newEvent, options.ExpectedVersion, options.Transaction, cancellationToken).ConfigureAwait(false);
        signal.Notify();
        return new EmitResult(stored.EventId, stored.StreamPosition);
    }

    public Task<EmitResult> EmitAll(StreamName stream, IReadOnlyList<object> events, CancellationToken cancellationToken = default)
    {
        return EmitAll(stream, events, DefaultEmitOptions, cancellationToken);
    }

    public async Task<EmitResult> EmitAll(
        StreamName stream,
        IReadOnlyList<object> events,
        EmitOptions options,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(options);

        var streamName = stream.Value();
        if (string.IsNullOrWhiteSpace(streamName))
        {
            throw new ArgumentException("The stream name must not be empty.", nameof(stream));
        }

        if (events.Count == 0)
        {
            throw new ArgumentException("EmitAll needs at least one event. To save one event, use Emit.", nameof(events));
        }

        var now = time.GetUtcNow();
        var newEvents = new List<NewEvent>(events.Count);
        for (var index = 0; index < events.Count; index++)
        {
            var @event = events[index] ?? throw new ArgumentException($"The event at position {index} is null.", nameof(events));
            newEvents.Add(new NewEvent(EventIds.NewVersion7(now), streamName, names.Events.NameOf(@event.GetType()), json.Serialize(@event), settings.Node, now));
        }

        var stored = await store.AppendAll(newEvents, options.ExpectedVersion, options.Transaction, cancellationToken).ConfigureAwait(false);
        signal.Notify();
        return new EmitResult(stored[^1].EventId, stored[^1].StreamPosition);
    }

    public Task<Guid> Enqueue<TJob>(TJob job, CancellationToken cancellationToken = default)
        where TJob : notnull
    {
        return Enqueue(job, DefaultEnqueueOptions, cancellationToken);
    }

    public async Task<Guid> Enqueue<TJob>(TJob job, EnqueueOptions options, CancellationToken cancellationToken = default)
        where TJob : notnull
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(options);

        var now = time.GetUtcNow();
        var runAt = options.RunAt is { } requested && requested > now ? requested : now;
        var newJob = new NewJob(EventIds.NewVersion7(now), names.Jobs.NameOf(job.GetType()), json.Serialize(job), now, runAt);

        var stored = await store.AddJob(newJob, options.Transaction, cancellationToken).ConfigureAwait(false);
        signal.Notify();
        return stored.JobId;
    }
}
