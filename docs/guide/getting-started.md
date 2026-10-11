# Getting started

In five minutes you will emit an event, handle it, and run a background job — in a plain console app.

## 1. Create the app

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download) or later.

```bash
dotnet new console -n PulseDemo
cd PulseDemo
dotnet add package PulseSharp --prerelease
dotnet add package Microsoft.Extensions.Hosting
```

PulseSharp runs inside the [.NET Generic Host](https://learn.microsoft.com/dotnet/core/extensions/generic-host), which web apps, worker services and desktop apps can all use. That is why the demo adds `Microsoft.Extensions.Hosting`.

## 2. Describe what happens

An **event** is a plain record. A **stream** groups the events of one thing — here, one sale.

```csharp
public sealed record SaleCompleted(int SaleNumber, decimal Total);

public sealed record SaleStream(int SaleNumber) : StreamName
{
    public override string Value()
    {
        return $"Sale-{SaleNumber}";
    }
}
```

A **job** is also a plain record:

```csharp
public sealed record ExportReport(DateOnly Day);
```

## 3. Set up PulseSharp

Replace `Program.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PulseSharp;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddPulseSharp(pulse =>
{
    pulse.UseInMemory();

    pulse.On<SaleCompleted>("sales-log", (sale, context, ct) =>
    {
        Console.WriteLine($"{context.Stream} #{context.StreamPosition}: {sale.Total:C}");
        return Task.CompletedTask;
    });

    pulse.OnJob<ExportReport>(async (job, context, ct) =>
    {
        for (var page = 1; page <= 3; page++)
        {
            await Task.Delay(200, ct);
            await context.Report(page, 3, ct);
        }

        Console.WriteLine($"Report for {job.Day:yyyy-MM-dd} exported.");
    });
});

using var host = builder.Build();
await host.StartAsync();

var pulse = host.Services.GetRequiredService<IPulse>();

await pulse.Emit(new SaleStream(1), new SaleCompleted(1, 12.50m));
await pulse.Emit(new SaleStream(2), new SaleCompleted(2, 4.99m));
await pulse.Enqueue(new ExportReport(DateOnly.FromDateTime(DateTime.Today)));

await Task.Delay(1500);
await host.StopAsync();
```

What is going on:

- `UseInMemory()` chooses where events and jobs are stored. The in-memory store is perfect for learning and tests; durable stores (SQLite, PostgreSQL) are coming.
- `On<SaleCompleted>("sales-log", …)` adds an event handler with the **stable name** `sales-log`. PulseSharp remembers how far each handler got under that name.
- `OnJob<ExportReport>(…)` adds the one handler for `ExportReport` jobs. `context.Report` saves progress that any part of the app can read.
- `Emit` saves the event and returns; delivery happens in the background, in order per stream.
- `Enqueue` saves the job; a worker runs it.

## 4. Run it

```bash
dotnet run
```

```text
Sale-1 #1: $12.50
Sale-2 #1: $4.99
Report for 2026-10-11 exported.
```

## Next steps

- [Emitting events](/guide/events) — streams, expected versions, transactions
- [Handlers](/guide/handlers) — handler classes, several event types, assembly scanning
- [Failures and control](/guide/failures) — retries, stopped streams, retry / skip / rewind
- [Background jobs](/guide/jobs) — delays, progress, cancel, leases
- A complete runnable sample lives in [`samples/ConsoleSample`](https://github.com/pulsesharp/pulsesharp/tree/main/samples/ConsoleSample).
