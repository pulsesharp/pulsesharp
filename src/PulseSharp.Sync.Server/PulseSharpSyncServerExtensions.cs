using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PulseSharp;
using PulseSharp.Stores;
using PulseSharp.Sync.Server;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Adds the server side of PulseSharp sync to an ASP.NET Core app.
/// </summary>
public static class PulseSharpSyncServerExtensions
{
    /// <summary>
    /// Lets devices sync with this app: they send the events they emit, and receive this app's events.
    /// Map the endpoint with <see cref="MapPulseSharpSync"/>.
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

    /// <summary>
    /// Maps the sync endpoint that devices call. Add the sync server first with <see cref="UseSyncServer"/>.
    /// </summary>
    /// <param name="endpoints">The app's endpoints.</param>
    /// <param name="pattern">The address of the endpoint. The default is <c>/pulse/sync</c>.</param>
    /// <returns>A builder for the endpoint, for example to add <c>.RequireAuthorization()</c>.</returns>
    /// <exception cref="InvalidOperationException">The sync server was not added, or the store can't sync.</exception>
    public static IEndpointConventionBuilder MapPulseSharpSync(this IEndpointRouteBuilder endpoints, string pattern = "/pulse/sync")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        var setup = endpoints.ServiceProvider.GetService<SyncServerSetup>() ?? throw new InvalidOperationException(
            "Add the sync server first: call pulse.UseSyncServer(...) inside AddPulseSharp."
        );
        var store = endpoints.ServiceProvider.GetRequiredService<IPulseStore>();
        var syncStore = store as ISyncStore ?? throw new InvalidOperationException(
            $"Sync needs a store that supports it, and {store.GetType().Name} does not. The in-memory, SQLite and PostgreSQL stores all do."
        );

        var endpoint = new SyncEndpoint(setup.Options, syncStore, setup.Node);
        var group = endpoints.MapGroup(pattern);
        group.MapGet("info", (RequestDelegate)endpoint.Info);
        group.MapPost("push", (RequestDelegate)endpoint.Push);
        group.MapGet("pull", (RequestDelegate)endpoint.Pull);
        return group;
    }
}

internal sealed record SyncServerSetup(SyncServerOptions Options, Func<string> Node);
