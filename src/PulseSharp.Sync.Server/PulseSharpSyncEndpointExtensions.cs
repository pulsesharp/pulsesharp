using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PulseSharp;
using PulseSharp.Stores;
using PulseSharp.Sync.Server;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Maps the endpoint of PulseSharp sync in an ASP.NET Core app.
/// </summary>
public static class PulseSharpSyncEndpointExtensions
{
    /// <summary>
    /// Maps the sync endpoint that devices call. Add the sync server first with <see cref="PulseSharpSyncServerExtensions.UseSyncServer"/>.
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

        var logger = endpoints.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("PulseSharp.Sync.Server");
        var endpoint = new SyncEndpoint(setup.Options, syncStore, setup.Node, logger);
        var group = endpoints.MapGroup(pattern);
        group.MapGet("info", (RequestDelegate)endpoint.Info);
        group.MapPost("push", (RequestDelegate)endpoint.Push);
        group.MapGet("pull", (RequestDelegate)endpoint.Pull);
        return group;
    }
}

