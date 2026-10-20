using Microsoft.Extensions.Logging;
using PulseSharp.Delivery;
using PulseSharp.Stores;

namespace PulseSharp.Jobs;

internal sealed class JobRunner(
    IPulseStore store,
    WakeUpSignal signal,
    PulseSharpSettings settings,
    IServiceProvider services,
    TimeProvider time,
    ILogger logger
)
{
    private readonly string _workerId = $"{settings.Node}:{Guid.NewGuid():N}";
    private readonly IReadOnlySet<string> _jobNames = settings.Jobs.Keys.ToHashSet(StringComparer.Ordinal);
    private static readonly TimeSpan CleanUpEvery = TimeSpan.FromHours(1);
    private const int CleanUpBatchSize = 1000;

    private readonly HashSet<Task> _running = [];
    private DateTimeOffset _nextCleanUp = DateTimeOffset.MinValue;

    public async Task Run(CancellationToken stoppingToken)
    {
        await Task.Yield();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var wakeUp = signal.Next;
                await StartDueJobs(stoppingToken).ConfigureAwait(false);
                await CleanUpFinishedJobs(stoppingToken).ConfigureAwait(false);
                await WaitForWork(wakeUp, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await Task.WhenAll(_running).ConfigureAwait(false);
        }
    }

    private async Task StartDueJobs(CancellationToken stoppingToken)
    {
        _running.RemoveWhere(task => task.IsCompleted);

        while (_running.Count < settings.MaxParallelJobs)
        {
            var job = await TryClaimNextJob(stoppingToken).ConfigureAwait(false);
            if (job is null)
            {
                return;
            }

            _running.Add(RunJob(job, stoppingToken));
        }
    }

    private async Task CleanUpFinishedJobs(CancellationToken stoppingToken)
    {
        var now = time.GetUtcNow();
        if (settings.KeepFinishedJobs is not { } keep || now < _nextCleanUp)
        {
            return;
        }

        _nextCleanUp = now + (keep < CleanUpEvery ? keep : CleanUpEvery);
        try
        {
            var deleted = 0;
            int batch;
            do
            {
                batch = await store.DeleteFinishedJobs(now - keep, CleanUpBatchSize, stoppingToken).ConfigureAwait(false);
                deleted += batch;
            }
            while (batch == CleanUpBatchSize);

            if (deleted > 0)
            {
                Log.FinishedJobsDeleted(logger, deleted);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Log.JobRunnerError(logger, exception);
        }
    }

    private async Task<StoredJob?> TryClaimNextJob(CancellationToken stoppingToken)
    {
        try
        {
            return await store
                .ClaimNextJob(_jobNames, _workerId, time.GetUtcNow(), settings.JobLease, stoppingToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Log.JobRunnerError(logger, exception);
            return null;
        }
    }

    private async Task RunJob(StoredJob job, CancellationToken stoppingToken)
    {
        await Task.Yield();

        try
        {
            await ExecuteJob(job, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.JobRunnerError(logger, exception);
        }
    }

    private async Task ExecuteJob(StoredJob job, CancellationToken stoppingToken)
    {
        using var jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var heartbeatStop = new CancellationTokenSource();
        var heartbeat = KeepLease(job.JobId, jobCancellation, heartbeatStop.Token);

        try
        {
            var context = new RunningJobContext(job, store, _workerId);
            await settings.Jobs[job.JobName].Invoke(services, job.Data, context, jobCancellation.Token).ConfigureAwait(false);
            await store.CompleteJob(job.JobId, _workerId, time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await store.ReleaseJob(job.JobId, _workerId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (jobCancellation.IsCancellationRequested)
        {
            await store.CancelRunningJob(job.JobId, _workerId, time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await RecordFailure(job, exception).ConfigureAwait(false);
        }
        finally
        {
            await heartbeatStop.CancelAsync().ConfigureAwait(false);
            await heartbeat.ConfigureAwait(false);
        }
    }

    private async Task KeepLease(Guid jobId, CancellationTokenSource jobCancellation, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(settings.PollInterval, time, stop).ConfigureAwait(false);
                var leaseUntil = time.GetUtcNow() + settings.JobLease;
                var lease = await store.RenewLease(jobId, _workerId, leaseUntil, stop).ConfigureAwait(false);
                if (lease.CancelRequested || !lease.Held)
                {
                    await jobCancellation.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return;
        }
    }

    private async Task RecordFailure(StoredJob job, Exception failure)
    {
        var now = time.GetUtcNow();
        if (job.Attempts >= settings.MaxAttempts)
        {
            Log.JobFailed(logger, failure, job.JobName, job.JobId, job.Attempts);
            await store.FailJobAttempt(job.JobId, _workerId, failure.ToString(), null, now, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var retryAt = now + settings.RetryDelayFor(job.Attempts);
        Log.JobWillRetry(logger, failure, job.JobName, job.JobId, job.Attempts, settings.MaxAttempts, retryAt);
        await store.FailJobAttempt(job.JobId, _workerId, failure.ToString(), retryAt, now, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task WaitForWork(Task wakeUp, CancellationToken stoppingToken)
    {
        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var delay = Task.Delay(settings.PollInterval, time, delayCancellation.Token);
        var slotFreed = _running.Count > 0 ? Task.WhenAny(_running) : Task.Delay(Timeout.Infinite, delayCancellation.Token);

        await Task.WhenAny(wakeUp, delay, slotFreed).ConfigureAwait(false);
        await delayCancellation.CancelAsync().ConfigureAwait(false);
        stoppingToken.ThrowIfCancellationRequested();
    }
}
