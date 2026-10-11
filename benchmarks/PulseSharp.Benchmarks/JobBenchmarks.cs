using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PulseSharp.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class JobBenchmarks
{
    private const int JobCount = 500;

    [Benchmark(Description = "Enqueue + run a job", OperationsPerInvoke = JobCount)]
    public async Task EnqueueAndRun()
    {
        var finished = 0;
        var allFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddPulseSharp(pulse =>
        {
            pulse.UseInMemory();
            pulse.OnJob<BenchmarkJob>((_, _, _) =>
            {
                if (Interlocked.Increment(ref finished) == JobCount)
                {
                    allFinished.TrySetResult();
                }

                return Task.CompletedTask;
            });
        });

        using var host = builder.Build();
        await host.StartAsync();
        var pulse = host.Services.GetRequiredService<IPulse>();

        for (var number = 0; number < JobCount; number++)
        {
            await pulse.Enqueue(new BenchmarkJob(number));
        }

        await allFinished.Task.WaitAsync(TimeSpan.FromSeconds(60));
        await host.StopAsync();
    }
}

public sealed record BenchmarkJob(int Number);
