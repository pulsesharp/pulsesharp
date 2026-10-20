using PulseSharp.Sqlite;

namespace PulseSharp;

/// <summary>
/// Adds the SQLite store to PulseSharp.
/// </summary>
public static class PulseSharpSqliteExtensions
{
    /// <summary>
    /// Keeps events, cursors and jobs in a SQLite database, so they survive a restart.
    /// </summary>
    /// <param name="pulse">The PulseSharp builder.</param>
    /// <param name="database">A file name such as <c>app.db</c>, or a Microsoft.Data.Sqlite connection string such as <c>Data Source=app.db</c>.</param>
    /// <returns>The builder.</returns>
    /// <example>
    /// <code>
    /// services.AddPulseSharp(pulse => pulse.UseSqlite("pos.db"));
    /// </code>
    /// </example>
    public static PulseSharpBuilder UseSqlite(this PulseSharpBuilder pulse, string database)
    {
        ArgumentNullException.ThrowIfNull(pulse);
        return pulse.UseStore(new SqliteStore(database));
    }
}
