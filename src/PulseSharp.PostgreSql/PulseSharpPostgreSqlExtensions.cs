using PulseSharp.PostgreSql;

namespace PulseSharp;

/// <summary>
/// Adds the PostgreSQL store to PulseSharp.
/// </summary>
public static class PulseSharpPostgreSqlExtensions
{
    /// <summary>
    /// Keeps events, cursors and jobs in PostgreSQL. Several app instances can share the database.
    /// </summary>
    /// <param name="pulse">The PulseSharp builder.</param>
    /// <param name="connectionString">An Npgsql connection string.</param>
    /// <param name="schema">The schema for PulseSharp's tables. The default is <c>pulsesharp</c>.</param>
    /// <returns>The builder.</returns>
    /// <example>
    /// <code>
    /// services.AddPulseSharp(pulse => pulse.UsePostgreSql(configuration.GetConnectionString("Events")!));
    /// </code>
    /// </example>
    public static PulseSharpBuilder UsePostgreSql(this PulseSharpBuilder pulse, string connectionString, string schema = "pulsesharp")
    {
        ArgumentNullException.ThrowIfNull(pulse);
        return pulse.UseStore(new PostgresStore(connectionString, schema));
    }
}
