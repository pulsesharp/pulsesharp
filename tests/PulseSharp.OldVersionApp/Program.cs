using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PulseSharp;

// A small app built on an older, published PulseSharp (see the .csproj). The upgrade tests run it to make a database the
// way that version does, then open the database with the current code. It leaves behind: three sales the "recorder"
// handler has handled, a "printer" handler stopped on the first sale, and an import job that has not run yet.
// Arguments: sqlite <connection string>, or postgres <connection string> <schema>.
var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
builder.Services.AddPulseSharp(pulse =>
{
    if (args[0] == "sqlite")
    {
        pulse.UseSqlite(args[1]);
    }
    else
    {
        pulse.UsePostgreSql(args[1], args[2]);
    }

    pulse.Node = "old-app";
    pulse.MaxAttempts = 1;
    pulse.On<SaleCompleted>("recorder", (_, _, _) => Task.CompletedTask);
    pulse.On<SaleCompleted>("printer", (_, _, _) => throw new InvalidOperationException("The printer is out of paper."));
});

using var host = builder.Build();
await host.StartAsync();
var pulse = host.Services.GetRequiredService<IPulse>();
var control = host.Services.GetRequiredService<IPulseControl>();

for (var number = 1; number <= 3; number++)
{
    await pulse.Emit(StreamName.From("Sale-A"), new SaleCompleted(number));
}

await pulse.Enqueue(new ImportProducts("prices.csv"));

var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
while (true)
{
    var cursors = await control.GetCursors();
    if (cursors.Any(cursor => cursor.HandlerName == "recorder" && cursor.Position == 3)
        && cursors.Any(cursor => cursor.HandlerName == "printer" && cursor.State == CursorState.Failed))
    {
        break;
    }

    if (DateTimeOffset.UtcNow > deadline)
    {
        Console.WriteLine("timed out");
        return 1;
    }

    await Task.Delay(100);
}

await host.StopAsync();
Console.WriteLine("done");
return 0;

internal sealed record SaleCompleted(int Number);

internal sealed record ImportProducts(string File);
