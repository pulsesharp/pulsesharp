# Changelog

All notable changes to PulseSharp are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

Planned as `0.8.0-alpha`. One way to do each thing, before a real app is built on PulseSharp.

### Changed

- **Breaking:** one way to set PulseSharp up: `services.AddPulseSharp(pulse => pulse.UseSqlite("app.db"))`, or `UsePostgreSql(…)` or `UseStore(…)`. `AddPulseSharpSqlite`, `AddPulseSharpPostgreSql` and `AddPulseSharpInMemory` are removed. Sync is set up with `UseSync(sync => …)` only; `SyncWithCloud` is removed.
- **Breaking:** one way per kind of handler, all of them safe for trimming and Native AOT: `On<TEvent>(name, (event, context, ct) => …)`, `AddHandler<TEvent, THandler>()`, `OnJob<TJob>((job, context, ct) => …)` and `AddJobHandler<TJob, THandler>()`. Removed: `When`, `OnJob` without the context and the token, and `AddHandler<THandler>()` and `AddHandlersFromAssembly`, which found handlers by reflection. A class that handles several event types is added once per event type.
- **Breaking:** the in-memory store moved to `PulseSharp.Testing` (`pulse.UseInMemory()`), so an app can never lose its events by choosing it by mistake. `AddPulseSharp` without a store stops at startup and says how to choose one.
- `samples/ConsoleSample` and `samples/PosSync` keep their data in SQLite.

### Tested

- Upgrades: a SQLite and a PostgreSQL database made by the published `0.4.0-alpha` open with the current version. Handled events are not delivered again, a stream carries on at the next position, a stopped handler stays stopped until it is retried, a waiting job runs, and the newer tables are added.

## [0.7.0-alpha] - 2026-10-24

A round of hardening found by building a complete example shop (a cloud API and desktop tills), then a round of simplifying: much faster delivery, handlers and jobs that heal when stuck or after a crash, a limit on what devices may write, and events that can be saved together.

### Added

- `pulse.EmitAll(stream, events)`: saves several events to one stream in one commit, all or none, in the order given. It is also much faster than emitting one by one (on one machine, 5,000 events: 1,404 to 13,731 per second on SQLite, 411 to 14,190 on PostgreSQL, in groups of 100). Works with `EmitOptions` (the expected version and the transaction apply to the whole group) and in `FakePulse` and `PulseTestHost`. `IPulse` has two new methods, so a class that implements `IPulse` must add them.
- **Store authors:** `IPulseStore.AppendAll(events, expectedVersion, transaction)`, with conformance tests.
- `Advanced.HandlerTimeout` (5 minutes) and `Advanced.JobTimeout` (1 hour): a handler or job that takes longer is cancelled, and abandoned when it does not stop within 10 poll intervals. The attempt counts as failed ("took longer than ... raise Advanced.HandlerTimeout") and is retried as usual, so a hanging handler can no longer block its stream for good. `null` sets no limit.
- Sync server: `server.PushStreams(node => [...])` says which streams each device may write, and `server.PushAnyStream()` allows all. An event for another stream is refused (the server logs a warning, the device logs one too), the device's other events are saved, and a refused event never blocks the ones behind it.
- Sync status: `SyncStatus.Waiting` (how many events the server has not confirmed, also after a restart that starts offline) and `IPulseSync.StatusChanged`, so a screen needs no polling. **Store authors:** `ISyncStore.CountForSync`.
- Sync sends a saved event at once while online (a short wait gathers a burst), and events received from the server wake the handlers at once. While the server cannot be reached, sync still backs off.

### Changed

- **Breaking:** `UseSyncServer` must say which streams devices may write: `PushStreams(...)` or `PushAnyStream()`. The app stops at startup with a message when neither is there, so a device can never write into the server's own streams by accident.
- Faster recovery after a crash: another instance, or the restarted app, takes over a handler after 10 seconds (it was 30) and a running job after 30 seconds (it was 5 minutes). **Breaking:** `Advanced.HandlerLease` and `Advanced.JobLease` are gone. A lease is renewed every poll interval, so it now follows `PollInterval`: 10 poll intervals for a handler and 30 for a job, at least 10 and 30 seconds.
- Docs: the events guide explains that every stream keeps a cursor per handler for good, and shows a stream per till and day for high volumes. The sync guide says what the server checks. The stores guide explains that events are kept for good, like in event sourcing. The API reference no longer lists internal types.

### Faster

