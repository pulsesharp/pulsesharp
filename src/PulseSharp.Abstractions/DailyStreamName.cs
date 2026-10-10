using System.Globalization;

namespace PulseSharp;

/// <summary>
/// A stream that starts again every day: <c>{Category}-{yyyy-MM-dd}</c>.
/// </summary>
/// <remarks>
/// Use it for app-wide events that are not about one entity, such as settings or navigation changes.
/// Without a daily split, such a stream would grow for the whole life of the app.
/// </remarks>
/// <example>
/// <code>
/// public sealed record SettingsStream(DateOnly Date) : DailyStreamName(Date)
/// {
///     protected override string Category
///     {
///         get
///         {
///             return "Settings";
///         }
///     }
/// }
/// </code>
/// </example>
/// <param name="Date">The day of the stream.</param>
public abstract record DailyStreamName(DateOnly Date) : StreamName
{
    /// <summary>
    /// The part of the name before the date, for example <c>Settings</c>.
    /// </summary>
    protected abstract string Category { get; }

    /// <summary>
    /// Returns <c>{Category}-{yyyy-MM-dd}</c>.
    /// </summary>
    /// <returns>The stream name.</returns>
    public sealed override string Value()
    {
        return string.Concat(Category, "-", Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }
}
