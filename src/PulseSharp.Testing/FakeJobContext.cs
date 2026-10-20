namespace PulseSharp.Testing;

/// <summary>
/// A <see cref="JobContext"/> for tests. It records the progress a job handler reports. Derive from it to change how
/// it behaves.
/// </summary>
public class FakeJobContext : JobContext
{
    private readonly object _gate = new();
    private readonly List<(int Done, int Total)> _progress = [];

    /// <summary>
    /// Creates the context.
    /// </summary>
    /// <param name="jobName">The job's name. The default is <c>test-job</c>.</param>
    /// <param name="attempt">The attempt number: 1 for the first try, 2 for the first retry. The default is 1.</param>
    public FakeJobContext(string jobName = "test-job", int attempt = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        JobName = jobName;
        Attempt = attempt;
    }

    /// <inheritdoc />
    public override Guid JobId { get; } = Guid.NewGuid();

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
