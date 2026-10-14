using PulseSharp;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Adds PulseSharp with the PostgreSQL store in one call.
/// </summary>
public static class PulseSharpPostgreSqlServiceCollectionExtensions
{
    /// <summary>
    /// Adds PulseSharp and keeps its events and jobs in PostgreSQL, in the <c>pulsesharp</c> schema.
    /// Several app instances can share the database.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">An Npgsql connection string.</param>
    /// <param name="configure">Adds handlers and changes settings. Optional.</param>
    /// <returns>The service collection.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddPulseSharpPostgreSql(builder.Configuration.GetConnectionString("Events")!, pulse =>
    /// {
    ///     pulse.When&lt;OrderPlaced&gt;(order => Console.WriteLine($"New order {order.Id}"));
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddPulseSharpPostgreSql(
        this IServiceCollection services,
        string connectionString,
        Action<PulseSharpBuilder>? configure = null
    )
    {
        return services.AddPulseSharp(pulse =>
        {
            pulse.UsePostgreSql(connectionString);
            configure?.Invoke(pulse);
        });
    }
}
