using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using PulseSharp.Testing;

namespace PulseSharp.Tests;

public sealed class TestingPackageTests
{
    [Fact]
    public async Task FakePulseRecordsWhatTheCodeEmitsAndEnqueues()
    {
        var pulse = new FakePulse();
        var checkout = new Checkout(pulse);

        await checkout.Complete(7, 250);

        var completed = pulse.Emitted<SaleCompleted>("Sale-7");
        Assert.Equal(7, completed.Number);
        pulse.Emitted(new SaleRefunded(7));
        pulse.Emitted<SaleRefunded>(refund => refund.Number == 7);
        pulse.NotEmitted<SaleVoided>();
        pulse.Enqueued(new ImportProducts("refund-7.csv"));
        pulse.NotEnqueued<RebuildIndex>();
        Assert.Equal(2, pulse.Events.Count);
        Assert.Equal("Sale-7", pulse.Events[0].Stream);
    }

    [Fact]
    public async Task AllEmittedReturnsEveryMatchWithoutChecking()
    {
        var pulse = new FakePulse();

        await pulse.Emit("Sale-1", new SaleCompleted(1));
        await pulse.Emit("Sale-2", new SaleCompleted(2));
        await pulse.EnqueueIn(new ImportProducts("a.csv"), TimeSpan.FromHours(1));

        Assert.Equal([1, 2], pulse.AllEmitted<SaleCompleted>().Select(sale => sale.Number));
        Assert.Equal([2], pulse.AllEmitted<SaleCompleted>("Sale-2").Select(sale => sale.Number));
        Assert.Single(pulse.AllEnqueued<ImportProducts>());
        Assert.NotNull(pulse.Jobs[0].RunAt);
        Assert.Throws<PulseAssertionException>(() => pulse.Emitted<SaleCompleted>());
    }

    [Fact]
    public async Task FakePulseChecksExpectedVersionsAndCanFail()
    {
        var pulse = new FakePulse();
        await pulse.Emit("Order-1", new SaleCompleted(1), new EmitOptions { ExpectedVersion = ExpectedVersion.NoStream });

        await Assert.ThrowsAsync<ExpectedVersionMismatchException>(
            () => pulse.Emit("Order-1", new SaleCompleted(2), new EmitOptions { ExpectedVersion = ExpectedVersion.NoStream })
        );

        pulse.ThrowOnEmit = new InvalidOperationException("The database is down.");
        await Assert.ThrowsAsync<InvalidOperationException>(() => pulse.Emit(new SaleCompleted(3)));
        pulse.Clear();
        Assert.Empty(pulse.Events);
    }

