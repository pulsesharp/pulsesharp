# Getting started

In five minutes you will save events, react to them, and run a background job - in a plain console app.

## 1. Create the app

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download) or later.

```bash
dotnet new console -n PulseDemo
cd PulseDemo
dotnet add package PulseSharp.Sqlite --prerelease
dotnet add package Microsoft.Extensions.Hosting
```

`PulseSharp.Sqlite` keeps events and jobs in a small database file, so nothing is lost when the app stops. PulseSharp runs inside the [.NET Generic Host](https://learn.microsoft.com/dotnet/core/extensions/generic-host), which web apps, worker services and desktop apps can all use. That is why the demo adds `Microsoft.Extensions.Hosting`.

## 2. Write the app

Replace `Program.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PulseSharp;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddPulseSharpSqlite("demo.db", pulse =>
{
    pulse.When<SaleCompleted>(sale => Console.WriteLine($"Sale {sale.Number}: {sale.Total:C}"));

    pulse.OnJob<ExportReport>(async job =>
    {
        await Task.Delay(500);
        Console.WriteLine($"Report for {job.Day:yyyy-MM-dd} exported.");
    });
});

using var host = builder.Build();
await host.StartAsync();

var pulse = host.Services.GetRequiredService<IPulse>();

await pulse.Emit(new SaleCompleted(1, 12.50m));
await pulse.Emit(new SaleCompleted(2, 4.99m));
await pulse.Enqueue(new ExportReport(DateOnly.FromDateTime(DateTime.Today)));

await Task.Delay(1500);
await host.StopAsync();

public sealed record SaleCompleted(int Number, decimal Total);

public sealed record ExportReport(DateOnly Day);
```

What is going on:

- An **event** (`SaleCompleted`) is a plain record that says something happened. A **job** (`ExportReport`) is a plain record that says what work to do.
- `AddPulseSharpSqlite("demo.db", …)` sets everything up and keeps the data in `demo.db`.
- `When<SaleCompleted>(…)` runs your code for every sale, in the order the sales happened.
- `OnJob<ExportReport>(…)` is the code that does the job.
- `Emit` saves the event and returns at once. Your `When` code runs in the background.
- `Enqueue` saves the job. A worker runs it in the background.

## 3. Run it

```bash
dotnet run
```

```text
Sale 1: $12.50
Sale 2: $4.99
Report for 2026-10-14 exported.
```

Run it again. You see two sales, not four: PulseSharp remembers which events it has already handled, even after a restart.

## Next steps

- [Emitting events](/guide/events) - streams, expected versions, transactions
- [Handlers](/guide/handlers) - named handlers, handler classes, assembly scanning
- [Failures and control](/guide/failures) - retries, stopped streams, retry / skip / rewind
- [Background jobs](/guide/jobs) - delays, progress, cancel, leases
- A complete runnable sample lives in [`samples/ConsoleSample`](https://github.com/pulsesharp/pulsesharp/tree/main/samples/ConsoleSample).
