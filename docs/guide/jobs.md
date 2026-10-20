# Background jobs

A **job** is work to do: export a report, import a file, rebuild an index. PulseSharp saves the job, a worker runs it with **exactly one handler**, and you can follow its progress.

## Define a job

A job is a plain record:

```csharp
public sealed record ExportReport(DateOnly Day);
```

Its stored name is the class name, or pin it:

```csharp
[JobName("export-report")]
public sealed record ExportReport(DateOnly Day);
```

## Add the handler

The short way:

```csharp
pulse.OnJob<ExportReport>(async job => await Export(job.Day));
```

With progress and cancel (see below), take the context and the token too:

```csharp
pulse.OnJob<ExportReport>(async (job, context, ct) =>
{
    await Export(job.Day, ct);
});
```

Or a class with dependencies from dependency injection:

```csharp
public sealed class ExportReportHandler(IReportWriter writer) : IJobHandler<ExportReport>
{
    public async Task Handle(ExportReport job, JobContext context, CancellationToken ct)
    {
        await writer.Write(job.Day, ct);
    }
}

pulse.AddJobHandler<ExportReport, ExportReportHandler>();   // explicit, AOT safe
pulse.AddHandlersFromAssembly(typeof(Program).Assembly);    // or let scanning find it
```

A job type has **exactly one** handler. Registering a second one stops the app at startup.

## Enqueue

```csharp
var jobId = await pulse.Enqueue(new ExportReport(today), ct);
```

Run it later:

```csharp
await pulse.EnqueueIn(new SendReminder(customerId), TimeSpan.FromHours(1));
await pulse.EnqueueAt(new ExportReport(today), tonight);
```

Both are short forms of `Enqueue` with `EnqueueOptions`:

```csharp
await pulse.Enqueue(new ExportReport(today), new EnqueueOptions { RunAt = tonight }, ct);
```

`EnqueueOptions.Transaction` saves the job inside your database transaction, like [events](/guide/events#saving-data-and-the-event-together).

::: info Precision
Delayed and retrying jobs are picked up on the next check, so they start within about one `Advanced.PollInterval` (default 1 second) of `RunAt`.
:::

## Report progress

```csharp
pulse.OnJob<ImportProducts>(async (job, context, ct) =>
{
    var rows = await ReadRows(job.File, ct);
    for (var index = 0; index < rows.Count; index++)
    {
        await Import(rows[index], ct);
        if (index % 100 == 0)
        {
            await context.Report(index, rows.Count, ct);
        }
    }
});
```

Report every few items, not every item. `JobContext` also gives `JobId`, `JobName` and `Attempt`.

## Follow, cancel and retry jobs

```csharp
var job = await control.GetJob(jobId);
Console.WriteLine($"{job!.State} {job.Done}/{job.Total}");

var failed = await control.GetJobs(JobState.Failed);   // newest first, up to 100
await control.CancelJob(jobId);
await control.RetryJob(jobId);
```

| `JobState` | Meaning |
|---|---|
| `Pending` | Waiting to run, now or at `RunAt` |
| `Running` | A worker is running it |
| `Retrying` | The last attempt failed; it runs again at `RunAt` |
| `Succeeded` | Done |
| `Failed` | Failed on every attempt - `RetryJob` runs it again |
| `Canceled` | Cancelled - `RetryJob` runs it again |

**Cancel** stops a waiting job at once. For a running job, the handler's `CancellationToken` is cancelled within about one poll interval - pass the token to your async calls so the job stops cleanly.

## Failures and retries

A handler that throws is retried with the same growing delay as events, up to `MaxAttempts`, then the job is **Failed**. Make job handlers safe to run again: a retry or a recovered job starts from the beginning.

## Leases: jobs survive a stopped app

When a worker starts a job it takes a **lease** (`Advanced.JobLease`, default 5 minutes) and renews it every poll interval. If the app instance stops unexpectedly, the lease runs out and another worker - or the same app after a restart - picks the job up again. On a normal shutdown the job is handed back at once, and that attempt is not counted.

## Finished jobs are cleaned up

Jobs that **succeeded** or were **cancelled** are deleted 7 days after they finished, so the job table never grows forever. **Failed** jobs are kept until you retry them, so problems never disappear on their own. Change the time, or keep every job:

```csharp
pulse.Advanced.KeepFinishedJobs = TimeSpan.FromDays(30);
pulse.Advanced.KeepFinishedJobs = null;                     // keep every job
```

The app instance that runs jobs does the cleanup, about once an hour.

## Parallelism

```csharp
pulse.Advanced.MaxParallelJobs = 4;   // default 4 jobs at the same time per app instance
```

A job without a registered handler stays `Pending`, so one app instance can enqueue jobs that another instance runs (with a shared database store).
