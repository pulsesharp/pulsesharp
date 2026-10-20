using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace PulseSharp.Testing;

/// <summary>
/// Checks on <see cref="FakePulse"/> and <see cref="PulseTestHost"/>. Each one fails with a message that shows what was
/// expected and everything that really happened. The ones that find something return it, so you can check it further
/// with your usual asserts.
/// </summary>
/// <remarks>
/// Write your own checks the same way: an extension method on <see cref="IRecordedPulse"/> that reads
/// <see cref="IRecordedPulse.Events"/> and <see cref="IRecordedPulse.Jobs"/>.
/// </remarks>
public static class PulseAssertions
{
    private const int ShownItems = 20;

    /// <summary>
    /// Checks that exactly one <typeparamref name="TEvent"/> was emitted, and returns it.
    /// </summary>
    /// <typeparam name="TEvent">The event type.</typeparam>
    /// <param name="pulse">The fake or the test host.</param>
    /// <returns>The event.</returns>
    /// <exception cref="PulseAssertionException">None was emitted, or more than one.</exception>
    public static TEvent Emitted<TEvent>(this IRecordedPulse pulse)
    {
        ArgumentNullException.ThrowIfNull(pulse);
        return One(EventsOf<TEvent>(pulse, null), $"one {typeof(TEvent).Name}", pulse);
    }

    /// <summary>
    /// Checks that exactly one <typeparamref name="TEvent"/> was emitted to a stream, and returns it.
    /// </summary>
    /// <typeparam name="TEvent">The event type.</typeparam>
    /// <param name="pulse">The fake or the test host.</param>
    /// <param name="stream">The stream, for example <c>"Sale-1"</c>.</param>
    /// <returns>The event.</returns>
    /// <exception cref="PulseAssertionException">None was emitted to the stream, or more than one.</exception>
    public static TEvent Emitted<TEvent>(this IRecordedPulse pulse, StreamName stream)
    {
        ArgumentNullException.ThrowIfNull(pulse);
        ArgumentNullException.ThrowIfNull(stream);
        return One(EventsOf<TEvent>(pulse, stream), $"one {typeof(TEvent).Name} in stream {stream.Value()}", pulse);
    }

