# Changelog

All notable changes to PulseSharp are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.0.1-alpha] - 2026-10-11

First preview. Events only, with the in-memory store.

### Added

- `PulseSharp.Abstractions`: `IPulse`, `IEventHandler<T>`, `EventContext`, `IJobHandler<T>`, `JobContext`, `StreamName`, `DailyStreamName`, `EmitOptions`, `ExpectedVersion`, `EmitResult`, `EnqueueOptions`, `ExpectedVersionMismatchException`, and the `[HandlerName]`, `[EventName]` and `[JobName]` attributes.
- `PulseSharp`: `AddPulseSharp`, the in-memory store, `On<T>` inline handlers, `AddHandler` for handler classes, in-order delivery per stream, retries with a growing delay, a per-stream stop after `MaxAttempts`, startup checks for duplicate names, and UUIDv7 event ids.
- Brand assets, build settings, CI and release workflows, and the vision and architecture documents.

### Not yet available

- `IPulse.Enqueue` throws `NotSupportedException`. Jobs arrive in 0.2.

[Unreleased]: https://github.com/pulsesharp/pulsesharp/compare/v0.0.1-alpha...HEAD
[0.0.1-alpha]: https://github.com/pulsesharp/pulsesharp/releases/tag/v0.0.1-alpha
