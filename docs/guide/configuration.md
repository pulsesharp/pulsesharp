# Configuration

Most apps need one line. The defaults suit desktop apps, web apps and workers alike:

```csharp
builder.Services.AddPulseSharpSqlite("app.db", pulse =>
{
    pulse.When<SaleCompleted>(sale => Console.WriteLine($"Sold for {sale.Total}"));
});
```

When you need more, everything is still in one place:

```csharp
builder.Services.AddPulseSharp(pulse =>
{
    pulse.UseSqlite("app.db");

    pulse.Node = "till-1";
    pulse.MaxAttempts = 5;
    pulse.RetryDelay = TimeSpan.FromSeconds(1);

    pulse.Advanced.MaxParallelStreams = 8;

    pulse.AddHandlersFromAssembly(typeof(Program).Assembly);
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

The one-line setups:

| Method | Store |
|---|---|
| `services.AddPulseSharpInMemory(…)` | In memory: lost when the app stops. For learning, tests and samples |
| `services.AddPulseSharpSqlite("app.db", …)` | SQLite file (`PulseSharp.Sqlite`). Takes a file name or a full connection string |
| `services.AddPulseSharpPostgreSql(connectionString, …)` | PostgreSQL in the `pulsesharp` schema (`PulseSharp.PostgreSql`) |

Inside `AddPulseSharp(pulse => …)`:

| Method | Store |
|---|---|
| `UseInMemory()` | Keeps everything in memory. Lost when the app stops - for tests, samples and learning |
| `UseSqlite("app.db")` | SQLite file (a file name or a full connection string) - durable, for desktop, mobile and small servers (`PulseSharp.Sqlite`). See [Stores](/guide/stores) |
| `UsePostgreSql(connectionString, schema)` | PostgreSQL, several app instances (`PulseSharp.PostgreSql`, schema `pulsesharp` by default). See [Stores](/guide/stores#postgresql) |
| `UseStore(store)` | Your own `IPulseStore` implementation |

## Handlers

| Method | Adds |
|---|---|
| `When<TEvent>(handler)` | The simplest event handler. Named `when-{EventName}`; one per event type |
| `On<TEvent>(name, handler)` | An inline event handler with a name you choose, the event context and a cancellation token |
| `AddHandler<TEvent, THandler>()` | An event handler class (AOT safe) |
| `OnJob<TJob>(handler)` | The inline handler of a job type. `async job => …`, or `async (job, context, ct) => …` for progress and cancel |
| `AddJobHandler<TJob, THandler>()` | The handler class of a job type (AOT safe) |
| `AddHandler<THandler>()` | Every event and job interface the class implements (reflection) |
| `AddHandlersFromAssembly(assembly)` | Every handler class in an assembly (reflection) |

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
