using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PulseSharp.Delivery;
using PulseSharp.Naming;
using PulseSharp.Stores;

namespace PulseSharp;

/// <summary>
/// Configures PulseSharp: the store, the handlers and the delivery settings.
/// Get one from <c>services.AddPulseSharp(pulse => { ... })</c>.
/// </summary>
public sealed class PulseSharpBuilder
{
    private const string ReflectionMessage =
        "Finds the handled event types with reflection. With trimming or Native AOT, use AddHandler<TEvent, THandler>() instead.";

    private readonly IServiceCollection _services;
    private readonly Dictionary<string, Subscription> _subscriptions = new(StringComparer.Ordinal);
    private Func<IPulseStore>? _createStore;

    internal PulseSharpBuilder(IServiceCollection services)
    {
        _services = services;
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
    /// How often handlers check for new events when nothing wakes them up. The default is 1 second.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    internal EventNames EventNames { get; } = new();

    /// <summary>
    /// Keeps events in memory. Everything is lost when the app stops: use it for tests and samples.
    /// </summary>
    /// <returns>This builder.</returns>
    public PulseSharpBuilder UseInMemory()
    {
        _createStore = () => new InMemoryStore();
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
            (_, @event, context, cancellationToken) => handler((TEvent)@event, context, cancellationToken)
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
            InvokeHandlerClass<TEvent, THandler>
        );
        return this;
    }

    /// <summary>
    /// Adds a handler class for every <see cref="IEventHandler{TEvent}"/> it implements.
    /// </summary>
    /// <typeparam name="THandler">The handler class. Its stable name is the class name, or the name in <see cref="HandlerNameAttribute"/>.</typeparam>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="THandler"/> implements no <see cref="IEventHandler{TEvent}"/>, or another handler already uses the same name.
    /// </exception>
    [RequiresDynamicCode(ReflectionMessage)]
    [RequiresUnreferencedCode(ReflectionMessage)]
    public PulseSharpBuilder AddHandler<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces)] THandler>()
        where THandler : class
    {
        var eventTypes = typeof(THandler)
            .GetInterfaces()
            .Where(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEventHandler<>))
            .Select(type => type.GetGenericArguments()[0])
            .ToList();

        if (eventTypes.Count == 0)
        {
            throw new InvalidOperationException($"{typeof(THandler).FullName} does not implement IEventHandler<TEvent>.");
        }

        var addHandler = typeof(PulseSharpBuilder).GetMethod(nameof(AddHandlerFor), BindingFlags.NonPublic | BindingFlags.Instance)!;
        foreach (var eventType in eventTypes)
        {
            addHandler.MakeGenericMethod(eventType, typeof(THandler)).Invoke(this, null);
        }

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

        if (RetryDelay <= TimeSpan.Zero || PollInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("PulseSharp: RetryDelay and PollInterval must be greater than zero.");
        }

        return new PulseSharpSettings(Node, MaxAttempts, RetryDelay, PollInterval, _subscriptions.Values.ToList());
    }

    internal IPulseStore CreateStore()
    {
        if (_createStore is null)
        {
            throw new InvalidOperationException("PulseSharp needs a store. Choose one, for example pulse.UseInMemory().");
        }

        return _createStore();
    }

    private void AddHandlerFor<TEvent, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>()
        where TEvent : notnull
        where THandler : class, IEventHandler<TEvent>
    {
        AddHandler<TEvent, THandler>();
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
        object @event,
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
            await handler.Handle((TEvent)@event, context, cancellationToken).ConfigureAwait(false);
        }
    }
}
