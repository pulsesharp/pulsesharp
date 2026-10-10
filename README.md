<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/brand/lockup-on-dark.svg">
    <img alt="PulseSharp" src="assets/brand/lockup-on-light.svg" height="72">
  </picture>
</p>

<p align="center"><strong>Lightweight events and jobs for .NET — from the device to the cloud.</strong></p>

---

> **Status: early development.** Nothing is published yet. The API below is the design target and may change before `1.0`.

## What is PulseSharp?

PulseSharp is a small .NET library for **durable events** and **background jobs**.

- **Events:** something happened (`SaleCompleted`). PulseSharp saves the event first, then delivers it to every handler, in order, with retries.
- **Jobs:** work to do (`ImportProducts`). PulseSharp saves the job, runs it once, and reports progress. Jobs can be retried and cancelled.

It works the same way in a **desktop app, a web app, a worker service or a console app**, and on the database you already have.

## A quick look

```csharp
services.AddPulseSharp(pulse => pulse.UseSqlite("Data Source=app.db"));

// Something happened
await pulse.Emit(new SaleStream(sale.Id), new SaleCompleted(sale.Id, sale.Total));

// React to it: inline…
pulse.On<SaleCompleted>(async (sale, ct) => await PrintReceipt(sale, ct));

// …or in a handler class
public sealed class UpdateStock(IStock stock) : IEventHandler<SaleCompleted>
{
    public async Task Handle(SaleCompleted sale, EventContext context, CancellationToken ct)
    {
        await stock.Decrease(sale.SaleId, ct);
    }
}

// Work to do
await pulse.Enqueue(new ImportProducts("products.csv"));
```

## Why PulseSharp?

- **Lightweight.** The contracts package has no dependencies. You add only the packages you use.
- **Any database.** In-memory, SQLite and PostgreSQL first. Other stores plug into a small contract.
- **Any app.** No ASP.NET Core in the core packages. It works with Native AOT.
- **Device to cloud.** Events are saved on the device and keep working offline. They sync to the server when the device is back online.
- **Honest guarantees.** Delivery is at-least-once, and every handler sees each stream in order. The docs say exactly what is guaranteed and what is not.

## Packages (planned)

| Package | What it adds |
|---|---|
| `PulseSharp.Abstractions` | Contracts for handlers and libraries. No dependencies. |
| `PulseSharp` | The engine, with an in-memory store built in |
| `PulseSharp.Sqlite` | SQLite store, for desktop and mobile apps |
| `PulseSharp.PostgreSql` | PostgreSQL store, for servers |
| `PulseSharp.Sync` | Device ↔ cloud sync |
| `PulseSharp.Monitor` | Optional web UI |

## Learn more

- [Vision](docs/vision.md): why PulseSharp exists and where it is going
- [Architecture](docs/architecture.md): concepts, packages and the store contract
- [Contributing](CONTRIBUTING.md)

## License

[MIT](LICENSE)