    /// <summary>
    /// Checks that exactly one event equal to <paramref name="expected"/> was emitted, and returns it. Records are
    /// compared by value; other types by their JSON.
    /// </summary>
    /// <typeparam name="TEvent">The event type.</typeparam>
    /// <param name="pulse">The fake or the test host.</param>
    /// <param name="expected">The event you expect.</param>
    /// <returns>The emitted event.</returns>
    /// <exception cref="PulseAssertionException">No such event was emitted, or more than one.</exception>
    /// <example>
    /// <code>
    /// pulse.Emitted(new StockWentNegative("coffee"));
    /// </code>
    /// </example>
    public static TEvent Emitted<TEvent>(this IRecordedPulse pulse, TEvent expected)
        where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(pulse);
        ArgumentNullException.ThrowIfNull(expected);
        var matches = EventsOf<TEvent>(pulse, null).Where(emitted => Same(emitted, expected)).ToList();
        return One(matches, $"one {expected}", pulse);
    }

    /// <summary>
    /// Checks that exactly one <typeparamref name="TEvent"/> that matches a condition was emitted, and returns it.
    /// </summary>
    /// <typeparam name="TEvent">The event type.</typeparam>
    /// <param name="pulse">The fake or the test host.</param>
    /// <param name="where">The condition.</param>
    /// <param name="condition">Filled in by the compiler with the condition's code, for the failure message.</param>
    /// <returns>The event.</returns>
    /// <exception cref="PulseAssertionException">No such event was emitted, or more than one.</exception>
    /// <example>
    /// <code>
    /// pulse.Emitted&lt;SaleCompleted&gt;(sale => sale.Total == 25m);
    /// </code>
    /// </example>
    public static TEvent Emitted<TEvent>(
        this IRecordedPulse pulse,
        Func<TEvent, bool> where,
        [CallerArgumentExpression(nameof(where))] string? condition = null
    )
    {
        ArgumentNullException.ThrowIfNull(pulse);
        ArgumentNullException.ThrowIfNull(where);
        return One(EventsOf<TEvent>(pulse, null).Where(where).ToList(), $"one {typeof(TEvent).Name} where {condition}", pulse);
    }

    /// <summary>
    /// Returns every <typeparamref name="TEvent"/> that was emitted, in order, without checking anything.
    /// </summary>
    /// <typeparam name="TEvent">The event type.</typeparam>
    /// <param name="pulse">The fake or the test host.</param>
    /// <param name="stream">Only events of this stream, or <see langword="null"/> for every stream.</param>
    /// <returns>The events.</returns>
    public static IReadOnlyList<TEvent> AllEmitted<TEvent>(this IRecordedPulse pulse, StreamName? stream = null)
    {
        ArgumentNullException.ThrowIfNull(pulse);
        return EventsOf<TEvent>(pulse, stream);
    }

    /// <summary>
    /// Checks that no <typeparamref name="TEvent"/> was emitted.
    /// </summary>
    /// <typeparam name="TEvent">The event type.</typeparam>
    /// <param name="pulse">The fake or the test host.</param>
    /// <param name="stream">Only check this stream, or <see langword="null"/> for every stream.</param>
    /// <exception cref="PulseAssertionException">One was emitted.</exception>
    public static void NotEmitted<TEvent>(this IRecordedPulse pulse, StreamName? stream = null)
    {
        ArgumentNullException.ThrowIfNull(pulse);
        var found = EventsOf<TEvent>(pulse, stream);
        if (found.Count > 0)
        {
            var where = stream is null ? string.Empty : $" in stream {stream.Value()}";
            throw Failure($"no {typeof(TEvent).Name}{where}", found.Count, pulse);
        }
    }

    /// <summary>
    /// Checks that exactly one <typeparamref name="TJob"/> was enqueued, and returns it.
    /// </summary>
    /// <typeparam name="TJob">The job type.</typeparam>
    /// <param name="pulse">The fake or the test host.</param>
    /// <returns>The job.</returns>
    /// <exception cref="PulseAssertionException">None was enqueued, or more than one.</exception>
    public static TJob Enqueued<TJob>(this IRecordedPulse pulse)
    {
        ArgumentNullException.ThrowIfNull(pulse);
        return One(JobsOf<TJob>(pulse), $"one {typeof(TJob).Name} job", pulse);
    }

    /// <summary>
    /// Checks that exactly one job equal to <paramref name="expected"/> was enqueued, and returns it. Records are
    /// compared by value; other types by their JSON.
    /// </summary>
    /// <typeparam name="TJob">The job type.</typeparam>
    /// <param name="pulse">The fake or the test host.</param>
    /// <param name="expected">The job you expect.</param>
    /// <returns>The enqueued job.</returns>
    /// <exception cref="PulseAssertionException">No such job was enqueued, or more than one.</exception>
    /// <example>
    /// <code>
    /// pulse.Enqueued(new SendReceipt(saleId));
    /// </code>
    /// </example>
    public static TJob Enqueued<TJob>(this IRecordedPulse pulse, TJob expected)
        where TJob : notnull
    {
        ArgumentNullException.ThrowIfNull(pulse);
        ArgumentNullException.ThrowIfNull(expected);
        var matches = JobsOf<TJob>(pulse).Where(job => Same(job, expected)).ToList();
        return One(matches, $"one job {expected}", pulse);
    }

    /// <summary>
    /// Checks that exactly one <typeparamref name="TJob"/> that matches a condition was enqueued, and returns it.
    /// </summary>
    /// <typeparam name="TJob">The job type.</typeparam>
    /// <param name="pulse">The fake or the test host.</param>
    /// <param name="where">The condition.</param>
    /// <param name="condition">Filled in by the compiler with the condition's code, for the failure message.</param>
    /// <returns>The job.</returns>
    /// <exception cref="PulseAssertionException">No such job was enqueued, or more than one.</exception>
    public static TJob Enqueued<TJob>(
        this IRecordedPulse pulse,
        Func<TJob, bool> where,
        [CallerArgumentExpression(nameof(where))] string? condition = null
    )
    {
        ArgumentNullException.ThrowIfNull(pulse);
        ArgumentNullException.ThrowIfNull(where);
        return One(JobsOf<TJob>(pulse).Where(where).ToList(), $"one {typeof(TJob).Name} job where {condition}", pulse);
    }

    /// <summary>
    /// Returns every <typeparamref name="TJob"/> that was enqueued, in order, without checking anything.
    /// </summary>
    /// <typeparam name="TJob">The job type.</typeparam>
    /// <param name="pulse">The fake or the test host.</param>
    /// <returns>The jobs.</returns>
    public static IReadOnlyList<TJob> AllEnqueued<TJob>(this IRecordedPulse pulse)
    {
        ArgumentNullException.ThrowIfNull(pulse);
        return JobsOf<TJob>(pulse);
    }

    /// <summary>
    /// Checks that no <typeparamref name="TJob"/> was enqueued.
    /// </summary>
    /// <typeparam name="TJob">The job type.</typeparam>
    /// <param name="pulse">The fake or the test host.</param>
    /// <exception cref="PulseAssertionException">One was enqueued.</exception>
    public static void NotEnqueued<TJob>(this IRecordedPulse pulse)
    {
        ArgumentNullException.ThrowIfNull(pulse);
        var found = JobsOf<TJob>(pulse);
        if (found.Count > 0)
        {
            throw Failure($"no {typeof(TJob).Name} job", found.Count, pulse);
        }
    }

    private static List<TEvent> EventsOf<TEvent>(IRecordedPulse pulse, StreamName? stream)
    {
        var streamName = stream?.Value();
        return pulse.Events
            .Where(emitted => emitted.Event is TEvent && (streamName is null || emitted.Stream == streamName))
            .Select(emitted => (TEvent)emitted.Event)
            .ToList();
    }

    private static List<TJob> JobsOf<TJob>(IRecordedPulse pulse)
    {
        return pulse.Jobs.Where(enqueued => enqueued.Job is TJob).Select(enqueued => (TJob)enqueued.Job).ToList();
    }

    private static T One<T>(IReadOnlyList<T> matches, string expected, IRecordedPulse pulse)
    {
        if (matches.Count != 1)
        {
            throw Failure(expected, matches.Count, pulse);
        }

        return matches[0];
    }

    private static PulseAssertionException Failure(string expected, int found, IRecordedPulse pulse)
    {
        var message = new StringBuilder()
            .Append("Expected ").Append(expected)
            .Append(", but found ").Append(found == 0 ? "none" : found.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('.');

        var events = pulse.Events;
        message.AppendLine().AppendLine();
        if (events.Count == 0)
        {
            message.Append("No events were emitted.");
        }
        else
        {
            message.Append("Emitted:");
            foreach (var emitted in events.Take(ShownItems))
            {
                message.AppendLine().Append("  ").Append(emitted.Event).Append(" in stream ").Append(emitted.Stream);
            }

            AppendMore(message, events.Count);
        }

        var jobs = pulse.Jobs;
        message.AppendLine();
        if (jobs.Count == 0)
        {
            message.Append("No jobs were enqueued.");
        }
        else
        {
            message.Append("Enqueued:");
            foreach (var enqueued in jobs.Take(ShownItems))
            {
                message.AppendLine().Append("  ").Append(enqueued.Job);
                if (enqueued.RunAt is { } runAt)
                {
                    message.Append(" to run at ").Append(runAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            AppendMore(message, jobs.Count);
        }

        return new PulseAssertionException(message.ToString());
    }

    private static void AppendMore(StringBuilder message, int count)
    {
        if (count > ShownItems)
        {
            message.AppendLine().Append("  ... and ").Append(count - ShownItems).Append(" more");
        }
    }

    private static bool Same(object actual, object expected)
    {
        if (actual.Equals(expected))
        {
            return true;
        }

        return actual.GetType() == expected.GetType()
            && JsonSerializer.Serialize(actual, actual.GetType()) == JsonSerializer.Serialize(expected, expected.GetType());
    }
}
