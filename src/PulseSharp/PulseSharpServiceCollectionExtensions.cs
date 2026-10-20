using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PulseSharp;
using PulseSharp.Control;
using PulseSharp.Delivery;
using PulseSharp.Naming;
using PulseSharp.Stores;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Adds PulseSharp to a service collection.
/// </summary>
public static class PulseSharpServiceCollectionExtensions
{
    /// <summary>
    /// Adds PulseSharp: <see cref="IPulse"/> for emitting events, <see cref="IPulseControl"/> for steering handlers,
    /// and a hosted service that delivers events to handlers.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Chooses the store and adds the handlers.</param>
    /// <returns>The service collection.</returns>
    /// <exception cref="InvalidOperationException">
    /// PulseSharp is added twice, no store is chosen, two handlers or two event types share a name, or a setting is invalid.
    /// </exception>
    /// <example>
    /// <code>
    /// services.AddPulseSharp(pulse =>
    /// {
    ///     pulse.UseInMemory();
    ///     pulse.On&lt;SaleCompleted&gt;("print-receipt", async (sale, context, ct) => await PrintReceipt(sale, ct));
    ///     pulse.AddHandler&lt;SaleCompleted, UpdateStock&gt;();
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddPulseSharp(this IServiceCollection services, Action<PulseSharpBuilder> configure)
    {
        return Add(services, configure);
    }

    /// <summary>
    /// Adds PulseSharp and keeps its events and jobs in memory. Everything is lost when the app stops: use it to
    /// learn, in tests and in samples. For data that must survive a restart, use <c>AddPulseSharpSqlite</c> or
    /// <c>AddPulseSharpPostgreSql</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Adds handlers and changes settings. Optional.</param>
    /// <returns>The service collection.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddPulseSharpInMemory(pulse =>
    /// {
    ///     pulse.When&lt;SaleCompleted&gt;(sale => Console.WriteLine($"Sold for {sale.Total}"));
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddPulseSharpInMemory(this IServiceCollection services, Action<PulseSharpBuilder>? configure = null)
    {
        return Add(services, pulse =>
        {
            pulse.UseInMemory();
            configure?.Invoke(pulse);
        });
    }

    private static IServiceCollection Add(IServiceCollection services, Action<PulseSharpBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(IPulse)))
        {
            throw new InvalidOperationException("AddPulseSharp was called twice. Configure PulseSharp in a single call.");
        }

        var builder = new PulseSharpBuilder(services);
        configure(builder);

        var settings = builder.BuildSettings();
        var store = builder.CreateStore();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(settings);
        services.AddSingleton(new PulseNames(builder.EventNames, builder.JobNames));
        services.AddSingleton(builder.Json);
        services.AddSingleton(store);
        services.AddSingleton<WakeUpSignal>();
        services.AddSingleton<IPulse, Pulse>();
        services.AddSingleton<IPulseControl, PulseControl>();
        services.AddHostedService(provider => new PulseSharpHostedService(
            settings,
            store,
            provider.GetRequiredService<WakeUpSignal>(),
            provider,
            provider.GetRequiredService<TimeProvider>(),
            provider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance
        ));

        return services;
    }
}
