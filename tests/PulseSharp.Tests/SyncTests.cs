using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PulseSharp.Stores;
using PulseSharp.Sync;
using PulseSharp.Sync.Server;

namespace PulseSharp.Tests;

public sealed record ProductChanged(string Sku);

public sealed partial class SyncTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task SendsTheDeviceEventsToTheCloudOnce()
    {
        await using var cloud = await SyncCloud.Start();
        await using var till = await SyncDevice.Start("till-1", cloud.CreateHandler());

        await till.Pulse.Emit(new SaleCompleted(1));
        await till.Pulse.Emit(new SaleCompleted(2));
        await till.Pulse.Emit(new SaleCompleted(3));
        var status = await till.Sync.SyncNow();
        await till.Sync.SyncNow();

        Assert.True(status.Online, status.LastError);
        Assert.NotNull(status.LastSyncedAt);
        await Eventually.True(() => cloud.Sales.Count == 3, "the cloud handles the three sales");
        await Eventually.StaysTrue(() => cloud.Sales.Count == 3, "the cloud handles each sale once");
        Assert.Equal(3, (await cloud.Store.ReadForSync(new SyncQuery(0, "till-1", null, [], 100), None)).Count);
    }

    [Fact]
    public async Task ReceivesCloudEventsWithoutSendingThemBack()
    {
        await using var cloud = await SyncCloud.Start();
        await using var till = await SyncDevice.Start("till-1", cloud.CreateHandler());

        await cloud.Pulse.Emit("Product-1", new ProductChanged("A"));
        await till.Sync.SyncNow();
        await till.Sync.SyncNow();

        await Eventually.True(() => till.Products.Count == 1, "the till handles the cloud's event");
        Assert.Single(await cloud.Store.ReadForSync(new SyncQuery(0, null, null, [], 100), None));
        Assert.Equal("cloud", Assert.Single(await till.Store.ReadForSync(new SyncQuery(0, null, null, [], 100), None)).Origin);
    }

    [Fact]
    public async Task PassesEventsFromOneTillToAnother()
    {
        await using var cloud = await SyncCloud.Start();
        await using var first = await SyncDevice.Start("till-1", cloud.CreateHandler());
        await using var second = await SyncDevice.Start("till-2", cloud.CreateHandler());

        var emitted = await first.Pulse.Emit(new SaleCompleted(1));
        await first.Sync.SyncNow();
        await second.Sync.SyncNow();
        await second.Sync.SyncNow();
        await first.Sync.SyncNow();

        var received = Assert.Single(await second.Store.ReadForSync(new SyncQuery(0, null, null, [], 100), None));
        Assert.Equal((emitted.EventId, "till-1"), (received.EventId, received.Origin));
        Assert.Single(await cloud.Store.ReadForSync(new SyncQuery(0, null, null, [], 100), None));
        Assert.Single(await first.Store.ReadForSync(new SyncQuery(0, null, null, [], 100), None));
    }

    [Fact]
    public async Task NeverSendsANodeItsOwnEvents()
    {
        await using var cloud = await SyncCloud.Start();
        await using var till = await SyncDevice.Start("till-1", cloud.CreateHandler());
        await till.Pulse.Emit(new SaleCompleted(1));
        await till.Sync.SyncNow();
        using var http = new HttpClient(cloud.CreateHandler()) { BaseAddress = new Uri("http://localhost/pulse/sync/") };

        var forTill1 = await Pull(http, "key-1");
        var forTill2 = await Pull(http, "key-2");

        Assert.Contains("\"events\":[]", forTill1, StringComparison.Ordinal);
        Assert.Contains("\"origin\":\"till-1\"", forTill2, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReceivesOnlyTheRequestedStreams()
    {
        await using var cloud = await SyncCloud.Start();
        await using var till = await SyncDevice.Start("till-1", cloud.CreateHandler(), sync => sync.PullStreams.Add("Product-"));

        await cloud.Pulse.Emit("Product-1", new ProductChanged("A"));
        await cloud.Pulse.Emit("Secret-1", new ProductChanged("B"));
        await till.Sync.SyncNow();

        var received = Assert.Single(await till.Store.ReadForSync(new SyncQuery(0, null, null, [], 100), None));
        Assert.Equal("Product-1", received.Stream);
    }

    [Fact]
    public async Task TheServerDecidesWhatANodeMayReceive()
    {
        await using var cloud = await SyncCloud.Start(server => server.AllowNode("till-1", "key-1", "Product-").PushAnyStream());
        await using var till = await SyncDevice.Start("till-1", cloud.CreateHandler());
        await using var greedy = await SyncDevice.Start("till-1", cloud.CreateHandler(), sync => sync.PullStreams.Add("Secret-"));

        await cloud.Pulse.Emit("Product-1", new ProductChanged("A"));
        await cloud.Pulse.Emit("Secret-1", new ProductChanged("B"));
        var allowed = await till.Sync.SyncNow();
        var refused = await greedy.Sync.SyncNow();

        Assert.True(allowed.Online, allowed.LastError);
        Assert.Equal("Product-1", Assert.Single(await till.Store.ReadForSync(new SyncQuery(0, null, null, [], 100), None)).Stream);
        Assert.False(refused.Online);
        Assert.Contains("may not receive streams starting with 'Secret-'", refused.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesAnUnknownKey()
    {
        await using var cloud = await SyncCloud.Start();
        await using var till = await SyncDevice.Start("till-1", cloud.CreateHandler(), key: "wrong-key");

        await till.Pulse.Emit(new SaleCompleted(1));
        var status = await till.Sync.SyncNow();

        Assert.False(status.Online);
        Assert.Contains("401", status.LastError, StringComparison.Ordinal);
        Assert.Empty(await cloud.Store.ReadForSync(new SyncQuery(0, null, null, [], 100), None));
    }

    [Fact]
    public async Task RefusesEventsThatCameFromAnotherNode()
    {
        await using var cloud = await SyncCloud.Start();
        using var http = new HttpClient(cloud.CreateHandler()) { BaseAddress = new Uri("http://localhost/pulse/sync/") };
        using var request = new HttpRequestMessage(HttpMethod.Post, "push")
        {
            Content = new StringContent(
                $$"""{"events":[{"id":"{{Guid.NewGuid()}}","stream":"Sale-1","name":"SaleCompleted","data":{"number":1},"origin":"till-2","emittedAt":"2026-10-15T10:00:00Z","position":1}]}""",
                Encoding.UTF8,
                "application/json"
            )
        };
        request.Headers.Add("X-PulseSharp-Protocol", "1");
        request.Headers.Add("X-PulseSharp-Key", "key-1");

        using var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("may only send its own events", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(await cloud.Store.ReadForSync(new SyncQuery(0, null, null, [], 100), None));
    }

    [Fact]
    public async Task KeepsEventsWhileOfflineAndSendsThemLater()
    {
        await using var cloud = await SyncCloud.Start();
        var network = new Network(cloud.CreateHandler()) { Offline = true };
        await using var till = await SyncDevice.Start("till-1", network);

        await till.Pulse.Emit(new SaleCompleted(1));
        await till.Pulse.Emit(new SaleCompleted(2));
        var offline = await till.Sync.SyncNow();
        network.Offline = false;
        var online = await till.Sync.SyncNow();

        Assert.False(offline.Online);
        Assert.Contains("No network", offline.LastError, StringComparison.Ordinal);
        Assert.True(online.Online, online.LastError);
        Assert.Null(online.LastError);
        Assert.Equal(2, (await cloud.Store.ReadForSync(new SyncQuery(0, "till-1", null, [], 100), None)).Count);
    }

    [Fact]
    public async Task SendsALargeBacklogInBatches()
    {
        await using var cloud = await SyncCloud.Start();
        var network = new Network(cloud.CreateHandler());
        await using var till = await SyncDevice.Start("till-1", network, sync => sync.BatchSize = 50);
        await till.Sync.SyncNow();

        for (var number = 1; number <= 120; number++)
        {
            await till.Pulse.Emit(new SaleCompleted(number));
        }

        var pushesBefore = network.Pushes;
        await till.Sync.SyncNow();

        Assert.Equal(120, (await cloud.Store.ReadForSync(new SyncQuery(0, "till-1", null, [], 1000), None)).Count);
        Assert.Equal(3, network.Pushes - pushesBefore);
    }

    [Fact]
    public async Task RemembersWhatWasSentAcrossRestarts()
    {
        using var database = new TempDatabase();
        await using var cloud = await SyncCloud.Start();

        await using (var before = await SyncDevice.Start("till-1", cloud.CreateHandler(), sqlite: database.ConnectionString))
        {
            await before.Pulse.Emit(new SaleCompleted(1));
            await before.Pulse.Emit(new SaleCompleted(2));
            await before.Sync.SyncNow();
        }

        var network = new Network(cloud.CreateHandler());
        await using (var after = await SyncDevice.Start("till-1", network, sqlite: database.ConnectionString))
        {
            var status = await after.Sync.SyncNow();
            Assert.True(status.Online, status.LastError);
        }

        Assert.Equal(0, network.Pushes);
        Assert.Equal(2, (await cloud.Store.ReadForSync(new SyncQuery(0, "till-1", null, [], 100), None)).Count);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task RefusesAServerWithTheSameNodeName()
    {
        await using var cloud = await SyncCloud.Start(server => server.AllowNode("cloud", "key-1").PushAnyStream());
        await using var device = await SyncDevice.Start("cloud", cloud.CreateHandler());

        var status = await device.Sync.SyncNow();

        Assert.False(status.Online);
        Assert.Contains("both called 'cloud'", status.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncsASqliteTillWithAPostgreSqlCloud()
    {
        using var schema = TempSchema.CreateOrSkip();
        using var database = new TempDatabase();
        await using (var cloud = await SyncCloud.Start(useStore: pulse => pulse.UsePostgreSql(schema.ConnectionString, schema.Name)))
        {
            await using var till = await SyncDevice.Start("till-1", cloud.CreateHandler(), sqlite: database.ConnectionString);

            await till.Pulse.Emit("Sale-1", new SaleCompleted(1));
            await cloud.Pulse.Emit("Product-1", new ProductChanged("A"));
            var status = await till.Sync.SyncNow();

            Assert.True(status.Online, status.LastError);
            await Eventually.True(() => cloud.Sales.Count == 1, "the cloud handles the till's sale");
            await Eventually.True(() => till.Products.Count == 1, "the till handles the cloud's product");
            var sale = Assert.Single(await cloud.Store.ReadForSync(new SyncQuery(0, "till-1", null, [], 100), None));
            using var data = System.Text.Json.JsonDocument.Parse(sale.Data);
            Assert.Equal(1, data.RootElement.GetProperty("number").GetInt32());
        }

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void ExplainsMissingOrInvalidSyncSettings()
    {
        var noServer = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddPulseSharp(pulse =>
        {
            pulse.UseInMemory();
            pulse.UseSync(_ => { });
        }));
        var twice = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddPulseSharp(pulse =>
        {
            pulse.UseInMemory();
            pulse.UseSync(sync => (sync.Server, sync.ApiKey) = (new Uri("https://cloud.example.com/pulse/sync"), "key-1"));
            pulse.UseSync(sync => (sync.Server, sync.ApiKey) = (new Uri("https://cloud.example.com/pulse/sync"), "key-1"));
        }));
        var nobody = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddPulseSharp(pulse =>
        {
            pulse.UseInMemory();
            pulse.UseSyncServer(_ => { });
        }));

        Assert.Contains("server address", noServer.Message, StringComparison.Ordinal);
        Assert.Contains("added twice", twice.Message, StringComparison.Ordinal);
        Assert.Contains("AllowNode", nobody.Message, StringComparison.Ordinal);
    }

    private static async Task<string> Pull(HttpClient http, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "pull?after=0");
        request.Headers.Add("X-PulseSharp-Protocol", "1");
        request.Headers.Add("X-PulseSharp-Key", key);
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private sealed class SyncCloud : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly ConcurrentQueue<SaleCompleted> _sales;

        private SyncCloud(WebApplication app, ConcurrentQueue<SaleCompleted> sales)
        {
            _app = app;
            _sales = sales;
        }

        public IPulse Pulse
        {
            get
            {
                return _app.Services.GetRequiredService<IPulse>();
            }
        }

        public ISyncStore Store
        {
            get
            {
                return (ISyncStore)_app.Services.GetRequiredService<IPulseStore>();
            }
        }

        public IReadOnlyCollection<SaleCompleted> Sales
        {
            get
            {
                return _sales;
            }
        }

        public static async Task<SyncCloud> Start(Action<SyncServerOptions>? configure = null, Action<PulseSharpBuilder>? useStore = null)
        {
            var sales = new ConcurrentQueue<SaleCompleted>();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddPulseSharp(pulse =>
            {
                (useStore ?? (cloudPulse => cloudPulse.UseInMemory()))(pulse);
                pulse.Node = "cloud";
                pulse.Advanced.PollInterval = TimeSpan.FromMilliseconds(50);
                pulse.On<SaleCompleted>("sales", (sale, _, _) =>
                {
                    sales.Enqueue(sale);
                    return Task.CompletedTask;
                });
                pulse.UseSyncServer(configure ?? (server => server.AllowNode("till-1", "key-1").AllowNode("till-2", "key-2").PushAnyStream()));
            });

            var app = builder.Build();
            app.MapPulseSharpSync();
            await app.StartAsync();
            return new SyncCloud(app, sales);
        }

        public HttpMessageHandler CreateHandler()
        {
            return _app.GetTestServer().CreateHandler();
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private sealed class SyncDevice : IAsyncDisposable
    {
        private readonly IHost _host;
        private readonly ConcurrentQueue<ProductChanged> _products;

        private SyncDevice(IHost host, ConcurrentQueue<ProductChanged> products)
        {
            _host = host;
            _products = products;
        }

        public IPulse Pulse
        {
            get
            {
                return _host.Services.GetRequiredService<IPulse>();
            }
        }

        public IPulseSync Sync
        {
            get
            {
                return _host.Services.GetRequiredService<IPulseSync>();
            }
        }

        public ISyncStore Store
        {
            get
            {
                return (ISyncStore)_host.Services.GetRequiredService<IPulseStore>();
            }
        }

        public IReadOnlyCollection<ProductChanged> Products
        {
            get
            {
                return _products;
            }
        }

        public static async Task<SyncDevice> Start(
            string node,
            HttpMessageHandler handler,
            Action<SyncOptions>? configure = null,
            string? key = null,
            string? sqlite = null
        )
        {
            var products = new ConcurrentQueue<ProductChanged>();
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddPulseSharp(pulse =>
            {
                if (sqlite is null)
                {
                    pulse.UseInMemory();
                }
                else
                {
                    pulse.UseSqlite(sqlite);
                }

                pulse.Node = node;
                pulse.Advanced.PollInterval = TimeSpan.FromMilliseconds(50);
                pulse.On<ProductChanged>("products", (product, _, _) =>
                {
                    products.Enqueue(product);
                    return Task.CompletedTask;
                });
                pulse.UseSync(sync =>
                {
                    sync.Server = new Uri("http://localhost/pulse/sync");
                    sync.ApiKey = key ?? (node == "till-2" ? "key-2" : "key-1");
                    sync.HttpHandler = handler;
                    sync.Interval = TimeSpan.FromHours(1);
                    configure?.Invoke(sync);
                });
            });

            var host = builder.Build();
            await host.StartAsync();
            return new SyncDevice(host, products);
        }

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    private sealed class Network(HttpMessageHandler server) : DelegatingHandler(server)
    {
        private int _pushes;

        public bool Offline { get; set; }

        public int Pushes
        {
            get
            {
                return _pushes;
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Offline)
            {
                throw new HttpRequestException("No network.");
            }

            if (request.RequestUri!.AbsolutePath.EndsWith("/push", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _pushes);
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
