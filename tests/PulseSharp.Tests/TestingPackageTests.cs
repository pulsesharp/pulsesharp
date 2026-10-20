using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using PulseSharp.Testing;

namespace PulseSharp.Tests;

public sealed class TestingPackageTests
{
    [Fact]
    public async Task RecordingPulseRecordsEventsAndJobs()
    {
        var pulse = new RecordingPulse();

        await pulse.Emit(new SaleCompleted(1));
        await pulse.Emit("Sale-2", new SaleCompleted(2));
        await pulse.Emit("Sale-2", new SaleRefunded(2));
        await pulse.Enqueue(new ImportProducts("a.csv"));
        await pulse.EnqueueIn(new ImportProducts("b.csv"), TimeSpan.FromHours(1));

        var sale = pulse.Emitted<SaleCompleted>().ShouldHaveOne(sale => sale.Number == 2);
        pulse.Emitted<SaleCompleted>().ShouldHave(2);
        pulse.Emitted<SaleCompleted>().InStream("Sale-2").ShouldHaveOne();
        pulse.Emitted<SaleVoided>().ShouldHaveNone();
        pulse.Enqueued<ImportProducts>().ShouldHave(2);
        pulse.Enqueued<RebuildIndex>().ShouldHaveNone();
        Assert.Equal(new SaleCompleted(2), sale);
        Assert.Equal([1, 2], pulse.Emitted<SaleCompleted>().Select(emitted => emitted.Number));

        pulse.Clear();
        pulse.Emitted<SaleCompleted>().ShouldHaveNone();
    }

    [Fact]
    public async Task RecordingPulseChecksExpectedVersions()
    {
        var pulse = new RecordingPulse();
        await pulse.Emit("Order-1", new SaleCompleted(1), new EmitOptions { ExpectedVersion = ExpectedVersion.NoStream });

        var conflict = await Assert.ThrowsAsync<ExpectedVersionMismatchException>(
            () => pulse.Emit("Order-1", new SaleCompleted(2), new EmitOptions { ExpectedVersion = ExpectedVersion.NoStream })
        );
        var next = await pulse.Emit("Order-1", new SaleCompleted(3), new EmitOptions { ExpectedVersion = ExpectedVersion.Exactly(1) });

        Assert.Equal(1, conflict.ActualPosition);
        Assert.Equal(2, next.StreamPosition);
    }

    [Fact]
    public async Task FailureMessagesSayWhatWasExpectedAndWhatHappened()
    {
        var pulse = new RecordingPulse();
        await pulse.Emit("Sale-1", new SaleCompleted(20));
        await pulse.Emit("Sale-1", new SaleRefunded(20));

        var notFound = Assert.Throws<PulseAssertionException>(() => pulse.Emitted<SaleCompleted>().ShouldHaveOne(sale => sale.Number == 25));
        var unexpected = Assert.Throws<PulseAssertionException>(() => pulse.Emitted<SaleRefunded>().ShouldHaveNone());
        var nothing = Assert.Throws<PulseAssertionException>(() => pulse.Enqueued<ImportProducts>().ShouldHaveOne());

        Assert.Contains("Expected one SaleCompleted where sale => sale.Number == 25, but found none.", notFound.Message, StringComparison.Ordinal);
        Assert.Contains("SaleCompleted { Number = 20 } in stream Sale-1", notFound.Message, StringComparison.Ordinal);
        Assert.Contains("Other events: SaleRefunded (1).", notFound.Message, StringComparison.Ordinal);
        Assert.Contains("Expected no SaleRefunded, but found 1.", unexpected.Message, StringComparison.Ordinal);
        Assert.Contains("No ImportProducts was enqueued.", nothing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TestEventContextFillsEveryValue()
    {
        var plain = TestEventContext.For(new SaleCompleted(1));
        var named = TestEventContext.For(new SaleVoided(1), stream: "Sale-1", attempt: 2, streamPosition: 4, handlerName: "voids");

        Assert.Equal(("SaleCompleted", "SaleCompleted", 1, 1L, "test-handler", "test"), (plain.EventName, plain.Stream, plain.Attempt, plain.StreamPosition, plain.HandlerName, plain.Origin));
        Assert.Equal(("sale-voided", "Sale-1", 2, 4L, "voids"), (named.EventName, named.Stream, named.Attempt, named.StreamPosition, named.HandlerName));
        Assert.NotEqual(Guid.Empty, plain.EventId);
    }

    [Fact]
    public async Task TestJobContextRecordsProgress()
    {
        var context = TestJobContext.For(new RebuildIndex(1), attempt: 3);
        Assert.Null(context.LastProgress);

        await context.Report(1, 3);
        await context.Report(3, 3);

        Assert.Equal(("rebuild-index", 3), (context.JobName, context.Attempt));
        Assert.Equal([(1, 3), (3, 3)], context.Progress);
        Assert.Equal((3, 3), context.LastProgress);
    }

    [Fact]
    public async Task TestHostRunsTheRealFlowAndShowsOnlyWhatHappenedAfterGiven()
    {
        await using var pulse = await PulseTestHost.Start(pulse => pulse.AddHandler<SaleCompleted, RefundBigSales>());

        await pulse.Given("Sale-1", new SaleCompleted(500));
        await pulse.Emit("Sale-2", new SaleCompleted(200));
        await pulse.Emit("Sale-3", new SaleCompleted(5));
        await pulse.WaitUntilIdle();

        pulse.Emitted<SaleRefunded>().ShouldHaveOne(refund => refund.Number == 200);
        pulse.Emitted<SaleCompleted>().ShouldHave(2);
        pulse.Emitted<SaleCompleted>().InStream("Sale-1").ShouldHaveNone();
        pulse.Enqueued<ImportProducts>().ShouldHaveOne(job => job.File == "refund-200.csv");
        pulse.ShouldHaveNoFailures();
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
    public async Task ShowsFailuresThatWouldOtherwiseGoUnnoticed()
    {
        await using var pulse = await PulseTestHost.Start(pulse =>
        {
            pulse.On<SaleCompleted>("printer", (_, _, _) => throw new InvalidOperationException("The printer is out of paper."));
            pulse.OnJob<ImportProducts>(_ => throw new InvalidOperationException("The file is missing."));
        });

        await pulse.Emit("Sale-1", new SaleCompleted(1));
        await pulse.Enqueue(new ImportProducts("a.csv"));
        await pulse.WaitUntilIdle();

        var failure = Assert.Throws<PulseAssertionException>(pulse.ShouldHaveNoFailures);
        Assert.Contains("Handler 'printer' stopped on stream Sale-1: System.InvalidOperationException: The printer is out of paper.", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Job ImportProducts", failure.Message, StringComparison.Ordinal);
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
        pulse.Emitted<SaleCompleted>().ShouldHaveNone();
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

            pulse.Emitted<SaleRefunded>().ShouldHaveOne();
            pulse.Enqueued<ImportProducts>().ShouldHaveOne();
        }

        SqliteConnection.ClearAllPools();
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
