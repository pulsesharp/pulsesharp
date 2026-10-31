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

Many streams failed for the same reason? Retry all of them at once, for one handler or for every handler:

```csharp
var retried = await control.RetryFailed();                  // every handler
await control.RetryFailed("print-receipt");                 // one handler
```

### Skip

The event can never succeed (bad data)? Skip it - the handler continues with the next event:

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
`Retry`, `Skip` and `Rewind` return when the change is stored, and work from any app instance: the instance that holds the handler applies the change at once with PostgreSQL, and within about one poll interval with other stores. An unknown handler name throws `ArgumentException`; retrying a stream the handler has never seen, or skipping when nothing is waiting, throws `InvalidOperationException`.
:::

## When a handler hangs

A handler that never returns would hold up its stream for good, and PulseSharp would never notice. So every handler has a time limit, `Advanced.HandlerTimeout` (5 minutes by default). When the time is up, the handler's cancellation token is cancelled. If the handler has not stopped 10 poll intervals later (10 seconds by default), PulseSharp stops waiting for it. The attempt counts as failed, with a message that says so, and the usual retries follow. If the next attempt works, the stream goes on by itself.

```csharp
builder.Services.AddPulseSharp(pulse =>
{
    pulse.UseSqlite("app.db");
    pulse.Advanced.HandlerTimeout = TimeSpan.FromMinutes(1);
});
```

Jobs work the same way with `Advanced.JobTimeout` (1 hour by default). Set either to `null` for no limit. Pass the `CancellationToken` on to everything your handler awaits, so it can stop in time. A handler that ignores the token is abandoned, but its code may still be running in the background.

## When an app stops suddenly

One app instance runs a handler at a time. It holds a short claim on the handler (a lease) and renews it while it runs. When the app stops normally, it hands its handlers over at once. When the app is killed or the machine loses power, the claim is still there, and the restarted app (or another instance) waits until it runs out: 10 seconds by default (10 poll intervals). Nothing is lost. The events wait and then catch up in order.

## Hooks for alerts

PulseSharp sends no e-mail and has no built-in alerting - on purpose. Connect your own: the log events (`PulseSharp` category, event ids `1001` retry, `1002` stopped, `2001`/`2002` jobs) work with any logging or monitoring tool, and `GetCursors()` / `GetJobs(JobState.Failed)` make a simple health check.
