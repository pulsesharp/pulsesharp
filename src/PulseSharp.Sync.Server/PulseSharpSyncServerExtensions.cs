using Microsoft.Extensions.DependencyInjection;
using PulseSharp.Sync.Server;

namespace PulseSharp;

/// <summary>
/// Adds the server side of PulseSharp sync.
/// </summary>
public static class PulseSharpSyncServerExtensions
{
    /// <summary>
    /// Lets devices sync with this app: they send the events they emit, and receive this app's events.
    /// Map the endpoint with <c>app.MapPulseSharpSync()</c>.
    /// </summary>
    /// <param name="pulse">The PulseSharp builder.</param>
    /// <param name="configure">Says which nodes may sync and what they may receive.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="InvalidOperationException">No node is allowed, or the sync server is added twice.</exception>
    /// <example>
    /// <code>
    /// builder.Services.AddPulseSharpPostgreSql(connectionString, pulse =>
    /// {
    ///     pulse.Node = "cloud";
    ///     pulse.UseSyncServer(server => server.AllowNode("till-1", tillKey));
    /// });
    ///
    /// app.MapPulseSharpSync();
    /// </code>
    /// </example>
    public static PulseSharpBuilder UseSyncServer(this PulseSharpBuilder pulse, Action<SyncServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(pulse);
        ArgumentNullException.ThrowIfNull(configure);

        if (pulse.Services.Any(descriptor => descriptor.ServiceType == typeof(SyncServerSetup)))
        {
            throw new InvalidOperationException("The sync server was added twice. Call UseSyncServer once.");
        }

        var options = new SyncServerOptions();
        configure(options);

        if (options.Nodes.Count == 0 && options.NodeFromUser is null)
        {
            throw new InvalidOperationException(
                "The sync server needs to know who may sync: add server.AllowNode(\"till-1\", key), or server.NodeFrom(user => ...)."
            );
        }

        if (options.MaxBatchSize < 1)
        {
            throw new InvalidOperationException("The sync server: MaxBatchSize must be at least 1.");
        }

        pulse.Services.AddSingleton(new SyncServerSetup(options, () => pulse.Node));
        return pulse;
    }
}

internal sealed record SyncServerSetup(SyncServerOptions Options, Func<string> Node);
