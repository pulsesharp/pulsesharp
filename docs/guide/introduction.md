# Introduction

PulseSharp is a small .NET library for two things almost every app needs:

- **Events** - *something happened* (`SaleCompleted`, `OrderShipped`). PulseSharp saves the event first, then delivers it to every handler that cares about it.
- **Jobs** - *work to do* (`ImportProducts`, `ExportReport`). PulseSharp saves the job, runs it once with a single handler, and reports its progress.

## Why PulseSharp?

| | |
|---|---|
| **Lightweight** | `PulseSharp.Abstractions` has no dependencies. The engine uses only the `Microsoft.Extensions.*.Abstractions` packages. Everything else is opt-in. |
| **Durable** | An event or job is stored before `Emit` / `Enqueue` returns. Handlers that fail are retried; nothing is silently dropped. |
| **Ordered** | Events of one stream reach each handler in order. Different streams are handled in parallel. |
| **Isolated failures** | A failing event stops only its own stream, and only for that handler. Every other stream and handler keeps flowing. |
| **Controllable** | `IPulseControl` shows where every handler is and lets you retry, skip or rewind - and read, cancel or retry jobs. |
| **Any app** | Desktop, web, worker or console. No ASP.NET Core in the core packages. Works with trimming and Native AOT. |
| **Honest** | Delivery is *at-least-once*, and the docs say exactly what that means. See [Delivery guarantees](/concepts/delivery). |

## Events or jobs?

| If… | Use |
|---|---|
| something happened, and zero, one or many parts of your app may react | an **event** |
| specific work must be done once - maybe later, maybe with progress | a **job** |

They work together: an event handler can enqueue a job.

```csharp
public sealed class StartDailyExport(IPulse pulse) : IEventHandler<DayClosed>
{
    public async Task Handle(DayClosed day, EventContext context, CancellationToken ct)
    {
        await pulse.Enqueue(new ExportReport(day.Date), ct);
    }
}
```

## Packages

| Package | What it adds | Status |
|---|---|---|
| `PulseSharp.Abstractions` | Contracts for handlers and libraries. No dependencies. | Preview |
| `PulseSharp` | The engine, with an in-memory store built in | Preview |
| `PulseSharp.Sqlite` | SQLite store, for desktop and mobile apps | Preview |
| `PulseSharp.PostgreSql` | PostgreSQL store, for servers and several app instances | Preview |
| `PulseSharp.Sync` | Device ↔ cloud sync | Planned (0.5) |
| `PulseSharp.Monitor` | Optional web UI | Planned (0.6) |

::: tip Reference only the contracts from libraries
A class library that only *defines* events, jobs or handlers can reference `PulseSharp.Abstractions` alone. Only the app that runs them needs `PulseSharp`.
:::

Next: [Getting started](/guide/getting-started).
