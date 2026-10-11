# Delivery guarantees

Messaging libraries often promise more than they can keep. Here is exactly what PulseSharp guarantees.

## Events

| Guarantee | Meaning |
|---|---|
| **Stored before `Emit` returns** | If `Emit` completed, the event is in the store. |
| **At-least-once delivery** | Every handler of the event type receives it. In normal running exactly once; after a crash at the wrong moment, possibly twice. |
| **Order per stream, per handler** | Within one stream, a handler receives events in stream order, one at a time. |
| **No order between streams** | Different streams are handled in parallel (`MaxParallelStreams`). |
| **No order between handlers** | Handlers of the same event are independent. Never rely on one running before another. |
| **Isolated failures** | A failing event stops only its stream, only for that handler, after `MaxAttempts`. |

### Why "at least once"?

A handler finishes its work, then PulseSharp moves the handler's cursor. If the process dies **between** those two steps, the event is delivered again after the restart. No library can avoid this without the handler's own data and the cursor sharing one transaction.

**What you do:** make handlers idempotent. Store `context.EventId` with the data you write, or use naturally repeatable writes (`UPDATE … SET status = 'shipped'`).

## Jobs

| Guarantee | Meaning |
|---|---|
| **Stored before `Enqueue` returns** | If `Enqueue` completed, the job is in the store. |
| **One handler** | Each job type has exactly one handler. |
| **At-least-once execution** | A job runs to success, failure, or cancellation. A job whose worker disappeared runs again after its lease expires. |
| **Not before `RunAt`** | A job never starts before `RunAt`; it starts within about one `PollInterval` after it. |

**What you do:** make job handlers safe to run again from the beginning.

## In-memory store

The in-memory store keeps everything in the process. When the app stops, events, cursors and jobs are gone. Use it for tests, samples and learning; durable stores arrive in 0.3 (SQLite) and 0.4 (PostgreSQL).
