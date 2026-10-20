namespace PulseSharp.Testing;

/// <summary>
/// A job that was enqueued, as a fake or the test host recorded it.
/// </summary>
/// <param name="Job">The job.</param>
/// <param name="RunAt">The earliest time it may run, or <see langword="null"/> for as soon as possible.</param>
/// <param name="Options">The options it was enqueued with.</param>
public sealed record EnqueuedJob(object Job, DateTimeOffset? RunAt, EnqueueOptions Options);
