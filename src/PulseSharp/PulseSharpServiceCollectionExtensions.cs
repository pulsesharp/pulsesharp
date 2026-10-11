using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PulseSharp;
using PulseSharp.Control;
using PulseSharp.Delivery;
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
        services.AddSingleton(builder.EventNames);
        services.AddSingleton(store);
        services.AddSingleton<WakeUpSignal>();
        services.AddSingleton<ControlRequests>();
        services.AddSingleton<IPulse, Pulse>();
        services.AddSingleton<IPulseControl, PulseControl>();
        services.AddHostedService(provider => new PulseSharpHostedService(
            settings,
            store,
            provider.GetRequiredService<WakeUpSignal>(),
            provider.GetRequiredService<ControlRequests>(),
            provider,
            provider.GetRequiredService<TimeProvider>(),
            provider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance
        ));

        return services;
    }
}
