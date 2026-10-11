namespace PulseSharp;

/// <summary>
/// The state of a job.
/// </summary>
public enum JobState
{
    /// <summary>Waiting to run, now or at <see cref="JobInfo.RunAt"/>.</summary>
    Pending = 0,

    /// <summary>A worker is running the job.</summary>
    Running = 1,

    /// <summary>The last attempt failed. The job runs again at <see cref="JobInfo.RunAt"/>.</summary>
    Retrying = 2,

    /// <summary>The job finished successfully.</summary>
    Succeeded = 3,

    /// <summary>The job failed on every attempt. Use <see cref="IPulseControl.RetryJob"/> to run it again.</summary>
    Failed = 4,

    /// <summary>The job was cancelled.</summary>
    Canceled = 5
}
