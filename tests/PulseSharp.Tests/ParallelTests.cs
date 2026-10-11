using System.Collections.Concurrent;

namespace PulseSharp.Tests;

public sealed class ParallelTests
{
    [Fact]
    public async Task HandlesSeveralStreamsAtTheSameTime()
    {
        var gauge = new ConcurrencyGauge();
        await using var host = await TestHost.Start(pulse =>
        {
            pulse.MaxParallelStreams = 4;
            pulse.On<SaleCompleted>("slow", async (_, _, ct) => await gauge.Measure(TimeSpan.FromMilliseconds(100), ct));
        });

        for (var stream = 0; stream < 8; stream++)
        {
            await host.Pulse.Emit(new SaleStream($"{stream}"), new SaleCompleted(stream));
        }

        await Eventually.True(() => gauge.Finished == 8, "all eight streams are handled");
        Assert.InRange(gauge.Highest, 2, 4);
    }

    [Fact]
    public async Task HandlesOneStreamAtATimeWhenParallelismIsOne()
    {
        var gauge = new ConcurrencyGauge();
        await using var host = await TestHost.Start(pulse =>
        {
            pulse.MaxParallelStreams = 1;
            pulse.On<SaleCompleted>("slow", async (_, _, ct) => await gauge.Measure(TimeSpan.FromMilliseconds(20), ct));
        });

        for (var stream = 0; stream < 5; stream++)
        {
            await host.Pulse.Emit(new SaleStream($"{stream}"), new SaleCompleted(stream));
        }

        await Eventually.True(() => gauge.Finished == 5, "all five streams are handled");
        Assert.Equal(1, gauge.Highest);
    }

    [Fact]
    public async Task KeepsTheOrderOfEachStreamWhileStreamsRunInParallel()
    {
        var received = new ConcurrentQueue<(string Stream, int Number)>();
        await using var host = await TestHost.Start(pulse =>
        {
            pulse.MaxParallelStreams = 4;
            pulse.On<SaleCompleted>("recorder", async (sale, context, ct) =>
            {
                await Task.Delay(sale.Number % 3, ct);
                received.Enqueue((context.Stream, sale.Number));
            });
        });

        for (var number = 0; number < 40; number++)
        {
            await host.Pulse.Emit(new SaleStream($"{number % 4}"), new SaleCompleted(number));
        }

        await Eventually.True(() => received.Count == 40, "all events are received");
        foreach (var stream in received.GroupBy(item => item.Stream))
        {
            var numbers = stream.Select(item => item.Number).ToList();
            Assert.Equal(numbers.Order(), numbers);
        }
    }

    private sealed class ConcurrencyGauge
    {
        private int _running;
        private int _highest;
        private int _finished;

        public int Highest
        {
            get
            {
                return Volatile.Read(ref _highest);
            }
        }

        public int Finished
        {
            get
            {
                return Volatile.Read(ref _finished);
            }
        }

        public async Task Measure(TimeSpan duration, CancellationToken cancellationToken)
        {
            var running = Interlocked.Increment(ref _running);
            RaiseHighest(running);
            await Task.Delay(duration, cancellationToken);
            Interlocked.Decrement(ref _running);
            Interlocked.Increment(ref _finished);
        }

        private void RaiseHighest(int running)
        {
            var highest = Volatile.Read(ref _highest);
            while (running > highest)
            {
                var previous = Interlocked.CompareExchange(ref _highest, running, highest);
                if (previous == highest)
                {
                    return;
                }

                highest = previous;
            }
        }
    }
}
