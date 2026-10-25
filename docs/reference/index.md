# API reference

The reference is generated from the XML documentation in the source code, so it always matches the published packages.

| Package | Contents |
|---|---|
| [PulseSharp.Abstractions](/reference/abstractions) | `IPulse`, handler interfaces, contexts, stream names, options, attributes. Reference it from libraries. |
| [PulseSharp](/reference/core) | `AddPulseSharp`, `PulseSharpBuilder`, `IPulseControl`, and the store contract (`IPulseStore`). Reference it from the app. |
| [PulseSharp.Sqlite](/reference/sqlite) | `UseSqlite` and `SqliteStore`. |
| [PulseSharp.PostgreSql](/reference/postgresql) | `UsePostgreSql` and `PostgreSqlStore`. |
| [PulseSharp.Sync](/reference/sync) | `UseSync`, `SyncOptions` and `IPulseSync`. |
| [PulseSharp.Sync.Server](/reference/sync-server) | `UseSyncServer`, `SyncServerOptions` and `MapPulseSharpSync`. |
| [PulseSharp.Testing](/reference/testing) | `FakePulse`, the checks (`PulseAssertions`), `handler.Handle(event)`, `FakeJobContext`, `PulseTestHost` and `UseInMemory`. Test projects only. |

Everyday types live in the `PulseSharp` namespace (including `UseSqlite`). `AddPulseSharp` is in `Microsoft.Extensions.DependencyInjection`, next to your other registrations. Types for store authors are in `PulseSharp.Stores`, and the store classes are in `PulseSharp.Sqlite` and `PulseSharp.PostgreSql`.
