# Vision

## The problem

Many .NET apps need two things that sound simple:

1. **"When X happens, do Y, Z and W, and never lose any of it."** These are events.
2. **"Do this work in the background, and tell me how it is going."** These are jobs.

Today you can either write this yourself again and again, or adopt a large framework that expects a server, a message broker or one specific database.

Things get harder when the app runs **on a device**: a point-of-sale till, a clinic desktop, a warehouse scanner, a field-service laptop. The device must keep working when the internet is down, and the cloud must still receive everything that happened.

## What PulseSharp does

PulseSharp gives you **one small programming model** for events and jobs that works everywhere:

- in a desktop or mobile app, on SQLite;
- in a web app or worker, on PostgreSQL;
- in tests, in memory.

```csharp
pulse.On<SaleCompleted>("print-receipt", async (sale, context, ct) => await PrintReceipt(sale, ct));

await pulse.Emit(new SaleStream(id), new SaleCompleted(id, total));
```

Write a handler once and run it on any of them.

## The big idea: events travel from the device to the cloud

Events are **facts that never change**. "Sale 1043 completed at 14:02" is never edited later.

That makes them easy to synchronise. Two sides only need to **exchange the events the other side has not seen yet**. There is nothing to merge and no "which version wins". Synchronising *state* is hard; synchronising *events* is simple.

```
  Till 1 (SQLite)        Till 2 (SQLite)         Cloud (PostgreSQL)
  local handlers         local handlers          server handlers
       │                      │                        │
       └──── sales go up ──── sync over HTTP ──── prices come down ────┘
```

- A till emits `SaleCompleted` locally. Local handlers run at once (stock, receipt), even offline.
- When the till is online, it sends the events it has not sent yet. The cloud skips any event it already has.
- In the cloud, synced events run the server's handlers exactly like local events.
- It works in both directions: `PriceChanged` emitted in the cloud reaches every till of that store.

## Principles

1. **Lightweight.** Small packages, few dependencies, opt-in features.
2. **Any database, any app.** The engine depends on a small store contract, never on a specific database or web framework.
3. **Honest guarantees.** Delivery is at-least-once, and each handler sees each stream in order. We say exactly what is guaranteed.
4. **Plain words.** Emit, On, Enqueue, Stream, Cursor, Store, Sync, Monitor.
5. **Built for sync from day one.** Even before sync ships, every event has a global id and an origin node, so sync never needs a breaking change.

## Roadmap

| Version | Goal |
|---|---|
| 0.1 | Events engine with the in-memory store |
| 0.2 | Jobs: delayed, retried, with progress and cancel |
| 0.3 | SQLite store and a desktop sample |
| 0.4 | PostgreSQL store, transactional outbox, several app instances |
| 0.5 | Device ↔ cloud sync |
| 0.6 | API review and hardening before the stable release |
| 1.0 | Stable API: events, jobs, SQLite, PostgreSQL and sync |
| 1.x | Monitor (a web UI for streams, cursors, jobs and sync), SQL Server, recurring jobs |
