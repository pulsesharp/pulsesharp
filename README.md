<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/brand/lockup-on-dark.svg">
    <img alt="PulseSharp" src="assets/brand/lockup-on-light.svg" height="72">
  </picture>
</p>

<p align="center"><strong>Lightweight events and jobs for .NET — from the device to the cloud.</strong></p>

---

> **Status: preview (`0.1`).** Events work with the in-memory store. Jobs, SQLite, PostgreSQL and sync are coming (see the [roadmap](docs/vision.md#roadmap)). The API may change before `1.0`.

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

// Setup: choose a store and add handlers. Every handler has a stable name.
builder.Services.AddPulseSharp(pulse =>
{
    pulse.UseInMemory();
    pulse.On<SaleCompleted>("print-receipt", async (sale, context, ct) => await PrintReceipt(sale, ct));
    pulse.AddHandler<SaleCompleted, UpdateStock>();
});

// Something happened: emit it from anywhere through IPulse
await pulse.Emit(new SaleStream(sale.Id), new SaleCompleted(sale.Id, sale.Total));

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
- **Handler discovery**: `pulse.AddHandlersFromAssembly(typeof(Program).Assembly)`, or explicit `AddHandler<TEvent, THandler>()` for trimming and Native AOT.

See [`samples/ConsoleSample`](samples/ConsoleSample/Program.cs): a printer runs out of paper, the receipts fail, and `IPulseControl` retries them.

## Performance

In-memory store, one machine, BenchmarkDotNet short run ([`benchmarks/`](benchmarks/PulseSharp.Benchmarks/EventBenchmarks.cs)):

| Per event | Time | Allocated |
|---|---|---|
| Emit (no handlers) | ~0.5 µs | 542 B |
| Emit + deliver to 1 handler | ~2.2 µs | 1.0 KB |
| Emit + deliver to 3 handlers | ~5.6 µs | 1.9 KB |

Numbers for the SQLite and PostgreSQL stores will follow with those stores.

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
| `PulseSharp.Sqlite` | SQLite store, for desktop and mobile apps | Planned (0.3) |
| `PulseSharp.PostgreSql` | PostgreSQL store, for servers | Planned (0.4) |
| `PulseSharp.Sync` | Device ↔ cloud sync | Planned (0.5) |
| `PulseSharp.Monitor` | Optional web UI | Planned (0.6) |

Jobs (`IPulse.Enqueue`) arrive in 0.2. In this preview, `Enqueue` throws `NotSupportedException`.

## Learn more

- [Vision](docs/vision.md): why PulseSharp exists and where it is going
- [Architecture](docs/architecture.md): concepts, packages and the store contract
- [Contributing](CONTRIBUTING.md)

## License

[MIT](LICENSE)
