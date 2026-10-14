# Stores

A **store** is where PulseSharp keeps events, handler cursors and jobs. You choose one when you set up PulseSharp.

| Store | Package | Use it for |
|---|---|---|
| In-memory | `PulseSharp` (built in) | Tests, samples, learning. Everything is lost when the app stops. |
| SQLite | `PulseSharp.Sqlite` | Desktop, mobile and small server apps. Events and jobs survive a restart. |
| PostgreSQL | `PulseSharp.PostgreSql` | Servers, including several app instances sharing one database. |

## SQLite

```bash
dotnet add package PulseSharp.Sqlite --prerelease
```

```csharp
builder.Services.AddPulseSharp(pulse =>
{
    pulse.UseSqlite("Data Source=pos.db");
    pulse.AddHandlersFromAssembly(typeof(Program).Assembly);
});
```

That is all. On start, PulseSharp creates its tables (and upgrades them in later versions). Every table name starts with `pulsesharp_`, so they can live in the same database file as your own tables.

**What a restart looks like:** each handler continues from its saved cursor - events it already handled are not handled again - and jobs that were waiting, retrying or interrupted run as usual.

**Speed:** about 0.66 ms per emit on a normal SSD. Each emit is one fully durable write: a committed event survives even a power cut.

The connection string is a normal [Microsoft.Data.Sqlite connection string](https://learn.microsoft.com/dotnet/standard/data/sqlite/connection-strings). The store turns on SQLite's WAL mode for good read/write concurrency.

::: tip Where to put the file
For a desktop app, use a folder the user can write to, for example `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyApp", "pulse.db")`.
:::

### Saving your data and the event together

Pass your own `SqliteTransaction`, and the event (or job) is saved in it. Both are committed together - or both are rolled back.

```csharp
await using var connection = new SqliteConnection("Data Source=pos.db");
await connection.OpenAsync(ct);
await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

await SaveSale(connection, transaction, sale, ct);
await pulse.Emit(new SaleStream(sale.Id), new SaleCompleted(sale.Id, sale.Total), new EmitOptions { Transaction = transaction }, ct);

await transaction.CommitAsync(ct);
```

Handlers receive the event only after the commit. The transaction must belong to the same database file; a transaction from another provider throws `ArgumentException`.

## PostgreSQL

```bash
dotnet add package PulseSharp.PostgreSql --prerelease
```

```csharp
builder.Services.AddPulseSharp(pulse =>
{
    pulse.UsePostgreSql(builder.Configuration.GetConnectionString("Events")!);
    pulse.AddHandlersFromAssembly(typeof(Program).Assembly);
});
```

PulseSharp keeps its tables in their own schema, `pulsesharp` by default. Choose another name with the second argument, for example one schema per app sharing a database:

```csharp
pulse.UsePostgreSql(connectionString, schema: "billing_events");
```

On start, PulseSharp creates the schema and its tables, and upgrades them in later versions. The database user needs permission to create a schema, or create the schema yourself and grant the user rights on it. Event and job data is stored as `jsonb`, so you can read it with any SQL tool.

### Several app instances

Run as many instances of your app as you like against one database:

- **Events:** each handler is delivered by **one instance at a time**. That instance holds a lease on the handler and renews it every poll interval. When it stops, another instance takes over at once; when it crashes, after `Advanced.HandlerLease` (default 30 seconds). Different handlers can run on different instances.
- **Jobs:** every instance runs jobs. A job is claimed by exactly one instance (`FOR UPDATE SKIP LOCKED`), so instances never wait for each other.
- **Control:** `IPulseControl` works from any instance. A retry, skip or rewind is stored, and the instance that holds the handler applies it within about one poll interval.

### Saving your data and the event together

Pass your own `NpgsqlTransaction`; the event or job is saved in it and committed together with your data:

```csharp
await using var connection = await dataSource.OpenConnectionAsync(ct);
await using var transaction = await connection.BeginTransactionAsync(ct);

await SaveSale(connection, transaction, sale, ct);
await pulse.Emit(new SaleStream(sale.Id), new SaleCompleted(sale.Id, sale.Total), new EmitOptions { Transaction = transaction }, ct);

await transaction.CommitAsync(ct);
```

::: tip Keep emitting transactions short
To make sure no reader ever skips an event, appends to one schema run one transaction at a time. A transaction that emits an event holds that turn until it commits, so keep it short: do the slow work before you open it.
:::

## How data is stored

Events and jobs are stored as **JSON** (System.Text.Json, web defaults: camelCase). Additive changes to an event record - a new optional property - just work with old stored events.

To change the JSON settings, or for trimming and Native AOT, set `Advanced.JsonOptions`:

```csharp
[JsonSerializable(typeof(SaleCompleted))]
[JsonSerializable(typeof(ExportReport))]
internal partial class AppJson : JsonSerializerContext;

pulse.Advanced.JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    TypeInfoResolver = AppJson.Default
};
```

An event or job that can't be serialized fails at `Emit` / `Enqueue` - even with the in-memory store - so you find out in your tests, not in production.

## Writing your own store

Implement `PulseSharp.Stores.IPulseStore` and pass it to `UseStore`:

```csharp
pulse.UseStore(new MyDatabaseStore(connectionString));
```

A store only saves and reads: ordering, retries, leases and delivery live in the engine, and data arrives already serialized as bytes. The [API reference](/reference/core#ipulsestore) documents every method, including the rules for atomic job claiming and lease ownership.

::: info Conformance tests
PulseSharp runs one shared test suite against every official store. It will be published as `PulseSharp.Testing` so community stores can prove they behave the same way.
:::
