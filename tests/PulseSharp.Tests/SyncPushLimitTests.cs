using Microsoft.Extensions.DependencyInjection;
using PulseSharp.Stores;
using PulseSharp.Sync;
using PulseSharp.Sync.Server;

namespace PulseSharp.Tests;

public sealed partial class SyncTests
{
    [Fact]
    public void ASyncServerMustSayWhichStreamsDevicesMayWrite()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddPulseSharp(pulse =>
        {
            pulse.UseInMemory();
            pulse.UseSyncServer(server => server.AllowNode("till-1", "key-1"));
        }));

        Assert.Contains("PushStreams", error.Message, StringComparison.Ordinal);
        Assert.Contains("PushAnyStream", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeviceCanOnlyWriteStreamsItIsAllowedToAndItsOtherEventsStillArrive()
    {
        await using var cloud = await SyncCloud.Start(server => server
            .AllowNode("till-1", "key-1")
            .AllowNode("till-2", "key-2")
            .PushStreams(node => [$"Sale-{node}-"]));
        await using var till = await SyncDevice.Start("till-1", cloud.CreateHandler());
        await till.Sync.SyncNow();

        await till.Pulse.Emit("Sale-till-1-2026-10-24", new SaleCompleted(1));
        await till.Pulse.Emit("Product-1", new ProductChanged("tea at 0.01"));
        await till.Pulse.Emit("Sale-till-2-2026-10-24", new SaleCompleted(2));
        await till.Pulse.Emit("Sale-till-1-2026-10-24", new SaleCompleted(3));
        var status = await till.Sync.SyncNow();

        Assert.True(status.Online, status.LastError);
        await Eventually.True(() => cloud.Sales.Count == 2, "the cloud handles the two sales the till may write");
        await Eventually.StaysTrue(() => cloud.Sales.Count == 2, "no refused event is handled later");
        Assert.Equal([1, 3], cloud.Sales.Select(sale => sale.Number));
        var saved = await cloud.Store.ReadForSync(new SyncQuery(0, "till-1", null, [], 100), None);
        Assert.Equal(["Sale-till-1-2026-10-24", "Sale-till-1-2026-10-24"], saved.Select(stored => stored.Stream));
    }

    [Fact]
    public async Task ARefusedEventIsNotSentAgainAndDoesNotBlockTheNextOnes()
    {
        await using var cloud = await SyncCloud.Start(server => server.AllowNode("till-1", "key-1").PushStreams(node => [$"Sale-{node}-"]));
        var network = new Network(cloud.CreateHandler());
        await using var till = await SyncDevice.Start("till-1", network);
        await till.Sync.SyncNow();
        await till.Pulse.Emit("Menu", new ProductChanged("not allowed"));
        await till.Sync.SyncNow();
        var pushesAfterFirst = network.Pushes;

        await till.Sync.SyncNow();
        await till.Pulse.Emit("Sale-till-1-2026-10-24", new SaleCompleted(1));
        var status = await till.Sync.SyncNow();

        Assert.Equal(pushesAfterFirst, network.Pushes - 1);
        Assert.True(status.Online, status.LastError);
        await Eventually.True(() => cloud.Sales.Count == 1, "the next allowed event still arrives");
    }

    [Fact]
    public async Task EachDeviceHasItsOwnAllowedStreams()
    {
        await using var cloud = await SyncCloud.Start(server => server
            .AllowNode("till-1", "key-1")
            .AllowNode("till-2", "key-2")
            .PushStreams(node => node == "till-1" ? ["Sale-till-1-"] : ["Sale-till-2-", "Refund-till-2-"]));
        await using var first = await SyncDevice.Start("till-1", cloud.CreateHandler());
        await using var second = await SyncDevice.Start("till-2", cloud.CreateHandler());

        await first.Pulse.Emit("Refund-till-2-x", new SaleCompleted(1));
        await second.Pulse.Emit("Refund-till-2-x", new SaleCompleted(2));
        await first.Sync.SyncNow();
        await second.Sync.SyncNow();

        await Eventually.True(() => cloud.Sales.Count == 1, "only till-2 may write the refund stream");
        Assert.Equal(2, Assert.Single(cloud.Sales).Number);
    }

    [Fact]
    public async Task PushAnyStreamLetsEveryDeviceWriteEveryStream()
    {
        await using var cloud = await SyncCloud.Start(server => server.AllowNode("till-1", "key-1").PushAnyStream());
        await using var till = await SyncDevice.Start("till-1", cloud.CreateHandler());

        await till.Pulse.Emit("Anything-at-all", new SaleCompleted(1));
        await till.Sync.SyncNow();

        await Eventually.True(() => cloud.Sales.Count == 1, "the event arrives");
    }
}
