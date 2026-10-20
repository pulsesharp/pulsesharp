using System.Reflection;

namespace PulseSharp.Testing;

/// <summary>
/// An <see cref="IPulse"/> that only records what is emitted and enqueued. No engine, no database and no handlers run,
/// so tests of code that emits events or enqueues jobs stay fast and simple.
/// </summary>
/// <remarks>
/// Expected versions are checked like the real stores do, so code that relies on them can be tested too.
/// </remarks>
/// <example>
/// <code>
/// var pulse = new RecordingPulse();
/// await checkout.Complete(sale, pulse);
///
/// pulse.Emitted&lt;SaleCompleted&gt;().ShouldHaveOne(e => e.Total == 25m);
/// pulse.Enqueued&lt;SendReceipt&gt;().ShouldHaveOne();
/// </code>
/// </example>
public sealed class RecordingPulse : IPulse
{
    private readonly object _gate = new();
    private readonly List<(object Event, string Stream)> _events = [];
    private readonly List<(object Job, DateTimeOffset? RunAt)> _jobs = [];
    private readonly Dictionary<string, long> _streamPositions = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<EmitResult> Emit<TEvent>(StreamName stream, TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : notnull
    {
        return Emit(stream, @event, new EmitOptions(), cancellationToken);
    }

    /// <inheritdoc />
    public Task<EmitResult> Emit<TEvent>(StreamName stream, TEvent @event, EmitOptions options, CancellationToken cancellationToken = default)
        where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(options);

        var streamName = stream.Value();
        lock (_gate)
        {
            _streamPositions.TryGetValue(streamName, out var current);
            if (!options.ExpectedVersion.Allows(current))
            {
                throw new ExpectedVersionMismatchException(streamName, options.ExpectedVersion, current);
            }

            _streamPositions[streamName] = current + 1;
            _events.Add((@event, streamName));
            return Task.FromResult(new EmitResult(Guid.NewGuid(), current + 1));
        }
    }

    /// <inheritdoc />
    public Task<Guid> Enqueue<TJob>(TJob job, CancellationToken cancellationToken = default)
        where TJob : notnull
    {
        return Enqueue(job, new EnqueueOptions(), cancellationToken);
    }

    /// <inheritdoc />
    public Task<Guid> Enqueue<TJob>(TJob job, EnqueueOptions options, CancellationToken cancellationToken = default)
        where TJob : notnull
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(options);

        lock (_gate)
        {
            _jobs.Add((job, options.RunAt));
        }

        return Task.FromResult(Guid.NewGuid());
    }

    /// <summary>
    /// The events of one type that were emitted, in order.
    /// </summary>
    /// <typeparam name="TEvent">The event type.</typeparam>
    /// <returns>The events, ready to check.</returns>
    public EmittedEvents<TEvent> Emitted<TEvent>()
    {
        lock (_gate)
        {
            var items = _events
                .Where(recorded => recorded.Event is TEvent)
                .Select(recorded => new RecordedItem<TEvent>((TEvent)recorded.Event, EmittedEvents<TEvent>.StreamText(recorded.Stream)))
                .ToList();
            var others = Summary.Others("events", _events.Where(recorded => recorded.Event is not TEvent).Select(recorded => NameOf(recorded.Event.GetType())));
            return new EmittedEvents<TEvent>(new Recorded<TEvent>(typeof(TEvent).Name, "emitted", items, string.Empty, others));
        }
    }

    /// <summary>
    /// The jobs of one type that were enqueued, in order.
    /// </summary>
    /// <typeparam name="TJob">The job type.</typeparam>
    /// <returns>The jobs, ready to check.</returns>
    public EnqueuedJobs<TJob> Enqueued<TJob>()
    {
        lock (_gate)
        {
            var items = _jobs
                .Where(recorded => recorded.Job is TJob)
                .Select(recorded => new RecordedItem<TJob>((TJob)recorded.Job, recorded.RunAt is { } runAt ? $" to run at {runAt:O}" : string.Empty))
                .ToList();
            var others = Summary.Others("jobs", _jobs.Where(recorded => recorded.Job is not TJob).Select(recorded => recorded.Job.GetType().Name));
            return new EnqueuedJobs<TJob>(new Recorded<TJob>(typeof(TJob).Name, "enqueued", items, string.Empty, others));
        }
    }

    /// <summary>
    /// Forgets everything recorded so far, for example after setting up a test.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _events.Clear();
            _jobs.Clear();
            _streamPositions.Clear();
        }
    }

    private static string NameOf(Type type)
    {
        return type.GetCustomAttribute<EventNameAttribute>()?.Name ?? type.Name;
    }
}
