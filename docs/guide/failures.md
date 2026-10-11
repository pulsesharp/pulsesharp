# Failures and control

## What happens when a handler throws

1. PulseSharp records the failure and **retries** the same event after a delay.
2. Each retry waits twice as long: `RetryDelay` × 2<sup>attempt−1</sup>, at most 5 minutes.
3. After `MaxAttempts` attempts, the cursor of that handler in that stream becomes **Failed** and the stream **stops for that handler only**.

Everything else keeps flowing: other streams of the same handler, and every other handler.

```csharp
builder.Services.AddPulseSharp(pulse =>
{
    pulse.MaxAttempts = 5;                       // default 5
    pulse.RetryDelay = TimeSpan.FromSeconds(1);  // default 1 second
});
```

Failures are logged through `ILogger` (category `PulseSharp`): a warning per retry and an error when a stream stops.

## IPulseControl

Inject `IPulseControl` to see and steer handlers while the app runs.

### See where every handler is

```csharp
foreach (var cursor in await control.GetCursors())
{
    Console.WriteLine($"{cursor.HandlerName} {cursor.Stream}: {cursor.State} at {cursor.Position}");
}
```

Each `HandlerCursor` has `Position` (last finished event), `State` (`Active`, `Retrying`, `Failed`), `Attempts`, `LastError` and `RetryAt`. Pass a handler name to see only that handler: `GetCursors("print-receipt")`.

### Retry

Fixed the cause (the printer has paper again)? Try the stuck event again now, with a fresh attempt count:

```csharp
await control.Retry("print-receipt", "Sale-42");
```

### Skip

The event can never succeed (bad data)? Skip it — the handler continues with the next event:

```csharp
await control.Skip("print-receipt", "Sale-42");
```

### Rewind

Fixed a bug in a handler and need it to process everything again, or need to rebuild data it writes? Rewind it to the start:

```csharp
await control.Rewind("sales-report");
```

The handler handles every stored event again, in order per stream.

::: tip
`Retry`, `Skip` and `Rewind` return when the handler has applied the request. An unknown handler name throws `ArgumentException`; retrying a stream the handler has never seen, or skipping when nothing is waiting, throws `InvalidOperationException`.
:::

## Hooks for alerts

PulseSharp sends no e-mail and has no built-in alerting — on purpose. Connect your own: the log events (`PulseSharp` category, event ids `1001` retry, `1002` stopped, `2001`/`2002` jobs) work with any logging or monitoring tool, and `GetCursors()` / `GetJobs(JobState.Failed)` make a simple health check.
