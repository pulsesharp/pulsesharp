using PulseSharp.Testing;

namespace PulseSharp;

/// <summary>
/// Keeps PulseSharp's data in memory, for tests.
/// </summary>
public static class PulseSharpInMemoryExtensions
{
    /// <summary>
    /// Keeps events, cursors and jobs in memory. Everything is lost when the app stops, so use it only in tests.
    /// <see cref="PulseTestHost"/> uses it by default.
    /// </summary>
    /// <param name="pulse">The PulseSharp builder.</param>
    /// <returns>The builder.</returns>
    public static PulseSharpBuilder UseInMemory(this PulseSharpBuilder pulse)
    {
        ArgumentNullException.ThrowIfNull(pulse);
        return pulse.UseStore(new InMemoryStore());
    }
}
