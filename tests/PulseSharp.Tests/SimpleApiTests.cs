using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PulseSharp.Sqlite;
using PulseSharp.Stores;

namespace PulseSharp.Tests;

public sealed class SimpleApiTests
{
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
    public async Task UseSqliteAcceptsAFileNameOrAConnectionString()
    {
        using var first = new TempDatabase();
        using var second = new TempDatabase();
        var byFileName = new SqliteStore(first.FilePath);
        var byConnectionString = new SqliteStore(second.ConnectionString);
        var newEvent = new NewEvent(Guid.NewGuid(), "Sale-1", "SaleCompleted", "{}"u8.ToArray(), "till-1", DateTimeOffset.UtcNow);

        await byFileName.Append(newEvent, ExpectedVersion.Any, null, CancellationToken.None);
        await byConnectionString.Append(newEvent with { EventId = Guid.NewGuid() }, ExpectedVersion.Any, null, CancellationToken.None);

        Assert.True(File.Exists(first.FilePath));
        Assert.Single(await byFileName.ReadStream("Sale-1", 0, 10, CancellationToken.None));
        Assert.Single(await byConnectionString.ReadStream("Sale-1", 0, 10, CancellationToken.None));
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task RunsOnSqliteInAnAppHost()
    {
        using var database = new TempDatabase();
        var handled = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddPulseSharp(pulse =>
        {
            pulse.UseSqlite(database.FilePath);
            pulse.On<SaleCompleted>("receipts", (sale, _, _) =>
            {
                handled.TrySetResult(sale.Number);
                return Task.CompletedTask;
            });
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