- A handler saves its progress in groups, one commit for all the streams it works on (after at most 100 handled events or 200 ms, at the end of every round, when an event fails, and when the app stops normally), instead of one commit per event. On one machine, with a handler that does nothing: a backlog in one stream goes from 1,339 to over 27,000 events per second on SQLite and from 465 to over 12,000 on PostgreSQL; 5,000 streams of one event each go from 1,377 to over 18,000 per second on SQLite and from 1,505 to about 4,800 on PostgreSQL. After a crash, the events handled in the last moments (at most 100 per handler) can be delivered again, which the at-least-once rule already allows.
- **Store authors:** `IPulseStore.SaveCursors(cursors, revision)` saves several cursors in one commit, with the same revision check as `SaveCursor`.
- `Advanced.MaxParallelStreams` is now 8 by default (it was 4).

### Fixed

- A handler could deliver events again after a retry, skip or rewind: when `IPulseControl` changed the handler at the moment an app instance picked it up, the instance could end up with fresh cursors tagged with an old revision, so its first progress save was refused and it started over. The revision is now checked again after the cursors are loaded.
- Sync: when the server refused a push, the device also stopped receiving, because the pull ran only after a successful push. Push and pull now run independently, so a problem in one direction no longer stops the other. The status still reports the error.
- Sync status: right after a successful sync, the status could briefly go back to the previous "offline" value, and `SyncNow` could return the result of a later background run instead of its own. Both are fixed.

### Tested

- Lost connections during sync: a lost answer to a push (also in the middle of a large backlog), a lost answer to a pull, and an error returned after the server saved the events. Nothing is lost and nothing is handled twice.
- A real app process killed in the middle of a handler and of a job, on SQLite and PostgreSQL: the next app carries on in order, loses nothing, and the killed job runs again as a new attempt.
- The PostgreSQL database taken away while the app runs (every connection cut, new ones refused for a while): the app carries on by itself when the database is back.

## [0.6.0-alpha] - 2026-10-20

Getting ready for 1.0: a consistent API, finished jobs cleaned up, instant wake-ups between PostgreSQL app instances, and the new `PulseSharp.Testing` package. Renames are listed old → new.

### Added

- `services.AddPulseSharpInMemory(pulse => …)`: the in-memory store in one call, like `AddPulseSharpSqlite` and `AddPulseSharpPostgreSql`.
- `IPulseControl.RetryFailed(handlerName = null)`: tries every failed stream again at once, for one handler or all, and returns how many.
- Finished jobs are cleaned up: jobs that succeeded or were cancelled are deleted 7 days after they finished (`pulse.Advanced.KeepFinishedJobs`, `null` keeps every job). Failed jobs are kept until retried. The app instance that runs jobs does it about once an hour.
- **Store authors:** `IPulseStore.DeleteFinishedJobs(finishedBefore, maxCount)`, with conformance tests.
- PostgreSQL wakes other app instances at once: every saved event, job, retry and control change sends a `NOTIFY` when its transaction commits (never for a rollback), and each instance listens on one extra connection, so handlers and jobs on other instances start right away instead of at the next poll. The listener reconnects on its own; polling stays as the safety net.
- **Store authors:** optional `INotifyingStore.ListenForChanges(changed, cancellationToken)` for stores that can tell other instances about new work.
- **`PulseSharp.Testing`**, a new package for test projects, for any test framework:
  - `FakePulse`: an `IPulse` that records what is emitted and enqueued (`Events`, `Jobs`), checks expected versions, and can fail on purpose (`ThrowOnEmit`, `ThrowOnEnqueue`). An open class you can derive from.
  - Checks as extension methods: `Emitted<T>()`, `Emitted<T>(stream)`, `Emitted(event)`, `Emitted<T>(e => …)`, `NotEmitted<T>()`, `Enqueued…`, `NotEnqueued<T>()`, `AllEmitted<T>()`, `AllEnqueued<T>()`. They return what they find, and a failing check shows everything that happened. Write your own on `IRecordedPulse`.
  - `handler.Handle(event)` and `jobHandler.Handle(job)`: call handlers without building a context; the job version returns a `FakeJobContext` with the reported progress.
  - `PulseTestHost`: the real engine in memory (or on any store) with your handlers and services; `Given(...)`, `WaitUntilIdle()` (no delays in tests) and `NothingFailed()`. The same checks work on it.

### Changed

- `UseSqlite` and `new SqliteStore(…)` accept a file name (`"app.db"`) as well as a connection string, like `AddPulseSharpSqlite`.
- **Breaking:** `PostgresStore` → `PostgreSqlStore`.
- **Breaking:** `JobInfo.Id` → `JobInfo.JobId`, and for store authors `StoredJob.Id` → `StoredJob.JobId`, so a job's id has one name everywhere.
- **Breaking (store authors):** `LeaseStatus` → `JobLeaseStatus`, next to `HandlerLeaseStatus`.
- **Breaking:** sync server `PullFilter(node => …)` → `PullStreams(node => …)`, the same word the device uses.
- **Breaking:** `UseSyncServer` moved to the `PulseSharp` namespace, with every other method you call on `pulse`. `MapPulseSharpSync` stays in `Microsoft.AspNetCore.Builder`; the `AddPulseSharp…` methods stay in `Microsoft.Extensions.DependencyInjection`.

