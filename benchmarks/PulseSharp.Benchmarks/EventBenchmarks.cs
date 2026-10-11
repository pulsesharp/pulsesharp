using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PulseSharp.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class EventBenchmarks
{
    private const int EventCount = 1_000;
    private const int StreamCount = 10;

    [Benchmark(Description = "Emit (in-memory, no handlers)", OperationsPerInvoke = EventCount)]
    public async Task Emit()
    {
        await using var services = new ServiceCollection()
            .AddPulseSharp(pulse => pulse.UseInMemory())
            .BuildServiceProvider();
        var pulse = services.GetRequiredService<IPulse>();

        for (var number = 0; number < EventCount; number++)
        {
            await pulse.Emit(new BenchmarkStream(number % StreamCount), new BenchmarkEvent(number));
        }
    }

    [Benchmark(Description = "Emit + deliver to 1 handler", OperationsPerInvoke = EventCount)]
    public Task EmitAndDeliverToOneHandler()
    {
        return EmitAndDeliver(handlerCount: 1);
    }

    [Benchmark(Description = "Emit + deliver to 3 handlers", OperationsPerInvoke = EventCount)]
    public Task EmitAndDeliverToThreeHandlers()
    {
        return EmitAndDeliver(handlerCount: 3);
    }

    private static async Task EmitAndDeliver(int handlerCount)
    {
        var expected = EventCount * handlerCount;
        var delivered = 0;
        var allDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddPulseSharp(pulse =>
        {
            pulse.UseInMemory();
            for (var handler = 0; handler < handlerCount; handler++)
            {
                pulse.On<BenchmarkEvent>($"handler-{handler}", (_, _, _) =>
                {
                    if (Interlocked.Increment(ref delivered) == expected)
                    {
                        allDelivered.TrySetResult();
                    }

                    return Task.CompletedTask;
                });
            }
        });

        using var host = builder.Build();
        await host.StartAsync();
        var pulse = host.Services.GetRequiredService<IPulse>();

        for (var number = 0; number < EventCount; number++)
        {
            await pulse.Emit(new BenchmarkStream(number % StreamCount), new BenchmarkEvent(number));
        }

        await allDelivered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await host.StopAsync();
    }
}

public sealed record BenchmarkEvent(int Number);

public sealed record BenchmarkStream(int Id) : StreamName
{
    public override string Value()
    {
        return $"Bench-{Id}";
    }
}
