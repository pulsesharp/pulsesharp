using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace PulseSharp.Tests;

public sealed class DeliveryTests
{
    [Fact]
    public async Task DeliversTheEventsOfOneStreamInOrder()
    {
        var received = new ConcurrentQueue<string>();
        await using var host = await TestHost.Start(pulse =>
        {
            pulse.On<SaleCompleted>("recorder", (sale, context, _) =>
            {
                received.Enqueue($"{context.Stream}:{sale.Number}");
                return Task.CompletedTask;
            });
        });

        await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(1));
        await host.Pulse.Emit(new SaleStream("B"), new SaleCompleted(10));
        await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(2));
        await host.Pulse.Emit(new SaleStream("B"), new SaleCompleted(11));
        await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(3));

        await Eventually.True(() => received.Count == 5, "all five events are received");
        Assert.Equal(["Sale-A:1", "Sale-A:2", "Sale-A:3"], received.Where(item => item.StartsWith("Sale-A", StringComparison.Ordinal)));
        Assert.Equal(["Sale-B:10", "Sale-B:11"], received.Where(item => item.StartsWith("Sale-B", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task DescribesTheEventInTheContext()
    {
        EventContext? received = null;
        await using var host = await TestHost.Start(pulse =>
        {
            pulse.Node = "till-1";
            pulse.On<SaleCompleted>("recorder", (_, context, _) =>
            {
                received = context;
                return Task.CompletedTask;
            });
        });

        var before = DateTimeOffset.UtcNow;
        var result = await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(1));

        await Eventually.True(() => received is not null, "the event is received");
        Assert.Equal(result.EventId, received!.EventId);
        Assert.Equal("SaleCompleted", received.EventName);
        Assert.Equal("Sale-A", received.Stream);
        Assert.Equal(1, received.StreamPosition);
        Assert.Equal("till-1", received.Origin);
        Assert.Equal("recorder", received.HandlerName);
        Assert.Equal(1, received.Attempt);
        Assert.InRange(received.EmittedAt, before, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task UsesTheNameFromTheEventNameAttribute()
    {
        string? eventName = null;
        await using var host = await TestHost.Start(pulse =>
        {
            pulse.On<SaleVoided>("recorder", (_, context, _) =>
            {
                eventName = context.EventName;
                return Task.CompletedTask;
            });
        });

        await host.Pulse.Emit(new SaleStream("A"), new SaleVoided(1));

        await Eventually.True(() => eventName is not null, "the event is received");
        Assert.Equal("sale-voided", eventName);
    }

    [Fact]
    public async Task RetriesAFailingHandlerUntilItSucceeds()
    {
        var attempts = new ConcurrentQueue<int>();
        var delivered = new ConcurrentQueue<int>();
        await using var host = await TestHost.Start(pulse =>
        {
            pulse.On<SaleCompleted>("flaky", (sale, context, _) =>
            {
                attempts.Enqueue(context.Attempt);
                if (sale.Number == 1 && context.Attempt < 3)
                {
                    throw new InvalidOperationException("Not yet.");
                }

                delivered.Enqueue(sale.Number);
                return Task.CompletedTask;
            });
        });

        await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(1));
        await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(2));

        await Eventually.True(() => delivered.Count == 2, "both events are delivered");
        Assert.Equal([1, 2], delivered);
        Assert.Equal([1, 2, 3, 1], attempts);
    }

    [Fact]
    public async Task StopsOnlyTheFailingStreamAfterMaxAttempts()
    {
        var calls = new ConcurrentQueue<string>();
        await using var host = await TestHost.Start(pulse =>
        {
            pulse.MaxAttempts = 2;
            pulse.On<SaleCompleted>("picky", (sale, context, _) =>
            {
                calls.Enqueue($"{context.Stream}:{sale.Number}");
                if (context.Stream == "Sale-A")
                {
                    throw new InvalidOperationException("Stream A always fails.");
                }

                return Task.CompletedTask;
            });
        });

        await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(1));
        await host.Pulse.Emit(new SaleStream("B"), new SaleCompleted(1));
        await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(2));
        await host.Pulse.Emit(new SaleStream("B"), new SaleCompleted(2));

        await Eventually.True(() => calls.Count(call => call.StartsWith("Sale-B", StringComparison.Ordinal)) == 2, "stream B is delivered");
        await Eventually.True(() => calls.Count(call => call == "Sale-A:1") == 2, "event A1 is tried twice");
        await Eventually.StaysTrue(() => calls.Count(call => call.StartsWith("Sale-A", StringComparison.Ordinal)) == 2, "stream A is stopped");
        Assert.DoesNotContain("Sale-A:2", calls);
    }

    [Fact]
    public async Task AFailingHandlerDoesNotBlockOtherHandlers()
    {
        var healthy = new ConcurrentQueue<int>();
        await using var host = await TestHost.Start(pulse =>
        {
            pulse.MaxAttempts = 1;
            pulse.On<SaleCompleted>("broken", (_, _, _) => throw new InvalidOperationException("Always fails."));
            pulse.On<SaleCompleted>("healthy", (sale, _, _) =>
            {
                healthy.Enqueue(sale.Number);
                return Task.CompletedTask;
            });
        });

        await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(1));
        await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(2));

        await Eventually.True(() => healthy.Count == 2, "the healthy handler receives both events");
        Assert.Equal([1, 2], healthy);
    }

    [Fact]
    public async Task DeliversOnlyTheEventTypesAHandlerHandles()
    {
        var received = new ConcurrentQueue<int>();
        await using var host = await TestHost.Start(pulse =>
        {
            pulse.On<SaleRefunded>("refunds", (refund, _, _) =>
            {
                received.Enqueue(refund.Number);
                return Task.CompletedTask;
            });
        });

        await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(1));
        await host.Pulse.Emit(new SaleStream("A"), new SaleRefunded(2));

        await Eventually.True(() => received.Count == 1, "the refund is received");
        await Eventually.StaysTrue(() => received.Count == 1, "only the refund is received");
        Assert.Equal([2], received);
    }

    [Fact]
    public async Task CreatesHandlerClassesThroughDependencyInjection()
    {
        var recorder = new Recorder();
        await using var host = await TestHost.Start(
            pulse => pulse.AddHandler<SaleCompleted, RecordingHandler>(),
            services => services.AddSingleton(recorder)
        );

        await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(1));

        await Eventually.True(() => recorder.Items.Count == 1, "the handler class receives the event");
        Assert.Equal("RecordingHandler:1", recorder.Items.Single());
    }

    [Fact]
    public async Task AHandlerClassForSeveralEventTypesKeepsOneOrderPerStream()
    {
        var recorder = new Recorder();
        await using var host = await TestHost.Start(
            pulse => pulse.AddHandler<SaleLedger>(),
            services => services.AddSingleton(recorder)
        );

        await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(1));
        await host.Pulse.Emit(new SaleStream("A"), new SaleRefunded(2));
        await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(3));

        await Eventually.True(() => recorder.Items.Count == 3, "all three events are received");
        Assert.Equal(["sale-ledger:completed:1", "sale-ledger:refunded:2", "sale-ledger:completed:3"], recorder.Items);
    }

    private sealed class Recorder
    {
        public ConcurrentQueue<string> Items { get; } = new();
    }

    private sealed class RecordingHandler(Recorder recorder) : IEventHandler<SaleCompleted>
    {
        public Task Handle(SaleCompleted @event, EventContext context, CancellationToken cancellationToken)
        {
            recorder.Items.Enqueue($"{context.HandlerName}:{@event.Number}");
            return Task.CompletedTask;
        }
    }

    [HandlerName("sale-ledger")]
    private sealed class SaleLedger(Recorder recorder) : IEventHandler<SaleCompleted>, IEventHandler<SaleRefunded>
    {
        public Task Handle(SaleCompleted @event, EventContext context, CancellationToken cancellationToken)
        {
            recorder.Items.Enqueue($"{context.HandlerName}:completed:{@event.Number}");
            return Task.CompletedTask;
        }

        public Task Handle(SaleRefunded @event, EventContext context, CancellationToken cancellationToken)
        {
            recorder.Items.Enqueue($"{context.HandlerName}:refunded:{@event.Number}");
            return Task.CompletedTask;
        }
    }
}
