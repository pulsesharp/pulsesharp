# Testing your app

`PulseSharp.Testing` keeps tests of code that uses PulseSharp plain and short: a fake `IPulse`, a few checks, and nothing else to learn. It works with any test framework (xUnit, NUnit, MSTest).

```bash
dotnet add package PulseSharp.Testing --prerelease
```

Add it to your **test** project only, with the same version as `PulseSharp`.

## Code that emits events or enqueues jobs

Give your code a `FakePulse`. It records what was emitted and enqueued, and runs nothing:

```csharp
[Fact]
public async Task CompletingASaleEmitsSaleCompleted()
{
    var pulse = new FakePulse();
    var checkout = new Checkout(pulse);

    await checkout.Complete(new Sale(7, 25m));

    var completed = pulse.Emitted<SaleCompleted>("Sale-7");
    Assert.Equal(25m, completed.Total);
    pulse.Enqueued(new SendReceipt(7));
}
```

## A handler

Create the handler with a `FakePulse` and your fakes, and call it with just the event:

```csharp
[Fact]
public async Task SellingTheLastBagWarnsAboutStock()
{
    var pulse = new FakePulse();
    var handler = new UpdateStock(new FakeStock(), pulse);

    await handler.Handle(new SaleCompleted(7, 25m));

    pulse.Emitted(new StockWentNegative("coffee"));
    pulse.NotEnqueued<RestockOrder>();
}
```

`handler.Handle(event)` fills in the context for you. Pass only what matters to the test: `handler.Handle(sale, stream: "Sale-7", attempt: 2)`.

A job handler returns the context, with the progress it reported:

```csharp
var context = await new ExportReportHandler().Handle(new ExportReport(today));

Assert.Equal((3, 3), context.LastProgress);
```

## Checks

| Check | Passes when |
|---|---|
| `pulse.Emitted<T>()` | exactly one `T` was emitted. Returns it |
| `pulse.Emitted<T>("Sale-7")` | exactly one `T` was emitted to that stream. Returns it |
| `pulse.Emitted(event)` | exactly one equal event was emitted. Records compare by value, classes by their JSON |
| `pulse.Emitted<T>(e => …)` | exactly one `T` matches. Returns it |
| `pulse.NotEmitted<T>()` | no `T` was emitted (optionally: to a stream) |
| `pulse.Enqueued<T>()`, `Enqueued(job)`, `Enqueued<T>(j => …)` | the same, for jobs |
| `pulse.NotEnqueued<T>()` | no `T` job was enqueued |
| `pulse.AllEmitted<T>()`, `pulse.AllEnqueued<T>()` | always. Return every match, for your own asserts |

The checks that find something return it, so you go on with your usual asserts. A failing check says what was expected and shows everything that happened:

```text
Expected one SaleCompleted where sale => sale.Total == 25m, but found none.

Emitted:
  SaleCompleted { Number = 7, Total = 20 } in stream Sale-7
Enqueued:
  SendReceipt { Number = 7 }
```

## Your own checks

The checks are extension methods on `IRecordedPulse`, which has two lists: `Events` and `Jobs`. Add your own the same way:

```csharp
public static class ShopChecks
{
    public static SaleCompleted SoldFor(this IRecordedPulse pulse, decimal total)
    {
        var sale = pulse.Emitted<SaleCompleted>();
        Assert.Equal(total, sale.Total);
        return sale;
    }
}
```

`FakePulse` is an ordinary class: derive from it to change how it behaves. To test what your code does when saving fails, set `pulse.ThrowOnEmit = new TimeoutException()`.

## The whole flow, with the real engine

To test several handlers working together, `PulseTestHost` runs the real engine in memory with your handlers and services. The same checks work on it:

```csharp
[Fact]
public async Task ASaleOfTheLastBagWarnsAboutStock()
{
    await using var pulse = await PulseTestHost.Start(
        pulse => pulse.AddHandler<SaleCompleted, UpdateStock>(),
        services => services.AddSingleton<IStock, FakeStock>());

    await pulse.Given("Sale-7", new SaleStarted(7));
    await pulse.Emit("Sale-7", new SaleCompleted(7, 25m));
    await pulse.WaitUntilIdle();

    pulse.Emitted(new StockWentNegative("coffee"));
    pulse.NothingFailed();
}
```

- **`Given(...)`** saves events and waits until the handlers are done with them: the starting state. They don't count in the checks afterwards, so the checks see only what the test caused.
- **`WaitUntilIdle()`** waits until every handler has caught up and no job is running or due. No `Task.Delay`, no flaky timing. If the engine is still busy after 10 seconds, it fails and says with what.
- **`NothingFailed()`** fails the test when a handler stopped on a failing event or a job failed, and shows the error. Without it, a failing handler could go unnoticed.

Retries are fast in the test host (10 ms, doubling), so a failing handler stops within a moment. `pulse.Services` gives you the app's services, for example to look at a fake afterwards. To test against a real database, choose the store in the setup, for example `pulse.UseSqlite("test.db")`.
