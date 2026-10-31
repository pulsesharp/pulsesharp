using System.Collections.Concurrent;
using PulseSharp;
using PulseSharp.Sync;

const string CloudAddress = "http://127.0.0.1:5288";
var folder = Path.Combine(Path.GetTempPath(), "pulsesharp-possync");
if (Directory.Exists(folder))
{
    Directory.Delete(folder, recursive: true);
}

Directory.CreateDirectory(folder);

Console.WriteLine("PulseSharp sync: two tills and one cloud.");
Console.WriteLine();
Console.WriteLine("1. The internet is down. Both tills keep selling the last bag of coffee.");

using var till1 = await StartTill("till-1");
using var till2 = await StartTill("till-2");
await Sell(till1, "till-1", new SaleCompleted("till-1", "coffee", 1, 12.50m));
await Sell(till2, "till-2", new SaleCompleted("till-2", "coffee", 1, 12.50m));
await SyncNow(till1, "till-1");
await SyncNow(till2, "till-2");

Console.WriteLine();
Console.WriteLine("2. The internet is back. The tills send their sales to the cloud.");

await using var cloud = await StartCloud();
await SyncNow(till1, "till-1");
await SyncNow(till2, "till-2");
await Task.Delay(1500);

Console.WriteLine();
Console.WriteLine("3. The cloud changes a price. The tills receive it, and the oversold warning.");

await cloud.Services.GetRequiredService<IPulse>().Emit(new PriceChanged("tea", 4.20m));
await SyncNow(till1, "till-1");
await SyncNow(till2, "till-2");
await Task.Delay(1500);

Console.WriteLine();
Console.WriteLine($"Done. The tills' databases are in {folder}.");
await till1.StopAsync();
await till2.StopAsync();
await cloud.StopAsync();

async Task<IHost> StartTill(string name)
{
    var builder = Host.CreateApplicationBuilder();
    builder.Logging.ClearProviders();
    builder.Services.AddPulseSharp(pulse =>
    {
        pulse.UseSqlite(Path.Combine(folder, $"{name}.db"));
        pulse.Node = name;
        pulse.UseSync(sync =>
        {
            sync.Server = new Uri($"{CloudAddress}/pulse/sync");
            sync.ApiKey = $"{name}-secret";
        });

        pulse.On<PriceChanged>("prices", (price, _, _) =>
        {
            Console.WriteLine($"  [{name}] New price for {price.Sku}: {price.Price:0.00}");
            return Task.CompletedTask;
        });
        pulse.On<StockWentNegative>("stock-warnings", (stock, _, _) =>
        {
            Console.WriteLine($"  [{name}] Warning: {stock.Sku} was sold {-stock.Left} more time(s) than in stock");
            return Task.CompletedTask;
        });
    });

    var host = builder.Build();
    await host.StartAsync();
    return host;
}

async Task<WebApplication> StartCloud()
{
    var stock = new ConcurrentDictionary<string, int>(StringComparer.Ordinal) { ["coffee"] = 1, ["tea"] = 10 };
    WebApplication? app = null;

    var builder = WebApplication.CreateBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls(CloudAddress);
    builder.Services.AddPulseSharp(pulse =>
    {
        pulse.UseSqlite(Path.Combine(folder, "cloud.db"));
        pulse.Node = "cloud";
        pulse.UseSyncServer(server => server
            .AllowNode("till-1", "till-1-secret")
            .AllowNode("till-2", "till-2-secret")
            .PushStreams(node => [$"Sale-{node}-"]));

        pulse.On<SaleCompleted>("stock", async (sale, _, _) =>
        {
            var left = stock.AddOrUpdate(sale.Sku, -sale.Quantity, (_, current) => current - sale.Quantity);
            Console.WriteLine($"  [cloud]  {sale.Till} sold {sale.Quantity} x {sale.Sku} for {sale.Total:0.00} (stock left: {left})");
            if (left < 0)
            {
                await app!.Services.GetRequiredService<IPulse>().Emit(new StockWentNegative(sale.Sku, left));
            }
        });
    });

    app = builder.Build();
    app.MapPulseSharpSync();
    await app.StartAsync();
    Console.WriteLine($"  [cloud]  Listening on {CloudAddress}");
    return app;
}

static async Task Sell(IHost till, string name, SaleCompleted sale)
{
    await till.Services.GetRequiredService<IPulse>().Emit($"Sale-{name}-{Guid.NewGuid():N}", sale);
    Console.WriteLine($"  [{name}] Sold {sale.Quantity} x {sale.Sku} for {sale.Total:0.00}");
}

static async Task SyncNow(IHost till, string name)
{
    var status = await till.Services.GetRequiredService<IPulseSync>().SyncNow();
    Console.WriteLine(status.Online
        ? $"  [{name}] Synced with the cloud"
        : $"  [{name}] Offline, the sale is kept: {status.LastError}");
}

public sealed record SaleCompleted(string Till, string Sku, int Quantity, decimal Total);

public sealed record PriceChanged(string Sku, decimal Price);

public sealed record StockWentNegative(string Sku, int Left);
