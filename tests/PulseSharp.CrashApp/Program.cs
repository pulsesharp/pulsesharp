using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PulseSharp;

// A small app that the crash tests start and then kill. It handles sales and runs import jobs, and gets stuck on
// purpose: on the sale number given and on every job. It writes what it does to the console, so the test knows when to kill it.
// Arguments: sqlite <file> <stuck sale number>, or postgres <connection string> <schema> <stuck sale number>.
var store = args[0];
var stuckOn = int.Parse(args[^1], System.Globalization.CultureInfo.InvariantCulture);

var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
builder.Services.AddPulseSharp(pulse =>
{
    if (store == "sqlite")
    {
        pulse.UseSqlite(args[1]);
    }
    else
    {
        pulse.UsePostgreSql(args[1], args[2]);
    }

    pulse.Node = "crash-app";
    pulse.Advanced.PollInterval = TimeSpan.FromMilliseconds(50);
    pulse.On<SaleCompleted>("recorder", async (sale, _, cancellationToken) =>
    {
        if (sale.Number == stuckOn)
        {
            Console.WriteLine($"stuck {sale.Number}");
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        Console.WriteLine($"handled {sale.Number}");
    });
    pulse.OnJob<ImportProducts>(async (job, context, cancellationToken) =>
    {
        Console.WriteLine($"job started {context.Attempt}");
        await Task.Delay(Timeout.Infinite, cancellationToken);
    });
});

await builder.Build().RunAsync();

internal sealed record SaleCompleted(int Number);

internal sealed record ImportProducts(string File);
