using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace PulseSharp.Tests;

public sealed record ImportProducts(string File);

[JobName("rebuild-index")]
public sealed record RebuildIndex(int Shard);

public sealed class JobTests
{
    [Fact]
    public async Task RunsAnEnqueuedJob()
    {
        JobContext? received = null;
        string? file = null;
        await using var host = await TestHost.Start(pulse =>
        {
            pulse.OnJob<ImportProducts>((job, context, _) =>
            {
                file = job.File;
                received = context;
                return Task.CompletedTask;
            });
        });

        var id = await host.Pulse.Enqueue(new ImportProducts("products.csv"));

        var job = await WaitForJob(host, id, JobState.Succeeded);
        Assert.Equal("products.csv", file);
        Assert.Equal(id, received!.JobId);
        Assert.Equal("ImportProducts", received.JobName);
        Assert.Equal(1, received.Attempt);
        Assert.Equal(1, job.Attempts);
        Assert.NotNull(job.StartedAt);
        Assert.NotNull(job.FinishedAt);
    }

    [Fact]
    public async Task RunsAJobHandlerClassFromDependencyInjection()
    {
        var recorder = new JobRecorder();
        await using var host = await TestHost.Start(
            pulse => pulse.AddJobHandler<RebuildIndex, RebuildIndexHandler>(),
            services => services.AddSingleton(recorder)
        );

        var id = await host.Pulse.Enqueue(new RebuildIndex(7));

        await WaitForJob(host, id, JobState.Succeeded);
        Assert.Equal(["rebuild-index:7"], recorder.Items);
    }

    [Fact]
    public async Task WaitsUntilRunAt()
    {
        DateTimeOffset? ranAt = null;
        await using var host = await TestHost.Start(pulse =>
        {
            pulse.OnJob<ImportProducts>((_, _, _) =>
            {
                ranAt = DateTimeOffset.UtcNow;
                return Task.CompletedTask;
            });
        });

        var runAt = DateTimeOffset.UtcNow.AddMilliseconds(400);
        var id = await host.Pulse.Enqueue(new ImportProducts("later.csv"), new EnqueueOptions { RunAt = runAt });

        await Task.Delay(150);
        Assert.Null(ranAt);
        Assert.Equal(JobState.Pending, (await host.Control.GetJob(id))!.State);

        await WaitForJob(host, id, JobState.Succeeded);
        Assert.True(ranAt >= runAt, $"Ran at {ranAt:O}, before {runAt:O}.");
    }

    [Fact]
    public async Task RetriesAFailingJobUntilItSucceeds()
    {
        var attempts = new ConcurrentQueue<int>();
        await using var host = await TestHost.Start(pulse =>
        {
            pulse.OnJob<ImportProducts>((_, context, _) =>
            {
                attempts.Enqueue(context.Attempt);
                if (context.Attempt < 3)
                {
                    throw new InvalidOperationException("Not yet.");
                }

                return Task.CompletedTask;
            });
        });

        var id = await host.Pulse.Enqueue(new ImportProducts("flaky.csv"));

        var job = await WaitForJob(host, id, JobState.Succeeded);
        Assert.Equal([1, 2, 3], attempts);
        Assert.Equal(3, job.Attempts);
    }

    [Fact]
    public async Task FailsAfterMaxAttemptsAndCanBeRetried()
    {
        var fixedIt = false;
        await using var host = await TestHost.Start(pulse =>
        {
            pulse.MaxAttempts = 2;
            pulse.OnJob<ImportProducts>((_, _, _) =>
            {
                if (!Volatile.Read(ref fixedIt))
                {
                    throw new InvalidOperationException("Bad file.");
                }

                return Task.CompletedTask;
            });
        });

        var id = await host.Pulse.Enqueue(new ImportProducts("bad.csv"));

        var failed = await WaitForJob(host, id, JobState.Failed);
        Assert.Equal(2, failed.Attempts);
        Assert.Contains("Bad file.", failed.LastError, StringComparison.Ordinal);

        Volatile.Write(ref fixedIt, true);
        Assert.True(await host.Control.RetryJob(id));

        var succeeded = await WaitForJob(host, id, JobState.Succeeded);
        Assert.Equal(1, succeeded.Attempts);
        Assert.Null(succeeded.LastError);
    }

    [Fact]
    public async Task SavesReportedProgress()
    {
        var reported = new TaskCompletionSource();
        var finish = new TaskCompletionSource();
        await using var host = await TestHost.Start(pulse =>
        {
            pulse.OnJob<ImportProducts>(async (_, context, ct) =>
            {
                await context.Report(5, 10, ct);
                reported.TrySetResult();
                await finish.Task.WaitAsync(ct);
            });
        });

        var id = await host.Pulse.Enqueue(new ImportProducts("big.csv"));
        await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var running = (await host.Control.GetJob(id))!;
        Assert.Equal(JobState.Running, running.State);
        Assert.Equal(5, running.Done);
        Assert.Equal(10, running.Total);

        finish.TrySetResult();
        await WaitForJob(host, id, JobState.Succeeded);
    }

