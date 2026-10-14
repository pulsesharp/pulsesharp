using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PulseSharp.Tests;

public sealed class SimpleApiTests
{
    [Fact]
    public async Task WhenRunsForEveryEventEmittedWithoutAStream()
    {
        var seen = new ConcurrentQueue<int>();
        await using var host = await TestHost.Start(pulse => pulse.When<SaleCompleted>(sale => seen.Enqueue(sale.Number)));

        var first = await host.Pulse.Emit(new SaleCompleted(1));
        var second = await host.Pulse.Emit(new SaleCompleted(2));

        await Eventually.True(() => seen.Count == 2, "both events are handled");
        Assert.Equal([1, 2], seen);
        Assert.Equal([1L, 2L], [first.StreamPosition, second.StreamPosition]);
    }

    [Fact]
    public async Task WhenAcceptsAsyncCode()
    {
        var handled = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await TestHost.Start(pulse => pulse.When<SaleCompleted>(async sale =>
        {
            await Task.Yield();
            handled.TrySetResult(sale.Number);
        }));

        await host.Pulse.Emit(new SaleCompleted(7));

        Assert.Equal(7, await handled.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void RefusesASecondWhenForTheSameEvent()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddPulseSharp(pulse =>
        {
            pulse.UseInMemory();
            pulse.When<SaleCompleted>(_ => { });
            pulse.When<SaleCompleted>(_ => { });
        }));

        Assert.Contains("give it a name", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UsesTheEventNameOrAPlainStringAsTheStream()
    {
        var streams = new ConcurrentQueue<string>();
        await using var host = await TestHost.Start(pulse =>
        {
            pulse.On<SaleVoided>("voids", (_, context, _) =>
            {
                streams.Enqueue(context.Stream);
                return Task.CompletedTask;
            });
        });

        await host.Pulse.Emit(new SaleVoided(1));
        await host.Pulse.Emit("Sale-42", new SaleVoided(2));

        await Eventually.True(() => streams.Count == 2, "both events are handled");
        Assert.Equal(["Sale-42", "sale-voided"], streams.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task RunsAJobWithTheShortForm()
    {
        var ran = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await TestHost.Start(pulse => pulse.OnJob<ImportProducts>(async job =>
        {
            await Task.Yield();
            ran.TrySetResult(job.File);
        }));

        await host.Pulse.Enqueue(new ImportProducts("prices.csv"));

        Assert.Equal("prices.csv", await ran.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task EnqueuesJobsForLater()
    {
        await using var host = await TestHost.Start(pulse => pulse.OnJob<ImportProducts>((_, _, _) => Task.CompletedTask));
        var before = DateTimeOffset.UtcNow;
        var at = before.AddDays(1);

        var inAnHour = await host.Pulse.EnqueueIn(new ImportProducts("a.csv"), TimeSpan.FromHours(1));
        var tomorrow = await host.Pulse.EnqueueAt(new ImportProducts("b.csv"), at);

        var first = (await host.Control.GetJob(inAnHour))!;
        Assert.InRange(first.RunAt, before.AddHours(1), DateTimeOffset.UtcNow.AddHours(1));
        Assert.Equal(JobState.Pending, first.State);
        Assert.Equal(at, (await host.Control.GetJob(tomorrow))!.RunAt);
    }

    [Fact]
    public async Task SetsUpSqliteInOneLine()
    {
        using var database = new TempDatabase();
        var handled = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddPulseSharpSqlite(database.FilePath, pulse =>
        {
            pulse.When<SaleCompleted>(sale => handled.TrySetResult(sale.Number));
        });

        using (var host = builder.Build())
        {
            await host.StartAsync();
            await host.Services.GetRequiredService<IPulse>().Emit(new SaleCompleted(3));
            Assert.Equal(3, await handled.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            await host.StopAsync();
        }

        SqliteConnection.ClearAllPools();
    }
}
