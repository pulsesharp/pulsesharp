# Changelog

All notable changes to PulseSharp are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

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

[Unreleased]: https://github.com/pulsesharp/pulsesharp/compare/v0.3.0-alpha...HEAD
[0.3.0-alpha]: https://github.com/pulsesharp/pulsesharp/compare/v0.2.0-alpha...v0.3.0-alpha
[0.2.0-alpha]: https://github.com/pulsesharp/pulsesharp/compare/v0.1.0-alpha...v0.2.0-alpha
[0.1.0-alpha]: https://github.com/pulsesharp/pulsesharp/compare/v0.0.1-alpha...v0.1.0-alpha
[0.0.1-alpha]: https://github.com/pulsesharp/pulsesharp/releases/tag/v0.0.1-alpha
