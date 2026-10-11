# Configuration

Everything is configured in one place:

```csharp
builder.Services.AddPulseSharp(pulse =>
{
    pulse.UseInMemory();

    pulse.Node = "till-1";
    pulse.MaxAttempts = 5;
    pulse.RetryDelay = TimeSpan.FromSeconds(1);
    pulse.PollInterval = TimeSpan.FromSeconds(1);
    pulse.MaxParallelStreams = 4;
    pulse.MaxParallelJobs = 4;
    pulse.JobLease = TimeSpan.FromMinutes(5);

    pulse.AddHandlersFromAssembly(typeof(Program).Assembly);
});
```

## Settings

| Setting | Default | Meaning |
|---|---|---|
| `Node` | machine name | The name of this app instance, stored with every event it emits |
| `MaxAttempts` | `5` | Attempts per event (per handler) or job before it fails |
| `RetryDelay` | 1 second | Wait before the first retry; doubles each time, up to 5 minutes |
| `PollInterval` | 1 second | How often handlers and workers check for work when nothing wakes them. Also the heartbeat of running jobs |
| `MaxParallelStreams` | `4` | Streams one handler works on at the same time |
| `MaxParallelJobs` | `4` | Jobs this app instance runs at the same time |
| `JobLease` | 5 minutes | How long a running job stays claimed without a heartbeat. Must be longer than `PollInterval` |
| `JsonOptions` | web defaults | How events and jobs are stored as JSON. Set a source-generated resolver for Native AOT — see [Stores](/guide/stores#how-data-is-stored) |

## Stores

| Method | Store |
|---|---|
| `UseInMemory()` | Keeps everything in memory. Lost when the app stops — for tests, samples and learning |
| `UseSqlite(connectionString)` | SQLite file — durable, for desktop, mobile and small servers (`PulseSharp.Sqlite`). See [Stores](/guide/stores) |
| `UseStore(store)` | Your own `IPulseStore` implementation |
| `UsePostgreSql(...)` | *Coming in 0.4* |

## Handlers

| Method | Adds |
|---|---|
| `On<TEvent>(name, handler)` | An inline event handler with a stable name |
| `AddHandler<TEvent, THandler>()` | An event handler class (AOT safe) |
| `OnJob<TJob>(handler)` | The inline handler of a job type |
| `AddJobHandler<TJob, THandler>()` | The handler class of a job type (AOT safe) |
| `AddHandler<THandler>()` | Every event and job interface the class implements (reflection) |
| `AddHandlersFromAssembly(assembly)` | Every handler class in an assembly (reflection) |

## Startup checks

PulseSharp refuses to start — with a message that says what to fix — when:

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
