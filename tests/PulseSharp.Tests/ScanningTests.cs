using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace PulseSharp.Tests;

public sealed class ScanningTests
{
    [Fact]
    public async Task AddsThePublicAndInternalHandlerClassesOfAnAssembly()
    {
        var recorder = new ScanRecorder();
        await using var host = await TestHost.Start(
            pulse => pulse.AddHandlersFromAssembly(typeof(ScanningTests).Assembly),
            services => services.AddSingleton(recorder)
        );

        await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(1));
        await host.Pulse.Emit(new SaleStream("A"), new SaleRefunded(2));

        await Eventually.True(() => recorder.Items.Count == 2, "both scanned handlers receive their event");
        Assert.Contains("scanned-receipts:1", recorder.Items);
        Assert.Contains("ScannedRefunds:2", recorder.Items);

        var handlerNames = (await host.Control.GetCursors()).Select(cursor => cursor.HandlerName).ToList();
        Assert.Equal(["ScannedRefunds", "scanned-receipts"], handlerNames);
    }
}

public sealed class ScanRecorder
{
    public ConcurrentQueue<string> Items { get; } = new();
}

[HandlerName("scanned-receipts")]
public sealed class ScannedReceipts(ScanRecorder recorder) : IEventHandler<SaleCompleted>
{
    public Task Handle(SaleCompleted @event, EventContext context, CancellationToken cancellationToken)
    {
        recorder.Items.Enqueue($"{context.HandlerName}:{@event.Number}");
        return Task.CompletedTask;
    }
}

internal sealed class ScannedRefunds(ScanRecorder recorder) : IEventHandler<SaleRefunded>
{
    public Task Handle(SaleRefunded @event, EventContext context, CancellationToken cancellationToken)
    {
        recorder.Items.Enqueue($"{context.HandlerName}:{@event.Number}");
        return Task.CompletedTask;
    }
}

public abstract class AbstractHandlerIsSkipped : IEventHandler<SaleCompleted>
{
    public abstract Task Handle(SaleCompleted @event, EventContext context, CancellationToken cancellationToken);
}
