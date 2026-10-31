using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PulseSharp.Delivery;
using PulseSharp.Jobs;
using PulseSharp.Naming;
using PulseSharp.Stores;

namespace PulseSharp;

/// <summary>
/// Configures PulseSharp: the store, the handlers and the delivery settings.
/// Get one from <c>services.AddPulseSharp(pulse => { ... })</c>.
/// </summary>
public sealed class PulseSharpBuilder
{
    private readonly IServiceCollection _services;
    private readonly Dictionary<string, Subscription> _subscriptions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JobRegistration> _jobs = new(StringComparer.Ordinal);
    private Func<IPulseStore>? _createStore;

    internal PulseSharpBuilder(IServiceCollection services)
    {
        _services = services;
    }

    /// <summary>
    /// The service collection PulseSharp is added to. Extension packages, such as sync, register their services here.
    /// </summary>
    public IServiceCollection Services
    {
        get
        {
            return _services;
        }
    }

    /// <summary>
    /// The name of this app instance, stored with every event it emits, for example <c>till-1</c> or <c>cloud</c>.
    /// The default is the machine name.
    /// </summary>
    public string Node { get; set; } = Environment.MachineName.ToLowerInvariant();

    /// <summary>
    /// How many times a handler tries one event before its stream stops for that handler. The default is 5.
    /// </summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>
    /// The wait before the first retry. Each later retry waits twice as long, up to 5 minutes. The default is 1 second.
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Settings most apps never need to change: polling, parallelism, time limits, clean-up and JSON options.
    /// </summary>
    public PulseSharpAdvanced Advanced { get; } = new();

    internal PulseJson Json { get; } = new();

    internal TypeNames EventNames { get; } = TypeNames.ForEvents();

    internal TypeNames JobNames { get; } = TypeNames.ForJobs();

    /// <summary>
    /// Uses a store: the place where events, cursors and jobs are kept. Store packages call this from their own
    /// method, such as <c>UseSqlite</c>.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <returns>This builder.</returns>
    public PulseSharpBuilder UseStore(IPulseStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _createStore = () => store;
        return this;
    }

