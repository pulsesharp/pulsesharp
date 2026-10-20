using System.Collections;
using System.Runtime.CompilerServices;

namespace PulseSharp.Testing;

/// <summary>
/// The events of one type that were emitted, in the order they were emitted. Check them with <see cref="ShouldHaveOne"/>,
/// <see cref="ShouldHaveNone"/> and <see cref="ShouldHave"/>, or use it as a list with any assertion library.
/// </summary>
/// <typeparam name="TEvent">The event type.</typeparam>
public sealed class EmittedEvents<TEvent> : IReadOnlyList<TEvent>
{
    private readonly Recorded<TEvent> _recorded;

    internal EmittedEvents(Recorded<TEvent> recorded)
    {
        _recorded = recorded;
    }

    /// <summary>
    /// How many events there are.
    /// </summary>
    public int Count
    {
        get
        {
            return _recorded.Items.Count;
        }
    }

    /// <summary>
    /// The event at a position.
    /// </summary>
    /// <param name="index">The position, starting at 0.</param>
    /// <returns>The event.</returns>
    public TEvent this[int index]
    {
        get
        {
            return _recorded.Items[index].Value;
        }
    }

    /// <summary>
    /// Only the events emitted to one stream.
    /// </summary>
    /// <param name="stream">The stream name, for example <c>Sale-1</c>.</param>
    /// <returns>The events of that stream.</returns>
    public EmittedEvents<TEvent> InStream(string stream)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stream);
        return new EmittedEvents<TEvent>(_recorded.Narrowed(item => item.Where == StreamText(stream), StreamText(stream)));
    }

    /// <summary>
    /// Checks that exactly one event was emitted, or exactly one that matches a condition, and returns it.
    /// </summary>
    /// <param name="where">The condition, or <see langword="null"/> for any event of this type.</param>
    /// <param name="condition">Filled in by the compiler with the condition's code, for the failure message.</param>
    /// <returns>The event.</returns>
    /// <exception cref="PulseAssertionException">There is no such event, or more than one.</exception>
    /// <example>
    /// <code>
    /// pulse.Emitted&lt;SaleCompleted&gt;().ShouldHaveOne(sale => sale.Total == 25m);
    /// </code>
    /// </example>
    public TEvent ShouldHaveOne(Func<TEvent, bool>? where = null, [CallerArgumentExpression(nameof(where))] string? condition = null)
    {
        return _recorded.One(where, condition);
    }

    /// <summary>
    /// Checks that no event was emitted, or none that matches a condition.
    /// </summary>
    /// <param name="where">The condition, or <see langword="null"/> for any event of this type.</param>
    /// <param name="condition">Filled in by the compiler with the condition's code, for the failure message.</param>
    /// <exception cref="PulseAssertionException">There is such an event.</exception>
    public void ShouldHaveNone(Func<TEvent, bool>? where = null, [CallerArgumentExpression(nameof(where))] string? condition = null)
    {
        _recorded.None(where, condition);
    }

    /// <summary>
    /// Checks how many events were emitted, or how many match a condition, and returns them.
    /// </summary>
    /// <param name="count">The expected number.</param>
    /// <param name="where">The condition, or <see langword="null"/> for any event of this type.</param>
    /// <param name="condition">Filled in by the compiler with the condition's code, for the failure message.</param>
    /// <returns>The matching events.</returns>
    /// <exception cref="PulseAssertionException">The number is different.</exception>
    public IReadOnlyList<TEvent> ShouldHave(int count, Func<TEvent, bool>? where = null, [CallerArgumentExpression(nameof(where))] string? condition = null)
    {
        return _recorded.Exactly(count, where, condition);
    }

    /// <summary>
    /// Returns the events in order.
    /// </summary>
    /// <returns>An enumerator.</returns>
    public IEnumerator<TEvent> GetEnumerator()
    {
        return _recorded.Items.Select(item => item.Value).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    internal static string StreamText(string stream)
    {
        return $" in stream {stream}";
    }
}