    [Fact]
    public async Task CancelsARunningJob()
    {
        var started = new TaskCompletionSource();
        var sawCancellation = false;
        await using var host = await TestHost.Start(pulse =>
        {
            pulse.OnJob<ImportProducts>(async (_, _, ct) =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    sawCancellation = true;
                    throw;
                }
            });
        });

        var id = await host.Pulse.Enqueue(new ImportProducts("endless.csv"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(await host.Control.CancelJob(id));

        await WaitForJob(host, id, JobState.Canceled);
        Assert.True(sawCancellation);
    }

    [Fact]
    public async Task CancelsAWaitingJobBeforeItRuns()
    {
        var ran = false;
        await using var host = await TestHost.Start(pulse =>
        {
            pulse.OnJob<ImportProducts>((_, _, _) =>
            {
                ran = true;
                return Task.CompletedTask;
            });
        });

        var id = await host.Pulse.Enqueue(new ImportProducts("later.csv"), new EnqueueOptions { RunAt = DateTimeOffset.UtcNow.AddMilliseconds(300) });

        Assert.True(await host.Control.CancelJob(id));
        await Task.Delay(500);

        Assert.Equal(JobState.Canceled, (await host.Control.GetJob(id))!.State);
        Assert.False(ran);
    }

    [Fact]
    public async Task CancelAndRetryReturnFalseWhenThereIsNothingToDo()
    {
        await using var host = await TestHost.Start(pulse => pulse.OnJob<ImportProducts>((_, _, _) => Task.CompletedTask));
        var id = await host.Pulse.Enqueue(new ImportProducts("quick.csv"));
        await WaitForJob(host, id, JobState.Succeeded);

        Assert.False(await host.Control.CancelJob(id));
        Assert.False(await host.Control.RetryJob(id));
        Assert.False(await host.Control.CancelJob(Guid.NewGuid()));
        Assert.Null(await host.Control.GetJob(Guid.NewGuid()));
    }

    [Fact]
    public async Task RunsJobsInParallelUpToTheLimit()
    {
        var running = 0;
        var highest = 0;
        var finished = 0;
        await using var host = await TestHost.Start(pulse =>
        {
            pulse.MaxParallelJobs = 2;
            pulse.OnJob<ImportProducts>(async (_, _, ct) =>
            {
                var now = Interlocked.Increment(ref running);
                InterlockedMax(ref highest, now);
                await Task.Delay(80, ct);
                Interlocked.Decrement(ref running);
                Interlocked.Increment(ref finished);
            });
        });

        for (var number = 0; number < 6; number++)
        {
            await host.Pulse.Enqueue(new ImportProducts($"{number}.csv"));
        }

        await Eventually.True(() => Volatile.Read(ref finished) == 6, "all six jobs finish");
        Assert.Equal(2, Volatile.Read(ref highest));
    }

    [Fact]
    public async Task ListsJobsByStateNewestFirst()
    {
        await using var host = await TestHost.Start(pulse => pulse.OnJob<ImportProducts>((_, _, _) => Task.CompletedTask));

        var first = await host.Pulse.Enqueue(new ImportProducts("1.csv"));
        var second = await host.Pulse.Enqueue(new ImportProducts("2.csv"));
        await WaitForJob(host, first, JobState.Succeeded);
        await WaitForJob(host, second, JobState.Succeeded);

        var succeeded = await host.Control.GetJobs(JobState.Succeeded);
        Assert.Equal([second, first], succeeded.Select(job => job.Id));
        Assert.Empty(await host.Control.GetJobs(JobState.Failed));
    }

    [Fact]
    public async Task KeepsAJobWithoutAHandlerPending()
    {
        await using var host = await TestHost.Start(_ => { });

        var id = await host.Pulse.Enqueue(new ImportProducts("nobody.csv"));
        await Task.Delay(200);

        Assert.Equal(JobState.Pending, (await host.Control.GetJob(id))!.State);
    }

    private static async Task<JobInfo> WaitForJob(TestHost host, Guid id, JobState state)
    {
        JobInfo? job = null;
        await Eventually.True(
            () =>
            {
                job = host.Control.GetJob(id).Result;
                return job?.State == state;
            },
            $"job {id} is {state} (last seen: {job?.State})"
        );
        return job!;
    }

    private static void InterlockedMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var previous = Interlocked.CompareExchange(ref target, value, current);
            if (previous == current)
            {
                return;
            }

            current = previous;
        }
    }

    private sealed class JobRecorder
    {
        public ConcurrentQueue<string> Items { get; } = new();
    }

    private sealed class RebuildIndexHandler(JobRecorder recorder) : IJobHandler<RebuildIndex>
    {
        public Task Handle(RebuildIndex job, JobContext context, CancellationToken cancellationToken)
        {
            recorder.Items.Enqueue($"{context.JobName}:{job.Shard}");
            return Task.CompletedTask;
        }
    }
}
