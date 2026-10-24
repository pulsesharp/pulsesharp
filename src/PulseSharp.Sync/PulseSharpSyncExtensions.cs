using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PulseSharp.Stores;
using PulseSharp.Sync;

namespace PulseSharp;

/// <summary>
/// Adds device to cloud sync to PulseSharp.
/// </summary>
public static class PulseSharpSyncExtensions
{
    /// <summary>
    /// Sends the events this app emits to a server, and receives the server's events, whenever the server can be
    /// reached. The app keeps working offline; nothing is lost or sent twice.
    /// </summary>
    /// <param name="pulse">The PulseSharp builder.</param>
    /// <param name="server">The server's sync endpoint, for example <c>https://cloud.example.com/pulse/sync</c>.</param>
    /// <param name="apiKey">The key the server gave this node.</param>
    /// <returns>The builder.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddPulseSharpSqlite("till.db", pulse =>
    /// {
    ///     pulse.Node = "till-1";
    ///     pulse.SyncWithCloud("https://cloud.example.com/pulse/sync", apiKey);
    /// });
    /// </code>
    /// </example>
    public static PulseSharpBuilder SyncWithCloud(this PulseSharpBuilder pulse, string server, string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        return pulse.UseSync(sync =>
        {
            sync.Server = new Uri(server, UriKind.Absolute);
            sync.ApiKey = apiKey;
        });
    }

    /// <summary>
    /// Syncs this app's events with a server, with full control over what is sent and received.
    /// </summary>
    /// <param name="pulse">The PulseSharp builder.</param>
    /// <param name="configure">Sets the server address and the other options.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="InvalidOperationException">Sync is added twice, or an option is not valid.</exception>
    /// <remarks>
    /// Sync runs in the background. Inject <see cref="IPulseSync"/> to show its status or to sync at once.
    /// Run sync in one app instance per node.
    /// </remarks>
    /// <example>
    /// <code>
    /// pulse.UseSync(sync =>
    /// {
    ///     sync.Server = new Uri("https://cloud.example.com/pulse/sync");
    ///     sync.ApiKey = apiKey;
    ///     sync.PullStreams.Add("Product-");
    /// });
    /// </code>
    /// </example>
    public static PulseSharpBuilder UseSync(this PulseSharpBuilder pulse, Action<SyncOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(pulse);
        ArgumentNullException.ThrowIfNull(configure);

        if (pulse.Services.Any(descriptor => descriptor.ServiceType == typeof(IPulseSync)))
        {
            throw new InvalidOperationException("Sync was added twice. Call UseSync or SyncWithCloud once.");
        }

        var options = new SyncOptions();
        configure(options);
        Validate(options);

        pulse.Services.AddSingleton(provider => new SyncService(
            options,
            () => pulse.Node,
            SyncStore(provider),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<PulseSharp.Delivery.WakeUpSignal>(),
            (provider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance).CreateLogger("PulseSharp.Sync")
        ));
        pulse.Services.AddSingleton<IPulseSync>(provider => provider.GetRequiredService<SyncService>());
        pulse.Services.AddSingleton<PulseSharp.Delivery.IRetentionGuard>(provider => provider.GetRequiredService<SyncService>());
        pulse.Services.AddHostedService(provider => provider.GetRequiredService<SyncService>());
        return pulse;
    }

    private static ISyncStore SyncStore(IServiceProvider provider)
    {
        var store = provider.GetRequiredService<IPulseStore>();
        return store as ISyncStore ?? throw new InvalidOperationException(
            $"Sync needs a store that supports it, and {store.GetType().Name} does not. " +
            "The in-memory, SQLite and PostgreSQL stores all do."
        );
    }

    private static void Validate(SyncOptions options)
    {
        if (options.Server is not { IsAbsoluteUri: true } server || (server.Scheme != Uri.UriSchemeHttps && server.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException("Sync needs the server address, for example https://cloud.example.com/pulse/sync.");
        }

        if (!options.Push && !options.Pull)
        {
            throw new InvalidOperationException("Sync has nothing to do: Push and Pull are both off.");
        }

        if (options.Interval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Sync: Interval must be greater than zero.");
        }

        if (options.BatchSize is < 1 or > 1000)
        {
            throw new InvalidOperationException("Sync: BatchSize must be between 1 and 1000.");
        }

        if (options.PullStreams.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("Sync: PullStreams must not contain an empty prefix.");
        }
    }
}
