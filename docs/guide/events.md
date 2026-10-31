# Emitting events

## Events

An event is a plain C# record (or class) that describes something that **happened**. Name it in the past tense.

```csharp
public sealed record SaleCompleted(Guid SaleId, decimal Total);
public sealed record PriceChanged(string Sku, decimal NewPrice);
```

There is no base class and no marker interface.

### Event names

PulseSharp stores the **event name**, not the .NET type name. By default the name is the class name (`SaleCompleted`). To rename or move the class later without breaking stored events, pin the name:

```csharp
[EventName("sale-completed")]
public sealed record SaleCompleted(Guid SaleId, decimal Total);
```

Two event types with the same name stop the app at startup with a clear message.

## Emit an event

Inject `IPulse` anywhere and call `Emit`:

```csharp
await pulse.Emit(new SaleCompleted(sale.Id, sale.Total));
```

That's all you need to start. When `Emit` returns, the event is saved, and every handler receives it in the background, in the order the events were emitted.

## Streams

Every event belongs to one **stream**: an ordered list of events about one thing. Events of one stream reach each handler **in order**; different streams are independent and handled in parallel.

When you don't choose a stream, PulseSharp uses one stream per event type (here `SaleCompleted`). That is always correct, but while one event keeps failing in a handler, the later events of that type wait behind it. When events are about separate things (one sale has nothing to do with another), give each thing its own stream with a plain string:

```csharp
await pulse.Emit("Sale-" + sale.Id, new SaleCompleted(sale.Id, sale.Total));
```

Now a problem with one sale never holds up the others, and different sales are handled in parallel.

### How many streams?

PulseSharp remembers how far each handler has got in each stream (its cursor) and keeps that for good. A stream per sale therefore leaves one small row per handler for every sale, forever, and the engine keeps the streams it has seen in memory while the app runs. That is fine for things you create now and then, like customers or documents. For a high volume of events, use a coarser stream, for example one per till and day:

```csharp
await pulse.Emit($"Sale-{till}-{DateTime.UtcNow:yyyy-MM-dd}", new SaleCompleted(sale.Id, sale.Total));
```

The trade-off: when a handler keeps failing on one sale, that till's later sales of the day wait behind it until you [retry or skip](/guide/failures) it. In the sample shop, 10,000 sales handled by 3 handlers left 30,001 cursor rows with a stream per sale, and 16 with a stream per till and day.

### Stream records

By convention a stream name is `{Category}-{Id}`. In a bigger app, create a small record per kind of stream so the name is built in one place:

```csharp
public sealed record SaleStream(Guid SaleId) : StreamName
{
    public override string Value()
    {
        return $"Sale-{SaleId}";
    }
}
```

For a one-off name, use `StreamName.From("Sale-42")`.

### Daily streams

Some events are about the whole app rather than one entity - settings, navigation, homepage changes. Put them in a stream that starts again every day, so it never grows forever:

```csharp
public sealed record SettingsStream(DateOnly Date) : DailyStreamName(Date)
{
    protected override string Category
    {
        get
        {
            return "Settings";
        }
    }
}

await pulse.Emit(new SettingsStream(DateOnly.FromDateTime(DateTime.UtcNow)), new SettingChanged("theme", "dark"));
// stream name: Settings-2026-10-11
```

## Several events at once

To save several events together, use `EmitAll`. They go into one stream, in the order you give, in one commit: all of them are saved, or none. It is also much faster than emitting them one by one, because the database commits once.

```csharp
await pulse.EmitAll("Menu", new ProductAdded("tea", 2.50m), new ProductAdded("coffee", 3.20m), new PriceChanged("tea", 2.80m));
```

The events may be of different types. With a list instead of separate arguments, you can add options. The expected version and the transaction apply to the whole group, and the result is the last event:

```csharp
var events = new List<object> { new ProductAdded("tea", 2.50m), new ProductAdded("coffee", 3.20m) };
var options = new EmitOptions { ExpectedVersion = ExpectedVersion.NoStream };

var result = await pulse.EmitAll("Menu", events, options);
// result.StreamPosition is 2: the position of the last event
```

A good use is putting the first data in place when an app starts: with `NoStream`, two app instances starting at the same time save it once, and a crash can never leave it half done.

## The emit result

`Emit` returns the event's id and position:

```csharp
public sealed class Checkout(IPulse pulse)
{
    public async Task Complete(Sale sale, CancellationToken ct)
    {
        var result = await pulse.Emit(new SaleStream(sale.Id), new SaleCompleted(sale.Id, sale.Total), ct);
        // result.EventId        - globally unique, time-ordered (UUIDv7)
        // result.StreamPosition - 1, 2, 3 … within the stream
    }
}
```

When `Emit` returns, the event is stored. Handlers receive it in the background.

## Expected version (optimistic concurrency)

When you decide something based on a stream you just read, make sure nobody changed it in the meantime:

```csharp
var options = new EmitOptions { ExpectedVersion = ExpectedVersion.Exactly(lastSeenPosition) };

try
{
    await pulse.Emit(new OrderStream(orderId), new OrderShipped(orderId), options, ct);
}
catch (ExpectedVersionMismatchException conflict)
{
    // conflict.Stream, conflict.Expected, conflict.ActualPosition - read again and retry
}
```

| `ExpectedVersion` | The emit succeeds when… |
|---|---|
| `Any` (default) | always |
| `NoStream` | the stream has no events yet |
| `StreamExists` | the stream has at least one event |
| `Exactly(n)` | the last event of the stream is at position `n` |

## Saving data and the event together

The classic bug: your data is saved, but the event is lost (or the other way around). `EmitOptions.Transaction` takes an ADO.NET `DbTransaction`, so a database store saves the event in **your** transaction - both succeed or both fail.

```csharp
await using var tx = await connection.BeginTransactionAsync(ct);
await SaveSale(connection, tx, sale, ct);
await pulse.Emit(new SaleStream(sale.Id), new SaleCompleted(sale.Id, sale.Total), new EmitOptions { Transaction = tx }, ct);
await tx.CommitAsync(ct);
```

::: info
The SQLite and PostgreSQL stores honour the transaction (see [Stores](/guide/stores#saving-your-data-and-the-event-together)); the in-memory test store ignores it.
:::

## Nodes

Every event records the **node** that emitted it - `EventContext.Origin`. Set a clear name per app instance:

```csharp
builder.Services.AddPulseSharp(pulse =>
{
    pulse.Node = "till-1";   // default: the machine name
});
```

Node names become important for device ↔ cloud sync.
