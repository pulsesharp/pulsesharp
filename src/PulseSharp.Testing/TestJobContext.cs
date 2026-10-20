using System.Reflection;

namespace PulseSharp.Testing;

/// <summary>
/// A <see cref="JobContext"/> for calling a job handler class directly in a test. It records the progress the
/// handler reports.
/// </summary>
/// <example>
/// <code>
/// var context = TestJobContext.For(job);
/// await handler.Handle(job, context, CancellationToken.None);
/// Assert.Equal((3, 3), context.LastProgress);
/// </code>
/// </example>
public sealed class TestJobContext : JobContext
{
    private readonly object _gate = new();
    private readonly List<(int Done, int Total)> _progress = [];

    private TestJobContext(Guid jobId, string jobName, int attempt)
    {
        JobId = jobId;
        JobName = jobName;
        Attempt = attempt;
    }

    /// <inheritdoc />
    public override Guid JobId { get; }

    /// <inheritdoc />
    public override string JobName { get; }

    /// <inheritdoc />
    public override int Attempt { get; }

    /// <summary>
    /// Every progress report, in order.
    /// </summary>
    public IReadOnlyList<(int Done, int Total)> Progress
    {
        get
        {
            lock (_gate)
            {
                return _progress.ToList();
            }
        }
    }

    /// <summary>
    /// The last progress report, or <see langword="null"/> when the handler reported none.
    /// </summary>
    public (int Done, int Total)? LastProgress
    {
        get
        {
            lock (_gate)
            {
                return _progress.Count == 0 ? null : _progress[^1];
            }
        }
    }

    /// <summary>
    /// Creates a context for a job.
    /// </summary>
    /// <typeparam name="TJob">The job type.</typeparam>
    /// <param name="job">The job the handler receives.</param>
    /// <param name="attempt">The attempt number: 1 for the first try, 2 for the first retry. The default is 1.</param>
    /// <returns>The context.</returns>
    public static TestJobContext For<TJob>(TJob job, int attempt = 1)
        where TJob : notnull
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);

        var type = job.GetType();
        return new TestJobContext(Guid.NewGuid(), type.GetCustomAttribute<JobNameAttribute>()?.Name ?? type.Name, attempt);
    }

    /// <summary>
    /// Records the progress.
    /// </summary>
    /// <param name="done">Items finished.</param>
    /// <param name="total">Items in total.</param>
    /// <param name="cancellationToken">Not used.</param>
    /// <returns>A completed task.</returns>
    public override Task Report(int done, int total, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _progress.Add((done, total));
        }

        return Task.CompletedTask;
    }
}
