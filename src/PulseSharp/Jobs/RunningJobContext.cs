using PulseSharp.Stores;

namespace PulseSharp.Jobs;

internal sealed class RunningJobContext(StoredJob job, IPulseStore store, string workerId) : JobContext
{
    public override Guid JobId
    {
        get
        {
            return job.Id;
        }
    }

    public override string JobName
    {
        get
        {
            return job.JobName;
        }
    }

    public override int Attempt
    {
        get
        {
            return job.Attempts;
        }
    }

    public override Task Report(int done, int total, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(done);
        ArgumentOutOfRangeException.ThrowIfNegative(total);
        return store.SaveProgress(job.Id, workerId, done, total, cancellationToken);
    }
}
