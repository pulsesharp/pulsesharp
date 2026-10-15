# Changelog

All notable changes to PulseSharp are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

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

[Unreleased]: https://github.com/pulsesharp/pulsesharp/compare/v0.5.0-alpha...HEAD
[0.5.0-alpha]: https://github.com/pulsesharp/pulsesharp/compare/v0.4.0-alpha...v0.5.0-alpha
[0.4.0-alpha]: https://github.com/pulsesharp/pulsesharp/compare/v0.3.0-alpha...v0.4.0-alpha
[0.3.0-alpha]: https://github.com/pulsesharp/pulsesharp/compare/v0.2.0-alpha...v0.3.0-alpha
[0.2.0-alpha]: https://github.com/pulsesharp/pulsesharp/compare/v0.1.0-alpha...v0.2.0-alpha
[0.1.0-alpha]: https://github.com/pulsesharp/pulsesharp/compare/v0.0.1-alpha...v0.1.0-alpha
[0.0.1-alpha]: https://github.com/pulsesharp/pulsesharp/releases/tag/v0.0.1-alpha
