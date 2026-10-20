using System.Collections;
using System.Runtime.CompilerServices;

namespace PulseSharp.Testing;

/// <summary>
/// The jobs of one type that were enqueued, in the order they were enqueued. Check them with <see cref="ShouldHaveOne"/>,
/// <see cref="ShouldHaveNone"/> and <see cref="ShouldHave"/>, or use it as a list with any assertion library.
/// </summary>
/// <typeparam name="TJob">The job type.</typeparam>
public sealed class EnqueuedJobs<TJob> : IReadOnlyList<TJob>
{
    private readonly Recorded<TJob> _recorded;

    internal EnqueuedJobs(Recorded<TJob> recorded)
    {
        _recorded = recorded;
    }

    /// <summary>
    /// How many jobs there are.
    /// </summary>
    public int Count
    {
        get
        {
            return _recorded.Items.Count;
        }
    }

    /// <summary>
    /// The job at a position.
    /// </summary>
    /// <param name="index">The position, starting at 0.</param>
    /// <returns>The job.</returns>
    public TJob this[int index]
    {
        get
        {
            return _recorded.Items[index].Value;
        }
    }

    /// <summary>
    /// Checks that exactly one job was enqueued, or exactly one that matches a condition, and returns it.
    /// </summary>
    /// <param name="where">The condition, or <see langword="null"/> for any job of this type.</param>
    /// <param name="condition">Filled in by the compiler with the condition's code, for the failure message.</param>
    /// <returns>The job.</returns>
    /// <exception cref="PulseAssertionException">There is no such job, or more than one.</exception>
    /// <example>
    /// <code>
    /// pulse.Enqueued&lt;SendReceipt&gt;().ShouldHaveOne(receipt => receipt.SaleId == saleId);
    /// </code>
    /// </example>
    public TJob ShouldHaveOne(Func<TJob, bool>? where = null, [CallerArgumentExpression(nameof(where))] string? condition = null)
    {
        return _recorded.One(where, condition);
    }

    /// <summary>
    /// Checks that no job was enqueued, or none that matches a condition.
    /// </summary>
    /// <param name="where">The condition, or <see langword="null"/> for any job of this type.</param>
    /// <param name="condition">Filled in by the compiler with the condition's code, for the failure message.</param>
    /// <exception cref="PulseAssertionException">There is such a job.</exception>
    public void ShouldHaveNone(Func<TJob, bool>? where = null, [CallerArgumentExpression(nameof(where))] string? condition = null)
    {
        _recorded.None(where, condition);
    }

    /// <summary>
    /// Checks how many jobs were enqueued, or how many match a condition, and returns them.
    /// </summary>
    /// <param name="count">The expected number.</param>
    /// <param name="where">The condition, or <see langword="null"/> for any job of this type.</param>
    /// <param name="condition">Filled in by the compiler with the condition's code, for the failure message.</param>
    /// <returns>The matching jobs.</returns>
    /// <exception cref="PulseAssertionException">The number is different.</exception>
    public IReadOnlyList<TJob> ShouldHave(int count, Func<TJob, bool>? where = null, [CallerArgumentExpression(nameof(where))] string? condition = null)
    {
        return _recorded.Exactly(count, where, condition);
    }

    /// <summary>
    /// Returns the jobs in order.
    /// </summary>
    /// <returns>An enumerator.</returns>
    public IEnumerator<TJob> GetEnumerator()
    {
        return _recorded.Items.Select(item => item.Value).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
