using System.Collections.Concurrent;

namespace PulseSharp.Tests;

public sealed class ControlTests
{
    [Fact]
    public async Task ShowsAFailedStreamInTheCursors()
    {
        await using var host = await TestHost.Start(pulse =>
        {
            pulse.MaxAttempts = 1;
            pulse.On<SaleCompleted>("picky", (_, _, _) => throw new InvalidOperationException("Printer offline."));
        });

        await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(1));

        var cursor = await WaitForCursor(host, "picky", "Sale-A", CursorState.Failed);
        Assert.Equal(0, cursor.Position);
        Assert.Equal(1, cursor.Attempts);
        Assert.Contains("Printer offline.", cursor.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetryRunsTheFailedEventAgain()
    {
        var printerOnline = false;
        var printed = new ConcurrentQueue<int>();
        await using var host = await TestHost.Start(pulse =>
        {
            pulse.MaxAttempts = 1;
            pulse.On<SaleCompleted>("printer", (sale, _, _) =>
            {
                if (!Volatile.Read(ref printerOnline))
                {
                    throw new InvalidOperationException("Printer offline.");
                }

                printed.Enqueue(sale.Number);
                return Task.CompletedTask;
            });
        });

        await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(1));
        await WaitForCursor(host, "printer", "Sale-A", CursorState.Failed);

        Volatile.Write(ref printerOnline, true);
        await host.Control.Retry("printer", "Sale-A");

        await Eventually.True(() => printed.Count == 1, "the event is printed after the retry");
        var cursor = await WaitForCursor(host, "printer", "Sale-A", CursorState.Active);
        Assert.Equal(1, cursor.Position);
    }

    [Fact]
    public async Task SkipMovesPastTheFailedEvent()
    {
        var handled = new ConcurrentQueue<int>();
        await using var host = await TestHost.Start(pulse =>
        {
            pulse.MaxAttempts = 1;
            pulse.On<SaleCompleted>("picky", (sale, _, _) =>
            {
                if (sale.Number == 1)
                {
                    throw new InvalidOperationException("Bad sale.");
                }

                handled.Enqueue(sale.Number);
                return Task.CompletedTask;
            });
        });

        await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(1));
        await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(2));
        await WaitForCursor(host, "picky", "Sale-A", CursorState.Failed);

        await host.Control.Skip("picky", "Sale-A");

        await Eventually.True(() => handled.Count == 1, "the next event is handled");
        Assert.Equal([2], handled);
        var cursor = await WaitForCursor(host, "picky", "Sale-A", CursorState.Active);
        Assert.Equal(2, cursor.Position);
    }

    [Fact]
    public async Task RewindHandlesEveryEventAgainInOrder()
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
        await Eventually.True(() => received.Count == 3, "the first pass is done");

        await host.Control.Rewind("recorder");

        await Eventually.True(() => received.Count == 6, "the rewound pass is done");
        var secondPass = received.Skip(3).ToList();
        Assert.Equal(["Sale-A:1", "Sale-A:2"], secondPass.Where(item => item.StartsWith("Sale-A", StringComparison.Ordinal)));
        Assert.Equal(["Sale-B:10"], secondPass.Where(item => item.StartsWith("Sale-B", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ListsTheCursorsOfAllHandlers()
    {
        await using var host = await TestHost.Start(pulse =>
        {
            pulse.On<SaleCompleted>("first", (_, _, _) => Task.CompletedTask);
            pulse.On<SaleCompleted>("second", (_, _, _) => Task.CompletedTask);
        });

        await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(1));

        await Eventually.True(() => host.Control.GetCursors().Result.Count == 2, "both handlers have a cursor");
        var cursors = await host.Control.GetCursors();
        Assert.Equal(["first", "second"], cursors.Select(cursor => cursor.HandlerName));
        Assert.All(cursors, cursor => Assert.Equal(1, cursor.Position));
    }

    [Fact]
    public async Task RefusesAnUnknownHandler()
    {
        await using var host = await TestHost.Start(pulse => pulse.On<SaleCompleted>("known", (_, _, _) => Task.CompletedTask));

        await Assert.ThrowsAsync<ArgumentException>(() => host.Control.Rewind("unknown"));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Control.GetCursors("unknown"));
    }

    [Fact]
    public async Task RefusesToRetryAStreamTheHandlerHasNeverSeen()
    {
        await using var host = await TestHost.Start(pulse => pulse.On<SaleCompleted>("known", (_, _, _) => Task.CompletedTask));

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Control.Retry("known", "Sale-Z"));
    }

    [Fact]
    public async Task RefusesToSkipWhenNothingIsWaiting()
    {
        await using var host = await TestHost.Start(pulse => pulse.On<SaleCompleted>("known", (_, _, _) => Task.CompletedTask));
        await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(1));
        await WaitForCursor(host, "known", "Sale-A", CursorState.Active);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Control.Skip("known", "Sale-A"));
    }

    private static async Task<HandlerCursor> WaitForCursor(TestHost host, string handlerName, string stream, CursorState state)
    {
        HandlerCursor? found = null;
        await Eventually.True(
            () =>
            {
                found = host.Control.GetCursors(handlerName).Result.SingleOrDefault(cursor => cursor.Stream == stream);
                return found?.State == state && (state != CursorState.Active || found.Position > 0);
            },
            $"{handlerName} is {state} in {stream}"
        );
        return found!;
    }
}
