namespace PulseSharp.Testing;

/// <summary>
/// An <see cref="IPulse"/> for tests: it records what is emitted and enqueued, and runs nothing. Pass it to the code
/// you test, then check it with <c>Emitted</c>, <c>NotEmitted</c>, <c>Enqueued</c> and <c>NotEnqueued</c>, or read
/// <see cref="Events"/> and <see cref="Jobs"/> yourself.
/// </summary>
/// <remarks>
/// Expected versions are checked like the real stores do. Derive from it to change how it behaves.
/// </remarks>
/// <example>
/// <code>
/// var pulse = new FakePulse();
/// var checkout = new Checkout(pulse);
///
/// await checkout.Complete(sale);
///
/// var completed = pulse.Emitted&lt;SaleCompleted&gt;();
/// Assert.Equal(25m, completed.Total);
/// pulse.Enqueued(new SendReceipt(sale.Id));
/// </code>
/// </example>
public class FakePulse : IPulse, IRecordedPulse
{
    private readonly object _gate = new();
    private readonly List<EmittedEvent> _events = [];
    private readonly List<EnqueuedJob> _jobs = [];
    private readonly Dictionary<string, long> _streamPositions = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public IReadOnlyList<EmittedEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return _events.ToList();
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<EnqueuedJob> Jobs
    {
        get
        {
            lock (_gate)
            {
                return _jobs.ToList();
            }
        }
    }

    /// <summary>
    /// When set, every <c>Emit</c> throws this exception instead of recording, to test what your code does when
    /// saving an event fails.
    /// </summary>
    public Exception? ThrowOnEmit { get; set; }

    /// <summary>
    /// When set, every <c>Enqueue</c> throws this exception instead of recording.
    /// </summary>
    public Exception? ThrowOnEnqueue { get; set; }

    /// <inheritdoc />
    public Task<EmitResult> Emit<TEvent>(StreamName stream, TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : notnull
    {
        return Emit(stream, @event, new EmitOptions(), cancellationToken);
    }

    /// <inheritdoc />
    public virtual Task<EmitResult> Emit<TEvent>(StreamName stream, TEvent @event, EmitOptions options, CancellationToken cancellationToken = default)
        where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(options);

        if (ThrowOnEmit is { } failure)
        {
            throw failure;
        }

        var streamName = stream.Value();
        lock (_gate)
        {
            _streamPositions.TryGetValue(streamName, out var current);
            if (!options.ExpectedVersion.Allows(current))
            {
                throw new ExpectedVersionMismatchException(streamName, options.ExpectedVersion, current);
            }

            _streamPositions[streamName] = current + 1;
            _events.Add(new EmittedEvent(@event, streamName, options));
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
    public virtual Task<Guid> Enqueue<TJob>(TJob job, EnqueueOptions options, CancellationToken cancellationToken = default)
        where TJob : notnull
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(options);

        if (ThrowOnEnqueue is { } failure)
        {
            throw failure;
        }

        lock (_gate)
        {
            _jobs.Add(new EnqueuedJob(job, options.RunAt, options));
        }

        return Task.FromResult(Guid.NewGuid());
    }

    /// <summary>
    /// Forgets everything recorded so far, for example after setting up a test.
    /// </summary>
    public virtual void Clear()
    {
        lock (_gate)
        {
            _events.Clear();
            _jobs.Clear();
            _streamPositions.Clear();
        }
    }
}
