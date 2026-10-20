using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PulseSharp.Naming;
using PulseSharp.Stores;

namespace PulseSharp.Testing;

/// <summary>
/// Runs the real PulseSharp engine in a test, in memory by default, and waits until all of its work is done, so tests
/// never need delays.
/// </summary>
/// <remarks>
/// It is an <see cref="IPulse"/> itself: emit and enqueue on it directly. Retries are fast (10 ms, doubling) so a
/// failing handler stops quickly; change any setting in the <c>configure</c> action.
/// </remarks>
/// <example>
/// <code>
/// await using var pulse = await PulseTestHost.Start(
///     pulse => pulse.AddHandler&lt;SaleCompleted, UpdateStock&gt;(),
///     services => services.AddSingleton&lt;IStock, FakeStock&gt;());
///
/// await pulse.Given("Sale-1", new SaleStarted(1), new ItemAdded("coffee"));
/// await pulse.Emit(new SaleCompleted(1, 25m));
/// await pulse.WaitUntilIdle();
///
/// pulse.Emitted(new StockWentNegative("coffee"));
/// pulse.NothingFailed();
/// </code>
/// </example>
public sealed class PulseTestHost : IPulse, IRecordedPulse, IAsyncDisposable
{
    private const int PageSize = 1000;
    private const int MaxJobs = 100_000;
    private static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMilliseconds(10);

    private readonly ServiceProvider _services;
    private readonly IReadOnlyList<IHostedService> _hostedServices;
    private readonly IPulse _pulse;
    private readonly ISyncStore _store;
    private readonly PulseSharpSettings _settings;
    private readonly PulseNames _names;
    private readonly PulseJson _json;
    private readonly TimeProvider _time;
    private long _eventsBefore;
    private HashSet<Guid> _jobsBefore = [];

    private PulseTestHost(ServiceProvider services, IReadOnlyList<IHostedService> hostedServices, ISyncStore store)
    {
        _services = services;
        _hostedServices = hostedServices;
        _store = store;
        _pulse = services.GetRequiredService<IPulse>();
        _settings = services.GetRequiredService<PulseSharpSettings>();
        _names = services.GetRequiredService<PulseNames>();
        _json = services.GetRequiredService<PulseJson>();
        _time = services.GetRequiredService<TimeProvider>();
    }

    /// <summary>
    /// The app's services, for example to get a fake you registered and check what it received.
    /// </summary>
    public IServiceProvider Services
    {
        get
        {
            return _services;
        }
    }

    /// <summary>
    /// The control API: cursors, retry, skip, rewind and jobs.
    /// </summary>
    public IPulseControl Control
    {
        get
        {
            return _services.GetRequiredService<IPulseControl>();
        }
    }

    /// <summary>
    /// Starts the engine with the in-memory store, your handlers and your services.
    /// </summary>
    /// <param name="configure">Adds handlers and changes settings, like in <c>AddPulseSharp</c>. Choose another store here to test with it.</param>
    /// <param name="services">Registers the services your handlers need, for example fakes. Optional.</param>
    /// <returns>The running test host. Dispose it at the end of the test.</returns>
    /// <exception cref="InvalidOperationException">The setup is not valid, or PulseSharp and PulseSharp.Testing have different versions.</exception>
    public static async Task<PulseTestHost> Start(Action<PulseSharpBuilder> configure, Action<IServiceCollection>? services = null)
    {
        ArgumentNullException.ThrowIfNull(configure);
        CheckVersions();

        var collection = new ServiceCollection();
        services?.Invoke(collection);
        collection.AddPulseSharp(pulse =>
        {
            pulse.UseInMemory();
            pulse.Node = "test";
            pulse.RetryDelay = TimeSpan.FromMilliseconds(10);
            pulse.Advanced.PollInterval = TimeSpan.FromMilliseconds(20);
            configure(pulse);
        });

        var provider = collection.BuildServiceProvider();
        var store = provider.GetRequiredService<IPulseStore>() as ISyncStore;
        if (store is null)
        {
            await provider.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("PulseTestHost needs a store that can list every event. The in-memory, SQLite and PostgreSQL stores can.");
        }

        var hostedServices = provider.GetServices<IHostedService>().ToList();
        foreach (var hostedService in hostedServices)
        {
            await hostedService.StartAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return new PulseTestHost(provider, hostedServices, store);
    }

    /// <summary>
    /// Sets up the starting state: saves events to a stream, then waits until the handlers have handled them.
    /// These events, and what the handlers do with them, are not part of <see cref="Events"/> and <see cref="Jobs"/>
    /// afterwards.
    /// </summary>
    /// <param name="stream">The stream, for example <c>"Sale-1"</c>.</param>
    /// <param name="events">The events, in order.</param>
    /// <returns>A task that completes when the handlers are done.</returns>
    public Task Given(StreamName stream, params object[] events)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return GivenEvents(stream, events);
    }