## [0.5.0-alpha] - 2026-10-15

### Added

- A simpler way in, on top of the same engine:
  - `services.AddPulseSharpSqlite("app.db", pulse => …)` and `services.AddPulseSharpPostgreSql(connectionString, pulse => …)` set PulseSharp up in one call.
  - `pulse.When<TEvent>(event => …)` reacts to an event without a name, context or token. It is named `when-{EventName}`; a second `When` for the same event stops the app at startup and asks for a name.
  - `pulse.OnJob<TJob>(async job => …)` runs a job without a context or token.
  - `pulse.Emit(event)` saves an event to the stream named after its type. A plain string now works as a stream name: `pulse.Emit("Sale-" + id, sale)`.
  - `pulse.EnqueueIn(job, delay)` and `pulse.EnqueueAt(job, time)`.
  - One log line at startup: `PulseSharp is ready. Store: …, node: …, event handlers: …, job types: …`.
- **Device to cloud sync.**
  - `PulseSharp.Sync` (the device side, no dependencies beyond PulseSharp): `pulse.SyncWithCloud(server, apiKey)`, or `pulse.UseSync(sync => …)` with `PullStreams`, `Push`, `Pull`, `Interval`, `BatchSize`, `Authorize` and `HttpHandler`. Sends the events this node emitted and receives everyone else's, in resumable batches. Offline is normal: the wait grows up to a minute and sync catches up later. `IPulseSync` shows the status and syncs on request (`SyncNow`).
  - `PulseSharp.Sync.Server` (ASP.NET Core): `pulse.UseSyncServer(server => …)` with `AllowNode(node, key, pullStreams)`, `NodeFrom(user => …)` and `PullFilter(node => …)`, and `app.MapPulseSharpSync()`. A node may only send its own events and only receive the streams it is allowed; it never gets its own events back.
  - Protocol version 1 over HTTP and JSON (`info`, `push`, `pull`).
  - `samples/PosSync`: two tills sell while the cloud is down, sync when it comes back, and receive a price change and an oversold warning.
- `PulseSharpBuilder.Services`, so extension packages can register their services.
- Store contract for sync: `ISyncStore` (`AppendReplicated`, `ReadForSync`, `LoadSyncPosition`, `SaveSyncPosition`), `SyncQuery` and `SyncDirection`, implemented by the in-memory, SQLite and PostgreSQL stores. Replicated events keep their id, origin and time; an event already in the store is skipped. New conformance tests cover it.

### Changed

- **Breaking:** the rarely changed settings moved to `pulse.Advanced`: `PollInterval`, `MaxParallelStreams`, `MaxParallelJobs`, `JobLease`, `HandlerLease` and `JsonOptions`. `Node`, `MaxAttempts` and `RetryDelay` stay on `pulse`.
- The README on nuget.org is plain Markdown now (it showed raw HTML), with working links, and the packages' project website is the docs site.
- SQLite schema version 3 and PostgreSQL schema version 2 add a sync position table and an index on the event origin. They are applied on start.

## [0.4.0-alpha] - 2026-10-13

### Added

- `PulseSharp.PostgreSql`: `pulse.UsePostgreSql(connectionString, schema)` (schema `pulsesharp` by default). Tables are created and upgraded on start under an advisory lock; event and job data is stored as `jsonb`; your own `NpgsqlTransaction` saves data and events or jobs together. Depends only on Npgsql.
- Several app instances on one database: each handler is delivered by one instance at a time (a renewed lease, `HandlerLease`, default 30 seconds); a stopped instance hands over at once, a crashed one after the lease. Jobs are claimed with `FOR UPDATE SKIP LOCKED`.
- Store contract: `LeaseHandler`, `ReleaseHandler`, `ChangeCursors`, `ReadStream`, `HandlerLeaseStatus`, and `SaveCursor` with a revision.
- Conformance tests now run against PostgreSQL too (in CI with a PostgreSQL service), plus multi-instance tests on SQLite and PostgreSQL. A PostgreSQL benchmark.

### Changed

- `IPulseControl` changes (`Retry`, `Skip`, `Rewind`) go through the store and work from any app instance. They return when the change is stored; the instance holding the handler applies it within about one poll interval.
- PostgreSQL appends run one transaction at a time per schema, so readers never skip an event that commits late.

## [0.3.0-alpha] - 2026-10-11

