# Testing your app

`PulseSharp.Testing` makes code that uses PulseSharp easy to test: no database, no delays, and clear messages when something is wrong. It works with any test framework (xUnit, NUnit, MSTest).

```bash
dotnet add package PulseSharp.Testing --prerelease
```

Add it to your **test** project only. Use the same version as `PulseSharp`.

There are three ways to test, from small to big. Pick the smallest one that covers what you want to check.

## 1. Code that emits events or enqueues jobs

`RecordingPulse` is an `IPulse` that only records. Nothing runs, so the test is instant:

```csharp
var pulse = new RecordingPulse();
var checkout = new Checkout(pulse);

await checkout.Complete(new Sale(Guid.NewGuid(), 25m));

pulse.Emitted<SaleCompleted>().ShouldHaveOne(sale => sale.Total == 25m);
pulse.Enqueued<SendReceipt>().ShouldHaveOne();
pulse.Emitted<SaleRefunded>().ShouldHaveNone();
```

## 2. One handler class

Call the handler yourself. `TestEventContext.For(event)` fills in the context with sensible values:

```csharp
var stock = new FakeStock();
var handler = new UpdateStock(stock);

await handler.Handle(sale, TestEventContext.For(sale), CancellationToken.None);
```

Pass what matters to the test, for example a retry: `TestEventContext.For(sale, stream: "Sale-1", attempt: 2)`.

For a job handler, `TestJobContext` also records the progress it reports:

```csharp
var job = new ExportReport(today);
var context = TestJobContext.For(job);

await new ExportReportHandler().Handle(job, context, CancellationToken.None);

var progress = context.LastProgress;   // for example (3, 3)
```

## 3. The whole flow, with the real engine

`PulseTestHost` runs PulseSharp in memory with your handlers and services. `WaitUntilIdle()` waits until every handler and job is done, so there is no `Task.Delay` and no flaky timing:

```csharp
await using var pulse = await PulseTestHost.Start(
    pulse => pulse.AddHandler<SaleCompleted, UpdateStock>(),
    services => services.AddSingleton<IStock, FakeStock>());

await pulse.Given("Sale-1", new SaleStarted(1), new ItemAdded("coffee"));
await pulse.Emit("Sale-1", new SaleCompleted(1, 25m));
await pulse.WaitUntilIdle();

pulse.Emitted<StockWentNegative>().ShouldHaveOne(stock => stock.Sku == "coffee");
pulse.ShouldHaveNoFailures();
```

- **`Given(...)`** saves events and waits until the handlers have handled them: the starting state of the test. They don't count in `Emitted` and `Enqueued` afterwards, so those show only what the test caused. Without a stream, `Given(event1, event2)` uses the stream named after each event's type.
- **`WaitUntilIdle()`** waits until every handler has caught up, no job is running or due, and nothing waits for a retry. If the engine is still busy after 10 seconds, it fails and says with what, for example which handler is behind on which stream.
- **`ShouldHaveNoFailures()`** fails the test when a handler stopped on a failing event or a job failed, and shows the error. Without it, a failing handler would go unnoticed: the engine keeps running and the test might still pass.
- **`Services`** gives you the app's services, for example to look at a fake after the test. **`Control`** is the `IPulseControl` of the test host.

Retries are fast in the test host (10 ms, doubling), so a failing handler stops within a moment. To test against a real database, choose the store in the setup, for example `pulse.UseSqlite("test.db")`.

## Checks

`Emitted<TEvent>()` and `Enqueued<TJob>()` work the same on `RecordingPulse` and `PulseTestHost`:

| Check | Passes when |
|---|---|
| `ShouldHaveOne()` / `ShouldHaveOne(e => …)` | exactly one matches. Returns it |
| `ShouldHaveNone()` / `ShouldHaveNone(e => …)` | none matches |
| `ShouldHave(count)` / `ShouldHave(count, e => …)` | exactly `count` match. Returns them |
| `InStream("Sale-1")` (events only) | narrows the events to one stream, then check as above |

A failing check says what was expected and what happened:

```text
Expected one SaleCompleted where sale => sale.Total == 25m, but found none.
Emitted SaleCompleted:
  SaleCompleted { SaleId = 7f3a..., Total = 20 } in stream Sale-1
Other events: SaleRefunded (1).
```

The results are also plain lists, so your usual assertions work too, for example `Assert.Single(pulse.Emitted<SaleCompleted>())`.
