using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PulseSharp.Sqlite;
using PulseSharp.Stores;
using PulseSharp.Sync;

namespace PulseSharp.Tests;

public sealed class EventRetentionTests : IDisposable
{
    private readonly TempDatabase _database = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _database.Dispose();
    }

    [Fact]
    public async Task HandledEventsAreDeletedAfterTheLimitExceptTheNewestOfTheStreamAndNewEventsStillArrive()
    {
        var handled = new ConcurrentQueue<int>();
        await using var host = await StartHost(handled, keep: TimeSpan.FromMilliseconds(1));

        for (var number = 1; number <= 10; number++)
        {
            await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(number));
        }

        await Eventually.True(() => handled.Count == 10, "all ten events are handled");
        await WaitForStreamToHaveEvents(1);
        await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(11));

        await Eventually.True(() => handled.Count == 11, "a new event after the cleanup is still handled");
        Assert.Equal(Enumerable.Range(1, 11), handled);
    }

    [Fact]
    public async Task EventsAHandlerHasNotHandledAreNeverDeleted()
    {
        var handled = new ConcurrentQueue<int>();
        await using var host = await StartHost(handled, keep: TimeSpan.FromMilliseconds(1), failAt: 3);

        for (var number = 1; number <= 10; number++)
        {
            await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(number));
        }

        await Eventually.True(() => handled.Count == 2, "the handler handles events 1 and 2 and stops at 3");
        await WaitForStreamToHaveEvents(8);
        await Eventually.StaysTrue(() => StreamPositions().SequenceEqual(Enumerable.Range(3, 8).Select(position => (long)position)), "events 3 to 10 stay");
    }

    [Fact]
    public async Task NothingIsDeletedWhenNoLimitIsSet()
    {
        var handled = new ConcurrentQueue<int>();
        await using var host = await StartHost(handled, keep: null);

        for (var number = 1; number <= 5; number++)
        {
            await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(number));
        }

        await Eventually.True(() => handled.Count == 5, "all events are handled");
        await Eventually.StaysTrue(() => StreamPositions().Count() == 5, "no event is deleted");
    }

    [Fact]
    public async Task ADevicesOwnEventsAreKeptUntilTheyWereSentToTheServer()
    {
        await using var cloud = await StartCloud();
        var network = new FlakyLink(cloud.CreateHandler()) { Offline = true };
        await using var device = await StartDevice(network);

        for (var number = 1; number <= 5; number++)
        {
            await device.Pulse.Emit(new SaleStream("A"), new SaleCompleted(number));
        }

        await Eventually.StaysTrue(() => StreamPositions().Count() == 5, "nothing is deleted while the events are not sent");

        network.Offline = false;
        var status = await device.Sync.SyncNow();

        Assert.True(status.Online, status.LastError);
        await WaitForStreamToHaveEvents(1);
        Assert.Equal(5, cloud.Received.Count);
    }

    private Task<TestHost> StartHost(ConcurrentQueue<int> handled, TimeSpan? keep, int failAt = 0)
    {
        return TestHost.Start(pulse =>
        {
            pulse.UseSqlite(_database.ConnectionString);
            pulse.Advanced.KeepEvents = keep;
            pulse.MaxAttempts = 1;
            pulse.On<SaleCompleted>("recorder", (sale, _, _) =>
            {
                if (sale.Number == failAt)
                {
                    throw new InvalidOperationException("Failing on purpose.");
                }

                handled.Enqueue(sale.Number);
                return Task.CompletedTask;
            });
        });
    }

    private IEnumerable<long> StreamPositions()
    {
        var store = new SqliteStore(_database.ConnectionString);
        return store.ReadStream("Sale-A", 0, 1000, CancellationToken.None).GetAwaiter().GetResult().Select(stored => stored.StreamPosition).ToList();
    }

    private async Task WaitForStreamToHaveEvents(int count)
    {
        await Eventually.True(() => StreamPositions().Count() == count, $"the stream is cut down to {count} events");
    }

    private static async Task<Cloud> StartCloud()
    {
        var received = new ConcurrentQueue<SaleCompleted>();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddPulseSharp(pulse =>
        {
            pulse.UseInMemory();
            pulse.Node = "cloud";
            pulse.Advanced.PollInterval = TimeSpan.FromMilliseconds(50);
            pulse.When<SaleCompleted>(sale => received.Enqueue(sale));
            pulse.UseSyncServer(server => server.AllowNode("till-1", "key-1").PushAnyStream());
        });

        var app = builder.Build();
        app.MapPulseSharpSync();
        await app.StartAsync();
        return new Cloud(app, received);
    }

    private async Task<Device> StartDevice(HttpMessageHandler handler)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddPulseSharp(pulse =>
        {
            pulse.UseSqlite(_database.ConnectionString);
            pulse.Node = "till-1";
            pulse.Advanced.PollInterval = TimeSpan.FromMilliseconds(50);
            pulse.Advanced.KeepEvents = TimeSpan.FromMilliseconds(1);
            pulse.UseSync(sync =>
            {
                sync.Server = new Uri("http://localhost/pulse/sync");
                sync.ApiKey = "key-1";
                sync.HttpHandler = handler;
                sync.Interval = TimeSpan.FromHours(1);
            });
        });

        var host = builder.Build();
        await host.StartAsync();
        return new Device(host);
    }

    private sealed class Cloud(WebApplication app, ConcurrentQueue<SaleCompleted> received) : IAsyncDisposable
    {
        public IReadOnlyCollection<SaleCompleted> Received
        {
            get
            {
                return received;
            }
        }

        public HttpMessageHandler CreateHandler()
        {
            return app.GetTestServer().CreateHandler();
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class Device(IHost host) : IAsyncDisposable
    {
        public IPulse Pulse
        {
            get
            {
                return host.Services.GetRequiredService<IPulse>();
            }
        }

        public IPulseSync Sync
        {
            get
            {
                return host.Services.GetRequiredService<IPulseSync>();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    private sealed class FlakyLink(HttpMessageHandler server) : DelegatingHandler(server)
    {
        public bool Offline { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Offline)
            {
                throw new HttpRequestException("No network.");
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
