using PulseSharp;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Adds PulseSharp with the SQLite store in one call.
/// </summary>
public static class PulseSharpSqliteServiceCollectionExtensions
{
    /// <summary>
    /// Adds PulseSharp and keeps its events and jobs in a SQLite database file, so they survive a restart.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="database">A file name such as <c>app.db</c>, or a full connection string such as <c>Data Source=app.db</c>.</param>
    /// <param name="configure">Adds handlers and changes settings. Optional.</param>
    /// <returns>The service collection.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddPulseSharpSqlite("app.db", pulse =>
    /// {
    ///     pulse.When&lt;SaleCompleted&gt;(sale => Console.WriteLine($"Sold for {sale.Total}"));
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddPulseSharpSqlite(
        this IServiceCollection services,
        string database,
        Action<PulseSharpBuilder>? configure = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        var connectionString = database.Contains('=', StringComparison.Ordinal) ? database : $"Data Source={database}";

        return services.AddPulseSharp(pulse =>
        {
            pulse.UseSqlite(connectionString);
            configure?.Invoke(pulse);
        });
    }
}
