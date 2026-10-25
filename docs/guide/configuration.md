# Configuration

Everything is set up in one place: the store, the handlers and the settings. The defaults suit desktop apps, web apps and workers alike, so most apps only choose a store and add their handlers:

```csharp
builder.Services.AddPulseSharp(pulse =>
{
    pulse.UseSqlite("app.db");

    pulse.Node = "till-1";
    pulse.MaxAttempts = 5;
    pulse.RetryDelay = TimeSpan.FromSeconds(1);

    pulse.AddHandler<SaleCompleted, PrintReceipt>();
    pulse.AddJobHandler<ExportReport, ExportReportHandler>();
});
```

## Settings

| Setting | Default | Meaning |
|---|---|---|
| `Node` | machine name | The name of this app instance, stored with every event it emits |
| `MaxAttempts` | `5` | Attempts per event (per handler) or job before it fails |
| `RetryDelay` | 1 second | Wait before the first retry; doubles each time, up to 5 minutes |

### Advanced settings

You rarely need these. They live under `pulse.Advanced` so the everyday settings stay easy to find.

| Setting | Default | Meaning |
|---|---|---|
| `PollInterval` | 1 second | How often handlers and workers check for work when nothing wakes them. Also the heartbeat that renews the claims on handlers and running jobs: after a crash, a handler is taken over after 10 poll intervals (at least 10 seconds) and a job after 30 (at least 30 seconds) |
| `MaxParallelStreams` | `8` | Streams one handler works on at the same time |
| `MaxParallelJobs` | `4` | Jobs this app instance runs at the same time |
| `KeepFinishedJobs` | 7 days | How long to keep jobs that succeeded or were cancelled. Failed jobs are kept until retried. `null` keeps every job |
| `HandlerTimeout` | 5 minutes | How long one handler may take for one event. Then it is cancelled, and abandoned if it does not stop within 10 poll intervals. The attempt counts as failed and is retried. `null` sets no limit |
| `JobTimeout` | 1 hour | The same for one run of a job. `null` sets no limit |
| `JsonOptions` | web defaults | How events and jobs are stored as JSON. Set a source-generated resolver for Native AOT - see [Stores](/guide/stores#how-data-is-stored) |

## Stores

Choose one inside `AddPulseSharp(pulse => …)`:

| Method | Store |
|---|---|
| `UseSqlite("app.db")` | SQLite file (a file name or a full connection string) - durable, for desktop, mobile and small servers (`PulseSharp.Sqlite`). See [Stores](/guide/stores) |
| `UsePostgreSql(connectionString, schema)` | PostgreSQL, several app instances (`PulseSharp.PostgreSql`, schema `pulsesharp` by default). See [Stores](/guide/stores#postgresql) |
| `UseStore(store)` | Your own `IPulseStore` implementation |

For tests, `PulseSharp.Testing` adds `UseInMemory()`, a store that keeps everything in memory. See [Testing](/guide/testing).

## Handlers

| Method | Adds |
|---|---|
| `On<TEvent>(name, handler)` | An inline event handler: `async (sale, context, ct) => …`, with a name you choose |
| `AddHandler<TEvent, THandler>()` | An event handler class, created from dependency injection |
| `OnJob<TJob>(handler)` | The inline handler of a job type: `async (job, context, ct) => …` |
| `AddJobHandler<TJob, THandler>()` | The handler class of a job type |

All four work with trimming and Native AOT.

## Startup checks

When everything is fine, PulseSharp logs one line at startup, for example `PulseSharp is ready. Store: SqliteStore, node: till-1, event handlers: 2, job types: 1.`

It refuses to start - with a message that says what to fix - when:

- no store is chosen,
- two handlers share a name, or two event types or two job types share a name,
- a job type has two handlers,
- a setting is out of range,
- `AddPulseSharp` is called twice.

## Registered services

| Service | Use it to |
|---|---|
| `IPulse` | Emit events and enqueue jobs |
| `IPulseControl` | Read cursors and jobs; retry, skip, rewind, cancel |

Both are singletons; inject them anywhere.