### Added

- `PulseSharp.Sqlite`: `pulse.UseSqlite(connectionString)`. Events, cursors and jobs survive a restart; tables (`pulsesharp_*`) are created and upgraded on start; WAL mode; atomic job claiming; your own `SqliteTransaction` saves data and events or jobs together.
- The store contract is public: `IPulseStore` (with `Initialize`), `NewEvent`, `StoredEvent`, `NewJob`, `StoredJob`, `LeaseStatus`, and `PulseSharpBuilder.UseStore`.
- `PulseSharpBuilder.JsonOptions`: events and jobs are stored as JSON (System.Text.Json); set a source-generated resolver for Native AOT.
- `ExpectedVersion.Allows(currentPosition)` for store authors.
- A conformance test suite that runs against every store, and a SQLite benchmark.

### Changed

- Events and jobs are serialized by the engine before they reach any store, including the in-memory store. A type that can't be serialized now fails at `Emit` / `Enqueue`.
- Store cursors use the public `HandlerCursor` type.

### Fixed

- A graceful shutdown right after a handler finished could skip saving its cursor, so the event was delivered again after a restart.

## [0.2.0-alpha] - 2026-10-11

### Added

- Jobs: `IPulse.Enqueue` with an optional `RunAt`, `OnJob<TJob>` and `AddJobHandler<TJob, THandler>`, and `AddHandler` / `AddHandlersFromAssembly` also find `IJobHandler<TJob>` classes. A job type has exactly one handler.
- Job retries with a growing delay up to `MaxAttempts`, progress through `JobContext.Report`, cooperative cancel, `MaxParallelJobs` (default 4), and leases (`JobLease`, default 5 minutes) renewed by a heartbeat.
- `IPulseControl`: `GetJob`, `GetJobs`, `CancelJob` and `RetryJob`, with the public `JobInfo` and `JobState` types.
- The console sample exports a report as a job; a job benchmark.

### Changed

- `IPulse.Enqueue` no longer throws `NotSupportedException`.

## [0.1.0-alpha] - 2026-10-11

### Added

- `IPulseControl`: `GetCursors`, `Retry`, `Skip` and `Rewind` for handlers, with the public `HandlerCursor` and `CursorState` types.
- `AddHandlersFromAssembly(assembly)`: adds every public or internal handler class of an assembly.
- `MaxParallelStreams` (default 4): a handler works on several streams at the same time; each stream stays in order.
- A console sample and the first benchmarks.

## [0.0.1-alpha] - 2026-10-11

First preview. Events only, with the in-memory store.

### Added

- `PulseSharp.Abstractions`: `IPulse`, `IEventHandler<T>`, `EventContext`, `IJobHandler<T>`, `JobContext`, `StreamName`, `DailyStreamName`, `EmitOptions`, `ExpectedVersion`, `EmitResult`, `EnqueueOptions`, `ExpectedVersionMismatchException`, and the `[HandlerName]`, `[EventName]` and `[JobName]` attributes.
- `PulseSharp`: `AddPulseSharp`, the in-memory store, `On<T>` inline handlers, `AddHandler` for handler classes, in-order delivery per stream, retries with a growing delay, a per-stream stop after `MaxAttempts`, startup checks for duplicate names, and UUIDv7 event ids.
- Brand assets, build settings, CI and release workflows, and the vision and architecture documents.

### Not yet available

- `IPulse.Enqueue` throws `NotSupportedException`. Jobs arrive in 0.2.

[Unreleased]: https://github.com/pulsesharp/pulsesharp/compare/v0.7.0-alpha...HEAD
[0.7.0-alpha]: https://github.com/pulsesharp/pulsesharp/compare/v0.6.0-alpha...v0.7.0-alpha
[0.6.0-alpha]: https://github.com/pulsesharp/pulsesharp/compare/v0.5.0-alpha...v0.6.0-alpha
[0.5.0-alpha]: https://github.com/pulsesharp/pulsesharp/compare/v0.4.0-alpha...v0.5.0-alpha
[0.4.0-alpha]: https://github.com/pulsesharp/pulsesharp/compare/v0.3.0-alpha...v0.4.0-alpha
[0.3.0-alpha]: https://github.com/pulsesharp/pulsesharp/compare/v0.2.0-alpha...v0.3.0-alpha
[0.2.0-alpha]: https://github.com/pulsesharp/pulsesharp/compare/v0.1.0-alpha...v0.2.0-alpha
[0.1.0-alpha]: https://github.com/pulsesharp/pulsesharp/compare/v0.0.1-alpha...v0.1.0-alpha
[0.0.1-alpha]: https://github.com/pulsesharp/pulsesharp/releases/tag/v0.0.1-alpha
