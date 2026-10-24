using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using PulseSharp.Stores;
using PulseSharp.Sync;

namespace PulseSharp.Tests;

public sealed partial class SyncTests
{
    [Fact]
    public async Task TheStatusCountsTheEventsTheServerHasNotConfirmed()
    {
        await using var cloud = await SyncCloud.Start();
        var network = new Network(cloud.CreateHandler()) { Offline = true };
        await using var till = await SyncDevice.Start("till-1", network, sync => sync.Push = true);

        await EmitSales(till, 3);
        var offline = await till.Sync.SyncNow();
        network.Offline = false;
        var online = await till.Sync.SyncNow();

        Assert.False(offline.Online);
        Assert.Equal(3, offline.Waiting);
        Assert.True(online.Online, online.LastError);
        Assert.Equal(0, online.Waiting);
    }

    [Fact]
    public async Task TheStatusChangedEventTellsAboutEveryChange()
    {
        await using var cloud = await SyncCloud.Start();
        var network = new Network(cloud.CreateHandler()) { Offline = true };
        await using var till = await SyncDevice.Start("till-1", network);
        var changes = new ConcurrentQueue<SyncStatus>();
        till.Sync.StatusChanged += changes.Enqueue;

        await EmitSales(till, 2);
        await till.Sync.SyncNow();
        network.Offline = false;
        await till.Sync.SyncNow();

        var seen = changes.ToList();
        Assert.Contains(seen, status => !status.Online && status.Waiting == 2);
        Assert.Contains(seen, status => status is { Online: true, Waiting: 0 });
    }

    [Fact]
    public async Task WhileOfflineSavingAnEventUpdatesTheWaitingCountWithoutAnyCall()
    {
        await using var cloud = await SyncCloud.Start();
        var network = new Network(cloud.CreateHandler()) { Offline = true };
        await using var till = await SyncDevice.Start("till-1", network);
        await till.Sync.SyncNow();
        var changes = new ConcurrentQueue<SyncStatus>();
        till.Sync.StatusChanged += changes.Enqueue;

        await EmitSales(till, 2);

        await Eventually.True(() => till.Sync.Status.Waiting == 2, "the status shows two sales waiting");
        Assert.Contains(changes, status => status.Waiting == 2);
    }

    [Fact]
    public async Task AnEventSavedWhileOnlineIsSentAtOnceWithoutWaitingForTheInterval()
    {
        await using var cloud = await SyncCloud.Start();
        await using var till = await SyncDevice.Start("till-1", cloud.CreateHandler());
        var online = await till.Sync.SyncNow();
        Assert.True(online.Online, online.LastError);

        await till.Pulse.Emit(new SaleCompleted(1));

        await Eventually.True(() => cloud.Sales.Count == 1, "the cloud receives the sale, although the interval is an hour");
        await Eventually.True(() => till.Sync.Status.Waiting == 0, "nothing is waiting afterwards");
    }

    [Fact]
    public async Task WhileTheServerCannotBeReachedASavedEventDoesNotMakeSyncTryAgainAtOnce()
    {
        await using var cloud = await SyncCloud.Start();
        var network = new Network(cloud.CreateHandler()) { Offline = true };
        await using var till = await SyncDevice.Start("till-1", network);
        await till.Sync.SyncNow();
        var pushesBefore = network.Pushes;

        await EmitSales(till, 5);
        await Eventually.StaysTrue(() => network.Pushes == pushesBefore, "no push is tried while sync is backing off");
    }

    [Fact]
    public async Task TheWaitingCountIsKnownAfterARestartThatStartsOffline()
    {
        using var database = new TempDatabase();
        await using var cloud = await SyncCloud.Start();

        await using (var before = await SyncDevice.Start("till-1", cloud.CreateHandler(), sqlite: database.ConnectionString))
        {
            await EmitSales(before, 2);
            await before.Sync.SyncNow();
        }

        var network = new Network(cloud.CreateHandler()) { Offline = true };
        await using (var after = await SyncDevice.Start("till-1", network, sqlite: database.ConnectionString))
        {
            await after.Pulse.Emit(new SaleCompleted(3));
            var status = await after.Sync.SyncNow();

            Assert.False(status.Online);
            Assert.Equal(1, status.Waiting);
        }

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task ASubscriberThatThrowsDoesNotBreakSync()
    {
        await using var cloud = await SyncCloud.Start();
        await using var till = await SyncDevice.Start("till-1", cloud.CreateHandler());
        till.Sync.StatusChanged += _ => throw new InvalidOperationException("A broken screen.");

        await EmitSales(till, 2);
        var status = await till.Sync.SyncNow();

        Assert.True(status.Online, status.LastError);
        await Eventually.True(() => cloud.Sales.Count == 2, "the events still arrive");
    }

    [Fact]
    public async Task EventsFromTheServerAreHandledAtOnceAfterTheyArrive()
    {
        await using var cloud = await SyncCloud.Start();
        await using var till = await SyncDevice.Start("till-1", cloud.CreateHandler());
        await till.Sync.SyncNow();

        await cloud.Pulse.Emit("Product-1", new ProductChanged("A"));
        await till.Sync.SyncNow();

        await Eventually.True(() => till.Products.Count == 1, "the till handles the product");
    }
}