    /// <summary>
    /// Sets up the starting state: saves each event to the stream named after its type, like <c>pulse.Emit(event)</c>,
    /// then waits until the handlers have handled them.
    /// </summary>
    /// <param name="events">The events, in order.</param>
    /// <returns>A task that completes when the handlers are done.</returns>
    public Task Given(params object[] events)
    {
        return GivenEvents(null, events);
    }

    /// <summary>
    /// Waits until every handler has handled every event, no job is running or due, and nothing is waiting for a retry.
    /// A handler that failed for good counts as done: check that with <see cref="NothingFailed"/>.
    /// </summary>
    /// <param name="timeout">How long to wait. The default is 10 seconds.</param>
    /// <returns>A task that completes when the engine is idle.</returns>
    /// <exception cref="PulseAssertionException">The engine was still busy after the timeout. The message says with what.</exception>
    public async Task WaitUntilIdle(TimeSpan? timeout = null)
    {
        var limit = timeout ?? DefaultIdleTimeout;
        var deadline = DateTimeOffset.UtcNow + limit;
        Snapshot? previous = null;

        while (true)
        {
            var snapshot = await TakeSnapshot().ConfigureAwait(false);
            if (snapshot.Busy is null && previous is { Busy: null } && previous.Fingerprint == snapshot.Fingerprint)
            {
                return;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new PulseAssertionException(
                    $"PulseSharp was still busy after {limit.TotalSeconds:0.###} seconds. {snapshot.Busy ?? "Its work kept changing."}"
                );
            }

            previous = snapshot;
            await Task.Delay(CheckEvery).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The events emitted since the start or the last <c>Given</c>, by the test or by handlers, in order. Call
    /// <see cref="WaitUntilIdle"/> first to include what handlers emit.
    /// </summary>
    public IReadOnlyList<EmittedEvent> Events
    {
        get
        {
            return RunNow(() => ReadAllEvents(_eventsBefore))
                .Select(stored => new EmittedEvent(ToObject(_names.Events, stored.EventName, stored.Data), stored.Stream, new EmitOptions()))
                .ToList();
        }
    }

    /// <summary>
    /// The jobs enqueued since the start or the last <c>Given</c>, by the test or by handlers, in order, whatever their
    /// state now. Call <see cref="WaitUntilIdle"/> first to include what handlers enqueue.
    /// </summary>
    public IReadOnlyList<EnqueuedJob> Jobs
    {
        get
        {
            return RunNow(ReadAllJobs)
                .Where(job => !_jobsBefore.Contains(job.JobId))
                .Select(job => new EnqueuedJob(
                    ToObject(_names.Jobs, job.JobName, job.Data),
                    job.RunAt > job.EnqueuedAt ? job.RunAt : null,
                    new EnqueueOptions()
                ))
                .ToList();
        }
    }

    /// <summary>
    /// Checks that no handler stopped on a failing event and no job failed. Without this check, a failing handler
    /// would go unnoticed in a test.
    /// </summary>
    /// <exception cref="PulseAssertionException">Something failed. The message shows each failure and its error.</exception>
    public void NothingFailed()
    {
        var failures = RunNow(ReadFailures);
        if (failures.Count > 0)
        {
            throw new PulseAssertionException($"Expected no failures, but found {failures.Count}:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
        }
    }

    /// <inheritdoc />
    public Task<EmitResult> Emit<TEvent>(StreamName stream, TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : notnull
    {
        return _pulse.Emit(stream, @event, cancellationToken);
    }

    /// <inheritdoc />
    public Task<EmitResult> Emit<TEvent>(StreamName stream, TEvent @event, EmitOptions options, CancellationToken cancellationToken = default)
        where TEvent : notnull
    {
        return _pulse.Emit(stream, @event, options, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Guid> Enqueue<TJob>(TJob job, CancellationToken cancellationToken = default)
        where TJob : notnull
    {
        return _pulse.Enqueue(job, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Guid> Enqueue<TJob>(TJob job, EnqueueOptions options, CancellationToken cancellationToken = default)
        where TJob : notnull
    {
        return _pulse.Enqueue(job, options, cancellationToken);
    }

    /// <summary>
    /// Stops the engine and releases everything.
    /// </summary>
    /// <returns>A task that completes when the engine has stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        foreach (var hostedService in _hostedServices.Reverse())
        {
            await hostedService.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await _services.DisposeAsync().ConfigureAwait(false);
    }

    private async Task GivenEvents(StreamName? stream, object[] events)
    {
        ArgumentNullException.ThrowIfNull(events);
        foreach (var @event in events)
        {
            ArgumentNullException.ThrowIfNull(@event, nameof(events));
            var target = stream ?? StreamName.From(_names.Events.NameOf(@event.GetType()));
            await _pulse.Emit(target, @event).ConfigureAwait(false);
        }

        await WaitUntilIdle().ConfigureAwait(false);

        var all = await ReadAllEvents(0).ConfigureAwait(false);
        _eventsBefore = all.Count > 0 ? all[^1].GlobalPosition : 0;
        _jobsBefore = (await ReadAllJobs().ConfigureAwait(false)).Select(job => job.JobId).ToHashSet();
    }

    private async Task<Snapshot> TakeSnapshot()
    {
        var now = _time.GetUtcNow();
        var fingerprint = new StringBuilder();
        string? busy = null;

        var jobs = await ReadAllJobs().ConfigureAwait(false);
        foreach (var job in jobs)
        {
            fingerprint.Append(job.JobId).Append(':').Append((int)job.State).Append(';');
            if (busy is null && _settings.Jobs.ContainsKey(job.JobName))
            {
                busy = job.State switch
                {
                    JobState.Running => $"Job {job.JobName} {job.JobId} is still running.",
                    JobState.Retrying => $"Job {job.JobName} {job.JobId} is waiting for a retry.",
                    JobState.Pending when job.RunAt <= now => $"Job {job.JobName} {job.JobId} is waiting to run.",
                    _ => null
                };
            }
        }

        var cursors = new Dictionary<string, Dictionary<string, HandlerCursor>>(StringComparer.Ordinal);
        foreach (var subscription in _settings.Subscriptions)
        {
            var loaded = await _store.LoadCursors(subscription.Name, CancellationToken.None).ConfigureAwait(false);
            cursors[subscription.Name] = loaded.ToDictionary(cursor => cursor.Stream, StringComparer.Ordinal);
            foreach (var cursor in loaded)
            {
                fingerprint.Append(cursor.HandlerName).Append('/').Append(cursor.Stream).Append(':').Append(cursor.Position).Append((int)cursor.State).Append(';');
            }
        }

        var events = await ReadAllEvents(0).ConfigureAwait(false);
        fingerprint.Append("events:").Append(events.Count > 0 ? events[^1].GlobalPosition : 0);

        foreach (var stored in events)
        {
            if (busy is not null)
            {
                break;
            }

            foreach (var subscription in _settings.Subscriptions.Where(subscription => subscription.Handles(stored.EventName)))
            {
                var cursor = cursors[subscription.Name].GetValueOrDefault(stored.Stream);
                var waiting = cursor is null
                    || cursor.State == CursorState.Retrying
                    || (cursor.State == CursorState.Active && cursor.Position < stored.StreamPosition);
                if (waiting)
                {
                    busy = $"Handler '{subscription.Name}' has not handled event {stored.StreamPosition} ({stored.EventName}) of stream {stored.Stream} yet.";
                    break;
                }
            }
        }

        return new Snapshot(busy, fingerprint.ToString());
    }

    private async Task<List<StoredEvent>> ReadAllEvents(long after)
    {
        var events = new List<StoredEvent>();
        while (true)
        {
            var page = await _store.ReadForSync(new SyncQuery(after, null, null, [], PageSize), CancellationToken.None).ConfigureAwait(false);
            events.AddRange(page);
            if (page.Count < PageSize)
            {
                return events;
            }

            after = page[^1].GlobalPosition;
        }
    }

    private async Task<List<StoredJob>> ReadAllJobs()
    {
        var newestFirst = await _store.GetJobs(null, MaxJobs, CancellationToken.None).ConfigureAwait(false);
        return newestFirst.Reverse().ToList();
    }

    private async Task<List<string>> ReadFailures()
    {
        var failures = new List<string>();
        foreach (var subscription in _settings.Subscriptions)
        {
            var cursors = await _store.LoadCursors(subscription.Name, CancellationToken.None).ConfigureAwait(false);
            failures.AddRange(cursors
                .Where(cursor => cursor.State == CursorState.Failed)
                .Select(cursor => $"  Handler '{cursor.HandlerName}' stopped on stream {cursor.Stream}: {FirstLine(cursor.LastError)}"));
        }

        var jobs = await _store.GetJobs(JobState.Failed, MaxJobs, CancellationToken.None).ConfigureAwait(false);
        failures.AddRange(jobs.Select(job => $"  Job {job.JobName} {job.JobId} failed: {FirstLine(job.LastError)}"));
        return failures;
    }

    private object ToObject(TypeNames names, string name, byte[] data)
    {
        if (names.TypeOf(name) is { } type)
        {
            return _json.Deserialize(data, type);
        }

        using var document = System.Text.Json.JsonDocument.Parse(data);
        return document.RootElement.Clone();
    }

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "no error message";
        }

        var end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end];
    }

    private static T RunNow<T>(Func<Task<T>> read)
    {
        return Task.Run(read).GetAwaiter().GetResult();
    }

    private static void CheckVersions()
    {
        var engine = typeof(PulseSharpBuilder).Assembly.GetName().Version;
        var testing = typeof(PulseTestHost).Assembly.GetName().Version;
        if (engine != testing)
        {
            throw new InvalidOperationException(
                $"PulseSharp.Testing {testing} needs PulseSharp {testing}, but PulseSharp {engine} is loaded. Use the same version of both packages."
            );
        }
    }

    private sealed record Snapshot(string? Busy, string Fingerprint);
}