    /// <summary>
    /// Adds an inline handler for <typeparamref name="TEvent"/>.
    /// </summary>
    /// <typeparam name="TEvent">The event type.</typeparam>
    /// <param name="name">
    /// The stable name of the handler, for example <c>print-receipt</c>. PulseSharp stores how far the handler has got
    /// under this name, so keep it the same between versions of your app.
    /// </param>
    /// <param name="handler">The code that handles one event.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">Another handler already uses <paramref name="name"/>.</exception>
    public PulseSharpBuilder On<TEvent>(string name, Func<TEvent, EventContext, CancellationToken, Task> handler)
        where TEvent : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handler);

        AddToSubscription(
            name,
            handler,
            typeof(TEvent),
            (_, data, context, cancellationToken) => handler(Json.Deserialize<TEvent>(data), context, cancellationToken)
        );
        return this;
    }

    /// <summary>
    /// Adds a handler class for <typeparamref name="TEvent"/>. A new instance is created from dependency injection
    /// for every event. Works with trimming and Native AOT.
    /// </summary>
    /// <typeparam name="TEvent">The event type.</typeparam>
    /// <typeparam name="THandler">The handler class. Its stable name is the class name, or the name in <see cref="HandlerNameAttribute"/>.</typeparam>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">Another handler already uses the same name.</exception>
    public PulseSharpBuilder AddHandler<TEvent, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>()
        where TEvent : notnull
        where THandler : class, IEventHandler<TEvent>
    {
        _services.TryAddTransient<THandler>();
        AddToSubscription(
            HandlerNames.NameOf(typeof(THandler)),
            typeof(THandler),
            typeof(TEvent),
            (services, data, context, cancellationToken) =>
                InvokeHandlerClass<TEvent, THandler>(services, Json.Deserialize<TEvent>(data), context, cancellationToken)
        );
        return this;
    }

    /// <summary>
    /// Adds an inline handler for jobs of type <typeparamref name="TJob"/>. A job type has exactly one handler.
    /// </summary>
    /// <typeparam name="TJob">The job type. Its stored name is the class name, or the name in <see cref="JobNameAttribute"/>.</typeparam>
    /// <param name="handler">The code that runs one job.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException"><typeparamref name="TJob"/> already has a handler.</exception>
    public PulseSharpBuilder OnJob<TJob>(Func<TJob, JobContext, CancellationToken, Task> handler)
        where TJob : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);

        AddJob(
            typeof(TJob),
            handler,
            (_, data, context, cancellationToken) => handler(Json.Deserialize<TJob>(data), context, cancellationToken)
        );
        return this;
    }

    /// <summary>
    /// Adds a handler class for jobs of type <typeparamref name="TJob"/>. A new instance is created from dependency
    /// injection for every job. Works with trimming and Native AOT.
    /// </summary>
    /// <typeparam name="TJob">The job type. Its stored name is the class name, or the name in <see cref="JobNameAttribute"/>.</typeparam>
    /// <typeparam name="THandler">The handler class.</typeparam>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException"><typeparamref name="TJob"/> already has another handler.</exception>
    public PulseSharpBuilder AddJobHandler<TJob, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>()
        where TJob : notnull
        where THandler : class, IJobHandler<TJob>
    {
        _services.TryAddTransient<THandler>();
        AddJob(
            typeof(TJob),
            typeof(THandler),
            (services, data, context, cancellationToken) =>
                InvokeJobHandlerClass<TJob, THandler>(services, Json.Deserialize<TJob>(data), context, cancellationToken)
        );
        return this;
    }

    internal PulseSharpSettings BuildSettings()
    {
        if (string.IsNullOrWhiteSpace(Node))
        {
            throw new InvalidOperationException("PulseSharp: Node must not be empty.");
        }

        if (MaxAttempts < 1)
        {
            throw new InvalidOperationException("PulseSharp: MaxAttempts must be at least 1.");
        }

        if (RetryDelay <= TimeSpan.Zero || Advanced.PollInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("PulseSharp: RetryDelay and Advanced.PollInterval must be greater than zero.");
        }

        if (Advanced.MaxParallelStreams < 1 || Advanced.MaxParallelJobs < 1)
        {
            throw new InvalidOperationException("PulseSharp: Advanced.MaxParallelStreams and Advanced.MaxParallelJobs must be at least 1.");
        }

        if (Advanced.JsonOptions is not null)
        {
            Json.Use(Advanced.JsonOptions);
        }

        if (Advanced.KeepFinishedJobs <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("PulseSharp: Advanced.KeepFinishedJobs must be greater than zero, or null to keep every job.");
        }

        if (Advanced.HandlerTimeout <= TimeSpan.Zero || Advanced.JobTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "PulseSharp: Advanced.HandlerTimeout and Advanced.JobTimeout must be greater than zero, or null for no limit."
            );
        }

        return new PulseSharpSettings(
            Node,
            MaxAttempts,
            RetryDelay,
            Advanced.PollInterval,
            Advanced.MaxParallelStreams,
            Advanced.MaxParallelJobs,
            PulseSharpSettings.LeaseFor(Advanced.PollInterval, missedRenewals: 30),
            PulseSharpSettings.LeaseFor(Advanced.PollInterval, missedRenewals: 10),
            Advanced.KeepFinishedJobs,
            Advanced.HandlerTimeout,
            Advanced.JobTimeout,
            _subscriptions.Values.ToList(),
            new Dictionary<string, JobRegistration>(_jobs, StringComparer.Ordinal)
        );
    }

    internal IPulseStore CreateStore()
    {
        if (_createStore is null)
        {
            throw new InvalidOperationException("PulseSharp needs a store. Choose one: pulse.UseSqlite(\"app.db\") or pulse.UsePostgreSql(connectionString).");
        }

        return _createStore();
    }

    private void AddJob(Type jobType, object source, JobInvoker invoker)
    {
        var jobName = JobNames.NameOf(jobType);

        if (_jobs.TryGetValue(jobName, out var existing))
        {
            if (Equals(existing.Source, source))
            {
                return;
            }

            throw new InvalidOperationException(
                $"The job '{jobName}' already has a handler. A job type has exactly one handler."
            );
        }

        _jobs.Add(jobName, new JobRegistration(jobName, source, invoker));
    }

    private static async Task InvokeJobHandlerClass<TJob, THandler>(
        IServiceProvider services,
        TJob job,
        JobContext context,
        CancellationToken cancellationToken
    )
        where TJob : notnull
        where THandler : class, IJobHandler<TJob>
    {
        var scope = services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var handler = scope.ServiceProvider.GetRequiredService<THandler>();
            await handler.Handle(job, context, cancellationToken).ConfigureAwait(false);
        }
    }

    private void AddToSubscription(string name, object source, Type eventType, EventInvoker invoker)
    {
        var eventName = EventNames.NameOf(eventType);

        if (!_subscriptions.TryGetValue(name, out var subscription))
        {
            subscription = new Subscription(name, source);
            _subscriptions.Add(name, subscription);
        }
        else if (!Equals(subscription.Source, source))
        {
            throw new InvalidOperationException(
                $"The handler name '{name}' is used twice. Handler names must be unique: " +
                "set one with [HandlerName(\"...\")] or with the name argument of On."
            );
        }

        subscription.Add(eventName, invoker);
    }

    private static async Task InvokeHandlerClass<TEvent, THandler>(
        IServiceProvider services,
        TEvent @event,
        EventContext context,
        CancellationToken cancellationToken
    )
        where TEvent : notnull
        where THandler : class, IEventHandler<TEvent>
    {
        var scope = services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var handler = scope.ServiceProvider.GetRequiredService<THandler>();
            await handler.Handle(@event, context, cancellationToken).ConfigureAwait(false);
        }
    }
}
