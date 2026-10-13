<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/brand/lockup-on-dark.svg">
    <img alt="PulseSharp" src="assets/brand/lockup-on-light.svg" height="72">
  </picture>
</p>

<p align="center"><strong>Lightweight events and jobs for .NET - from the device to the cloud.</strong></p>

---

> **Status: preview (`0.4`).** Events and jobs work with the in-memory, **SQLite** and **PostgreSQL** stores. Device-to-cloud sync is coming (see the [roadmap](https://pulsesharp.github.io/pulsesharp/concepts/vision#roadmap)). The API may change before `1.0`.

## What is PulseSharp?

PulseSharp is a small .NET library for **durable events** and **background jobs**.

- **Events:** something happened (`SaleCompleted`). PulseSharp saves the event first, then delivers it to every handler, in order, with retries.
- **Jobs:** work to do (`ImportProducts`). PulseSharp saves the job, runs it once, and reports progress. Jobs can be retried and cancelled.

It works the same way in a **desktop app, a web app, a worker service or a console app**, and on the database you already have.

## A quick look

```bash
dotnet add package PulseSharp --prerelease
```

```csharp
public sealed record SaleCompleted(Guid SaleId, decimal Total);

public sealed record SaleStream(Guid SaleId) : StreamName
{
    public override string Value()
    {
        return $"Sale-{SaleId}";
    }
}

// Setup: choose a store and add handlers. Every event handler has a stable name;
// every job type has exactly one handler.
builder.Services.AddPulseSharp(pulse =>
{
    pulse.UseInMemory();
    pulse.On<SaleCompleted>("print-receipt", async (sale, context, ct) => await PrintReceipt(sale, ct));
    pulse.AddHandler<SaleCompleted, UpdateStock>();
    pulse.OnJob<ExportReport>(async (job, context, ct) =>
    {
        for (var page = 1; page <= 10; page++)
        {
            await ExportPage(job, page, ct);
            await context.Report(page, 10, ct);
        }
    });
});

// Something happened: emit it from anywhere through IPulse
await pulse.Emit(new SaleStream(sale.Id), new SaleCompleted(sale.Id, sale.Total));

// Work to do: run it now, or later, with retries, progress and cancel
var jobId = await pulse.Enqueue(new ExportReport(today));
await pulse.Enqueue(new ExportReport(today), new EnqueueOptions { RunAt = tonight });

// A handler class, created through dependency injection for every event
public sealed class UpdateStock(IStock stock) : IEventHandler<SaleCompleted>
{
    public async Task Handle(SaleCompleted sale, EventContext context, CancellationToken ct)
    {
        await stock.Decrease(sale.SaleId, ct);
    }
}
```

PulseSharp runs as a hosted service, so it works in any app that uses the .NET Generic Host: web apps, worker services, and desktop apps that build a host.

## What you get today

- Events of one stream reach each handler **in order**. Different streams and different handlers are independent.
- A failing handler is **retried** with a growing delay. After `MaxAttempts` its stream stops **for that handler only**, and everything else keeps flowing.
- **Stable names**: duplicate handler names or event names stop the app at startup with a clear message.
- **Optimistic concurrency** with `EmitOptions.ExpectedVersion`.
- Every event gets a time-ordered, globally unique id (UUIDv7) and records the node that emitted it.
- **Several streams in parallel** per handler (`MaxParallelStreams`, default 4), still in order within each stream.
- **`IPulseControl`**: list the cursors of every handler, **retry** or **skip** a failed event, or **rewind** a handler to handle everything again.
- **Handler discovery**: `pulse.AddHandlersFromAssembly(typeof(Program).Assembly)`, or explicit `AddHandler<TEvent, THandler>()` / `AddJobHandler<TJob, THandler>()` for trimming and Native AOT.
- **SQLite store**: `pulse.UseSqlite("Data Source=app.db")` (package `PulseSharp.Sqlite`). Events, cursors and jobs survive a restart; your own `SqliteTransaction` saves data and events together.
- **PostgreSQL store**: `pulse.UsePostgreSql(connectionString)` (package `PulseSharp.PostgreSql`, schema `pulsesharp` by default). Run several app instances on one database: each handler is delivered by one instance at a time, jobs are claimed with `SKIP LOCKED`, and `IPulseControl` works from any instance.
- **Jobs**: `Enqueue` now or later (`RunAt`), one handler per job type, retries with a growing delay, progress (`context.Report`), cancel (the handler's token), `MaxParallelJobs` (default 4), and leases so a job left behind by a stopped app instance runs again. `IPulseControl` reads, lists, cancels and retries jobs.

See [`samples/ConsoleSample`](samples/ConsoleSample/Program.cs): a printer runs out of paper, the receipts fail, and `IPulseControl` retries them; then an end-of-day job reports its progress.

## Performance

In-memory store, one machine, BenchmarkDotNet short run ([`benchmarks/`](benchmarks/PulseSharp.Benchmarks/EventBenchmarks.cs)):

| Per event | Time | Allocated |
|---|---|---|
| Emit (no handlers) | ~0.5 µs | 542 B |
| Emit + deliver to 1 handler | ~2.2 µs | 1.0 KB |
| Emit + deliver to 3 handlers | ~5.6 µs | 1.9 KB |
| Enqueue + run a job | ~21 µs | 4.5 KB |
| Emit to a SQLite file (pooled, fully durable) | ~0.66 ms | 5.7 KB |
| Emit to PostgreSQL (one round trip, local Docker) | ~2.0 ms | 5.8 KB |

Numbers for the PostgreSQL store will follow with that store.

## Why PulseSharp?

- **Lightweight.** The contracts package has no dependencies. You add only the packages you use.
- **Any database.** In-memory, SQLite and PostgreSQL first. Other stores plug into a small contract.
- **Any app.** No ASP.NET Core in the core packages. It works with Native AOT.
- **Device to cloud.** Events are saved on the device and keep working offline. They sync to the server when the device is back online.
- **Honest guarantees.** Delivery is at-least-once, and every handler sees each stream in order. The docs say exactly what is guaranteed and what is not.

## Packages

| Package | What it adds | Status |
|---|---|---|
| `PulseSharp.Abstractions` | Contracts for handlers and libraries. No dependencies. | Preview |
| `PulseSharp` | The engine, with an in-memory store built in | Preview |
| `PulseSharp.Sqlite` | SQLite store, for desktop and mobile apps | Preview |
| `PulseSharp.PostgreSql` | PostgreSQL store, for servers and several app instances | Preview |
| `PulseSharp.Sync` | Device ↔ cloud sync | Planned (0.5) |
| `PulseSharp.Monitor` | Optional web UI | Planned (0.6) |


## Learn more

- 📖 **[Documentation](https://pulsesharp.github.io/pulsesharp/)**: guides, concepts and the API reference
- [Getting started](https://pulsesharp.github.io/pulsesharp/guide/getting-started): your first event and job in five minutes
- [Vision and roadmap](https://pulsesharp.github.io/pulsesharp/concepts/vision)
- [Contributing](CONTRIBUTING.md)

## License

[MIT](LICENSE)
