# Architecture

> Design target for `0.x`. Names and signatures may change until `1.0`.

## Concepts

| Term | Meaning |
|---|---|
| **Event** | A plain C# record that describes something that happened. No base class and no marker interface. |
| **Stream** | A named, ordered list of events, such as `Sale-7f3a…` or `Homepage-2026-10-10`. Order is guaranteed inside a stream. |
| **Emit** | Saving an event to a stream: `pulse.Emit(stream, event)`. |
| **Handler** | Code that reacts to an event: `On<T>("name", …)` at setup, or a class that implements `IEventHandler<T>`. Each handler has a stable name. |
| **Cursor** | How far a handler has got in one stream. One cursor exists per handler and per stream. |
| **Job** | A plain C# record that describes work to do, handled by exactly one `IJobHandler<T>`. |
| **Store** | The package that keeps events, cursors and jobs in a database. |
| **Node** | One running copy of the app: a till, a phone, a server instance. |

### Events or jobs?

| If… | Use |
|---|---|
| something happened, and zero, one or many parts of the app may react | an **event** |
| specific work must be done once, maybe later, maybe with progress | a **job** |

They work together: a handler can enqueue a job.

## How delivery works

```
 wake-up hints: in-process signal · database notify · timer
                          │
                          ▼
 for each handler:
   1. read new events of the types it handles
   2. group them by stream; skip streams that are retrying or failed
   3. hold the handler's lease (one instance runs a handler at a time)
   4. call the handler (with a time limit), then move the cursor
   5. save the cursors of many streams together, in groups
   6. on error: retry with backoff, then mark the cursor failed
```

- **The timer always runs.** Notifications only make delivery faster, so delivery never depends on a notification arriving.
- **Order:** within one stream, events reach each handler in order, one at a time. Different streams are handled in parallel.
- **Isolation:** a failing event stops only its own stream, and only for that one handler.
- **Several instances:** a lease on each handler makes sure two servers never run the same handler at once. A lease expires if its owner dies (after 10 poll intervals), and another instance or the restarted app takes over.
- **Healing:** a handler or job that hangs is cancelled and abandoned after its time limit, and the attempt counts as failed and is retried.
- **Guarantee:** delivery is **at-least-once**. In normal running a handler sees each event once. After a crash at the wrong moment, it can see the same event again, so handlers should be safe to repeat.

### Operator control

`IPulseControl` lets an app or the Monitor **retry** a failed cursor, **skip** a bad event, or **rewind** a handler so that it handles everything again.

## Jobs

1. `Enqueue` saves the job, optionally with `runAt` for later.
2. A worker claims the next due job with a lease, and renews the lease while the job runs.
3. On success the job is finished. On error it is retried with backoff, then marked failed.
4. If the worker dies, the lease expires and another worker picks the job up again.
5. Progress (`Report(done, total)`) and cancel requests are stored, so any instance can read or set them.

## Built for sync

These rules hold from the first version, so that device ↔ cloud sync can ship without breaking changes:

1. Every event has a **global, time-ordered id** (UUIDv7). The receiver skips ids it already has.
2. Every event records its **origin node**. An event is never sent back to where it came from.
3. **Each stream has one writer node.** A sale stream is written by its till, a price stream by the cloud. Order is never ambiguous, so sync has no conflicts.
4. **Positions are local, ids are global.** Each node numbers its own log, and sync keeps a cursor per remote node.
5. **Business conflicts are handled by handlers, not by sync.** If two offline tills sell the last item, both sales happened, and a cloud handler reacts (for example by emitting `StockWentNegative`).

## The store contract

The engine contains all the logic. A store only saves and reads. A store implements:

| Part | Responsibility |
|---|---|
| Events | Emit one event or a group of events in one commit (with an expected-version check and an optional `DbTransaction`); read after a position, filtered by type; read one stream |
| Cursors | Load and save cursors, one or many in one commit; record failures; list status |
| Leases | Acquire, renew and release a lease with an expiry |
| Jobs | Enqueue; claim the next due job; progress; finish, fail or reschedule; cancel flag |
| Wake-up *(optional, `INotifyingStore`)* | Tell other instances about new data at once. The PostgreSQL store uses `LISTEN/NOTIFY`. Without it, the timer is used. |
| Schema | Create and upgrade its own tables, safely when several instances start at once |

A shared **conformance test suite** (`PulseSharp.Testing`) runs against every store, official or community. A store that passes it behaves like every other store.

### Transactional outbox

Saving your data and emitting the event must succeed or fail together:

```csharp
await using var tx = await connection.BeginTransactionAsync(ct);
await SaveSale(connection, tx, sale, ct);
await pulse.Emit(new SaleStream(sale.Id), new SaleCompleted(sale.Id, sale.Total), new EmitOptions { Transaction = tx }, ct);
await tx.CommitAsync(ct);
```

`transaction` is the ADO.NET `DbTransaction`, shared by SQLite, PostgreSQL, SQL Server and EF Core. Handlers see the event only after the commit.

## Packages

| Package | Depends on |
|---|---|
| `PulseSharp.Abstractions` | nothing |
| `PulseSharp` | `Microsoft.Extensions.*.Abstractions` only |
| `PulseSharp.Sqlite` | `Microsoft.Data.Sqlite` |
| `PulseSharp.PostgreSql` | `Npgsql` |
| `PulseSharp.Sync` | core; the server endpoint also needs ASP.NET Core |
| `PulseSharp.Monitor` | ASP.NET Core |
| `PulseSharp.Testing` | xUnit |

The target framework is .NET 8 or later. Reflection is never required: explicit registration always works, so trimming and Native AOT are supported.

## Observability

- `ILogger` throughout.
- `ActivitySource("PulseSharp")`: one trace span per emit, handler call and job run, linked to the caller's trace (OpenTelemetry).
- `Meter("PulseSharp")`: events emitted, handler duration, failures, lag and running jobs.
- Hooks such as `OnHandlerFailed`, so that you can send alerts with any tool. PulseSharp sends no email itself.