    [Fact]
    public async Task FailureMessagesShowWhatWasExpectedAndWhatHappened()
    {
        var pulse = new FakePulse();
        await pulse.Emit("Sale-1", new SaleCompleted(20));
        await pulse.Enqueue(new ImportProducts("a.csv"));

        var byCondition = Assert.Throws<PulseAssertionException>(() => pulse.Emitted<SaleCompleted>(sale => sale.Number == 25));
        var byValue = Assert.Throws<PulseAssertionException>(() => pulse.Emitted(new SaleCompleted(25)));
        var unexpected = Assert.Throws<PulseAssertionException>(() => pulse.NotEnqueued<ImportProducts>());

        Assert.Contains("Expected one SaleCompleted where sale => sale.Number == 25, but found none.", byCondition.Message, StringComparison.Ordinal);
        Assert.Contains("SaleCompleted { Number = 20 } in stream Sale-1", byCondition.Message, StringComparison.Ordinal);
        Assert.Contains("ImportProducts { File = a.csv }", byCondition.Message, StringComparison.Ordinal);
        Assert.Contains("Expected one SaleCompleted { Number = 25 }, but found none.", byValue.Message, StringComparison.Ordinal);
        Assert.Contains("Expected no ImportProducts job, but found 1.", unexpected.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComparesClassesByTheirJson()
    {
        var pulse = new FakePulse();
        await pulse.Emit(new PriceSet { Sku = "tea", Price = 4.2m });

        pulse.Emitted(new PriceSet { Sku = "tea", Price = 4.2m });
        Assert.Throws<PulseAssertionException>(() => pulse.Emitted(new PriceSet { Sku = "tea", Price = 5m }));
    }

    [Fact]
    public async Task CallsHandlersWithoutContextOrToken()
    {
        var pulse = new FakePulse();
        var handler = new RefundBigSales(pulse);

        await handler.Handle(new SaleCompleted(300), stream: "Sale-3");
        await handler.Handle(new SaleCompleted(5));

        pulse.Emitted<SaleRefunded>("Sale-3");
        pulse.Enqueued(new ImportProducts("refund-300.csv"));
    }

    [Fact]
    public async Task CallsJobHandlersAndRecordsTheirProgress()
    {
        var context = await new ReportPages().Handle(new RebuildIndex(3), attempt: 2);

        Assert.Equal(("rebuild-index", 2), (context.JobName, context.Attempt));
        Assert.Equal([(1, 3), (2, 3), (3, 3)], context.Progress);
        Assert.Equal((3, 3), context.LastProgress);
    }

    [Fact]
    public async Task TestHostRunsTheRealFlowAndShowsOnlyWhatHappenedAfterGiven()
    {
        await using var pulse = await PulseTestHost.Start(pulse => pulse.AddHandler<SaleCompleted, RefundBigSales>());

        await pulse.Given("Sale-1", new SaleCompleted(500));
        await pulse.Emit("Sale-2", new SaleCompleted(200));
        await pulse.WaitUntilIdle();

        pulse.Emitted(new SaleRefunded(200));
        pulse.Emitted<SaleCompleted>("Sale-2");
        pulse.NotEmitted<SaleCompleted>("Sale-1");
        pulse.Enqueued(new ImportProducts("refund-200.csv"));
        pulse.NothingFailed();
    }

    [Fact]
    public async Task WaitUntilIdleWaitsForSlowHandlersAndJobs()
    {
        var handled = 0;
        var imported = 0;
        await using var pulse = await PulseTestHost.Start(pulse =>
        {
            pulse.When<SaleCompleted>(async _ =>
            {
                await Task.Delay(300);
                Interlocked.Increment(ref handled);
            });
            pulse.OnJob<ImportProducts>(async _ =>
            {
                await Task.Delay(300);
                Interlocked.Increment(ref imported);
            });
        });

        await pulse.Emit(new SaleCompleted(1));
        await pulse.Enqueue(new ImportProducts("a.csv"));
        await pulse.WaitUntilIdle();

        Assert.Equal((1, 1), (Volatile.Read(ref handled), Volatile.Read(ref imported)));
    }

    [Fact]
    public async Task NothingFailedShowsFailuresThatWouldOtherwiseGoUnnoticed()
    {
        await using var pulse = await PulseTestHost.Start(pulse =>
        {
            pulse.On<SaleCompleted>("printer", (_, _, _) => throw new InvalidOperationException("The printer is out of paper."));
            pulse.OnJob<ImportProducts>(_ => throw new InvalidOperationException("The file is missing."));
        });

        await pulse.Emit("Sale-1", new SaleCompleted(1));
        await pulse.Enqueue(new ImportProducts("a.csv"));
        await pulse.WaitUntilIdle();

        var failure = Assert.Throws<PulseAssertionException>(pulse.NothingFailed);
        Assert.Contains("Handler 'printer' stopped on stream Sale-1: System.InvalidOperationException: The printer is out of paper.", failure.Message, StringComparison.Ordinal);
        Assert.Contains("The file is missing.", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitUntilIdleSaysWhatIsStillBusy()
    {
        await using var pulse = await PulseTestHost.Start(pulse =>
            pulse.On<SaleCompleted>("stuck", (_, _, cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken)));

        await pulse.Emit("Sale-1", new SaleCompleted(1));
        var busy = await Assert.ThrowsAsync<PulseAssertionException>(() => pulse.WaitUntilIdle(TimeSpan.FromMilliseconds(300)));

        Assert.Contains("Handler 'stuck' has not handled event 1 (SaleCompleted) of stream Sale-1 yet.", busy.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestHostUsesTheServicesYouRegister()
    {
        await using var pulse = await PulseTestHost.Start(
            pulse => pulse.AddHandler<SaleCompleted, CountSales>(),
            services => services.AddSingleton<SaleCounter>());

        await pulse.Given(new SaleCompleted(1), new SaleCompleted(2));

        Assert.Equal(2, pulse.Services.GetRequiredService<SaleCounter>().Count);
        pulse.NotEmitted<SaleCompleted>();
        Assert.Single(await pulse.Control.GetCursors());
    }

    [Fact]
    public async Task TestHostRunsOnAnotherStore()
    {
        using var database = new TempDatabase();
        await using (var pulse = await PulseTestHost.Start(pulse =>
        {
            pulse.UseSqlite(database.ConnectionString);
            pulse.AddHandler<SaleCompleted, RefundBigSales>();
        }))
        {
            await pulse.Emit("Sale-1", new SaleCompleted(300));
            await pulse.WaitUntilIdle();

            pulse.Emitted<SaleRefunded>();
            pulse.Enqueued<ImportProducts>();
        }

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task YourOwnChecksAreExtensionMethods()
    {
        var pulse = new FakePulse();

        await pulse.Emit("Sale-1", new SaleCompleted(300));

        pulse.SoldWithNumber(300);
    }

    private sealed class Checkout(IPulse pulse)
    {
        public async Task Complete(int number, int total)
        {
            await pulse.Emit($"Sale-{number}", new SaleCompleted(number));
            if (total > 100)
            {
                await pulse.Emit($"Sale-{number}", new SaleRefunded(number));
                await pulse.Enqueue(new ImportProducts($"refund-{number}.csv"));
            }
        }
    }

    private sealed class PriceSet
    {
        public string Sku { get; init; } = string.Empty;

        public decimal Price { get; init; }
    }

    private sealed class RefundBigSales(IPulse pulse) : IEventHandler<SaleCompleted>
    {
        public async Task Handle(SaleCompleted @event, EventContext context, CancellationToken cancellationToken)
        {
            if (@event.Number > 100)
            {
                await pulse.Emit(context.Stream, new SaleRefunded(@event.Number), cancellationToken);
                await pulse.Enqueue(new ImportProducts($"refund-{@event.Number}.csv"), cancellationToken);
            }
        }
    }

    private sealed class ReportPages : IJobHandler<RebuildIndex>
    {
        public async Task Handle(RebuildIndex job, JobContext context, CancellationToken cancellationToken)
        {
            for (var page = 1; page <= job.Shard; page++)
            {
                await context.Report(page, job.Shard, cancellationToken);
            }
        }
    }

    private sealed class SaleCounter
    {
        private int _count;

        public int Count
        {
            get
            {
                return Volatile.Read(ref _count);
            }
        }

        public void Add()
        {
            Interlocked.Increment(ref _count);
        }
    }

    private sealed class CountSales(SaleCounter counter) : IEventHandler<SaleCompleted>
    {
        public Task Handle(SaleCompleted @event, EventContext context, CancellationToken cancellationToken)
        {
            counter.Add();
            return Task.CompletedTask;
        }
    }
}

internal static class ShopAssertions
{
    public static SaleCompleted SoldWithNumber(this IRecordedPulse pulse, int number)
    {
        var sale = pulse.Emitted<SaleCompleted>();
        Assert.Equal(number, sale.Number);
        return sale;
    }
}
