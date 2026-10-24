# Handlers

A handler reacts to events. Every handler has a **stable name**: PulseSharp stores how far each handler has got, per stream, under that name (its *cursor*).

## The short way: `When`

```csharp
builder.Services.AddPulseSharpSqlite("app.db", pulse =>
{
    pulse.When<SaleCompleted>(sale => Console.WriteLine($"Sold for {sale.Total}"));
    pulse.When<PriceChanged>(async change => await shop.UpdatePrice(change.Sku, change.NewPrice));
});
```

`When` names the handler for you (`when-SaleCompleted`), so one event type can have one `When` handler. To react to the same event in a second place, give that handler a name with `On`.

## Named inline handlers

`On` takes a name you choose, and gives your code the [event context](#the-event-context) and a cancellation token:

```csharp
builder.Services.AddPulseSharp(pulse =>
{
    pulse.UseInMemory();
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
pulse.AddHandler<SaleCompleted, UpdateStock>();   // explicit, trimming and Native AOT safe
pulse.AddHandler<UpdateStock>();                  // finds the handled event types by reflection
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

A class can handle several event types. They share **one name and one order per stream**:

```csharp
[HandlerName("sale-ledger")]
public sealed class SaleLedger : IEventHandler<SaleCompleted>, IEventHandler<SaleRefunded>
{
    public Task Handle(SaleCompleted sale, EventContext context, CancellationToken ct) { … }
    public Task Handle(SaleRefunded refund, EventContext context, CancellationToken ct) { … }
}
```

In a stream with *completed → refunded → completed*, the ledger sees exactly that order.

## Scanning an assembly

Register every public or internal handler class (events **and** jobs) of an assembly in one line:

```csharp
pulse.AddHandlersFromAssembly(typeof(Program).Assembly);
```

Abstract classes, generic definitions and private nested classes are skipped.

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

## Write handlers that are safe to repeat

Delivery is **at-least-once**: after a crash at the wrong moment, a handler can see the same event again. Make handlers idempotent - for example, store `context.EventId` with the data you write and skip events you have already applied. See [Delivery guarantees](/concepts/delivery).
