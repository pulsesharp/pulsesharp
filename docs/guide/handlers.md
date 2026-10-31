# Handlers

A handler reacts to events. Every handler has a **stable name**: PulseSharp stores how far each handler has got, per stream, under that name (its *cursor*).

## Inline handlers

For a few lines of code, add the handler inline with `On`. It takes a name you choose, and gives your code the [event context](#the-event-context) and a cancellation token:

```csharp
builder.Services.AddPulseSharp(pulse =>
{
    pulse.UseSqlite("app.db");
    pulse.On<SaleCompleted>("sales-log", (sale, context, ct) =>
    {
        logger.LogInformation("Sale {Id}: {Total}", sale.SaleId, sale.Total);
        return Task.CompletedTask;
    });
});
```

## Handler classes

For real work - with dependencies from dependency injection - implement `IEventHandler<TEvent>`:

```csharp
public sealed class UpdateStock(IStockRepository stock) : IEventHandler<SaleCompleted>
{
    public async Task Handle(SaleCompleted sale, EventContext context, CancellationToken ct)
    {
        await stock.Decrease(sale.SaleId, ct);
    }
}
```

Register it:

```csharp
pulse.AddHandler<SaleCompleted, UpdateStock>();
```

A new instance is created in a new dependency-injection **scope** for every event, so scoped services (like a `DbContext`) work as expected.

### Handler names

The default name is the class name (`UpdateStock`). Pin it before you ever rename or move the class:

```csharp
[HandlerName("update-stock")]
public sealed class UpdateStock(IStockRepository stock) : IEventHandler<SaleCompleted> { … }
```

Two handlers with the same name stop the app at startup with a clear message.

### One class, several event types

A class can handle several event types. Add it once per event type; they share **one name and one order per stream**:

```csharp
[HandlerName("sale-ledger")]
public sealed class SaleLedger : IEventHandler<SaleCompleted>, IEventHandler<SaleRefunded>
{
    public Task Handle(SaleCompleted sale, EventContext context, CancellationToken ct) { … }
    public Task Handle(SaleRefunded refund, EventContext context, CancellationToken ct) { … }
}

pulse.AddHandler<SaleCompleted, SaleLedger>();
pulse.AddHandler<SaleRefunded, SaleLedger>();
```

In a stream with *completed → refunded → completed*, the ledger sees exactly that order.

## The event context

Every handler receives an `EventContext`:

| Property | Meaning |
|---|---|
| `EventId` | Globally unique id (UUIDv7). Use it to make side effects idempotent. |
| `EventName` | The stored event name |
| `Stream`, `StreamPosition` | Where the event is (position starts at 1) |
| `Origin` | The node that emitted it |
| `EmittedAt` | When it was emitted |
| `HandlerName` | The handler receiving it |
| `Attempt` | 1 on the first try, higher on retries |

## Ordering and parallelism

- Within **one stream**, a handler receives events **one at a time, in order**.
- A handler works on up to `Advanced.MaxParallelStreams` streams **at the same time** (default 8).
- Different handlers are completely independent. **Never rely on the order between handlers** of the same event.

```csharp
pulse.Advanced.MaxParallelStreams = 8;
```

A higher value helps handlers that wait on something, such as a web call or a slow query, because that many streams can wait at the same time. Each stream in progress may use a database connection, so keep `MaxParallelStreams` times the number of handlers below your connection pool size.

## A handler that takes too long

One handler call may take at most `Advanced.HandlerTimeout` (5 minutes by default). After that the handler's token is cancelled, the attempt counts as failed and is retried, so a handler that hangs never blocks its stream for good. Pass the `CancellationToken` on to everything your handler awaits. See [When a handler hangs](/guide/failures#when-a-handler-hangs).

## Write handlers that are safe to repeat

Delivery is **at-least-once**: after a crash at the wrong moment, a handler can see the same event again. Make handlers idempotent - for example, store `context.EventId` with the data you write and skip events you have already applied. See [Delivery guarantees](/concepts/delivery).
