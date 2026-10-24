# Device to cloud sync

A till, a tablet or a laptop must keep working when the internet is down. With sync, each device saves its events in its own database and works offline. When the server can be reached, the device sends its events up and receives the server's events. Nothing is lost, nothing arrives twice, and no event is sent back to where it came from.

## The device

```bash
dotnet add package PulseSharp.Sqlite --prerelease
dotnet add package PulseSharp.Sync --prerelease
```

```csharp
using PulseSharp;

builder.Services.AddPulseSharpSqlite("till.db", pulse =>
{
    pulse.Node = "till-1";
    pulse.SyncWithCloud("https://cloud.example.com/pulse/sync", builder.Configuration["SyncKey"]!);

    pulse.When<PriceChanged>(price => Console.WriteLine($"New price for {price.Sku}: {price.Price}"));
});
```

That is the whole setup. Every event the till emits goes to the server, and every event from the server (here `PriceChanged`) reaches the till's handlers.

## The server

```bash
dotnet add package PulseSharp.PostgreSql --prerelease
dotnet add package PulseSharp.Sync.Server --prerelease
```

```csharp
using PulseSharp;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPulseSharpPostgreSql(builder.Configuration.GetConnectionString("Events")!, pulse =>
{
    pulse.Node = "cloud";
    pulse.UseSyncServer(server => server
        .AllowNode("till-1", builder.Configuration["Tills:till-1"]!)
        .PushStreams(node => [$"Sale-{node}-"]));

    pulse.When<SaleCompleted>(sale => Console.WriteLine($"Till sale: {sale.Total}"));
});

var app = builder.Build();
app.MapPulseSharpSync();
app.Run();
```

`AllowNode` gives each device a name and a secret key. Keep the keys in configuration, not in source code.

`PushStreams` says which streams a device may write: here only streams that start with `Sale-` and its own name, such as `Sale-till-1-2026-10-24`. A device can never write into a stream that belongs to the server (a price list, say) or to another device. The server stops at startup when you leave this out, so this choice is never forgotten. If you trust every device completely, `server.PushAnyStream()` allows all streams.

::: tip Try it
[`samples/PosSync`](https://github.com/pulsesharp/pulsesharp/tree/main/samples/PosSync) runs two tills and a cloud in one console app: the tills sell while the cloud is down, catch up when it starts, and receive a price change and an oversold warning. Run it with `dotnet run --project samples/PosSync`.
:::

## How it works

- **Every app has a name**, `pulse.Node`. Each event remembers the node that emitted it.
- **A device sends only its own events**, and receives the events of every other node: the server's, and those of other tills that reached the server. The server refuses an event that claims to come from another node.
- **No duplicates.** Every event has a global id. An event that arrives twice (for example, when the connection drops in the middle) is saved once.
- **Offline is normal.** Events wait in the device's database. Sync runs every 5 seconds; while the server can't be reached it waits longer, up to a minute, and catches up when the connection is back. Your app never sees an error.
- **Handlers run where they are added.** A sale emitted on the till reaches the till's handlers at once, and the server's handlers after sync.

### One writer per stream

Each stream should be written by one node, so the order of its events is never in doubt. Give each device its own streams, for example `"Sale-" + saleId` with ids the device creates. When two devices change the same thing while offline (two tills sell the last item), that is a business rule, not a sync problem: let a handler on the server notice it and react, for example by emitting `StockWentNegative`.

### What the server checks

- **Who a device is.** Every request needs a known key (or a signed-in user, see below).
- **That events come from that device.** The server refuses a request with an event that claims to come from another node.
- **Which streams a device writes.** An event for a stream the device may not write is refused. The server saves the device's other events as usual, logs a warning that names the device and a stream, and answers with the number of refused events. The device logs a warning too, and does not send the refused events again, so one wrong event can never block the ones behind it. The refused events stay in the device's own database.

Events from devices are still *input*. When a handler on the server acts on them, treat them like any other input from outside: check the values, and when it matters where an event came from, check `EventContext.Origin`:

```csharp
pulse.On<SaleCompleted>("record-sale", async (sale, context, cancellationToken) =>
{
    if (sale.Till != context.Origin)
    {
        return;
    }

    await sales.Record(sale, cancellationToken);
});
```

Use HTTPS between devices and the server, and keep the keys secret.

## Sync status and "Sync now"

Inject `IPulseSync` to show the status or add a "Sync now" button:

```csharp
public sealed class SyncButton(IPulseSync sync)
{
    public async Task<string> Click()
    {
        var status = await sync.SyncNow();
        return status.Online ? $"Synced at {status.LastSyncedAt:t}" : $"Offline: {status.LastError}";
    }
}
```

`SyncNow` never throws because the server can't be reached; the status says what happened.

### How many events are waiting

`status.Waiting` is the number of events saved on this device that the server has not confirmed yet. It counts events saved while the app was offline, and events saved before the app was last started. A till can show "3 sales waiting" with it.

Instead of asking again and again, listen to `StatusChanged`. It is raised after every sync, and whenever saving or sending events changes `Waiting`:

```csharp
sync.StatusChanged += status =>
{
    // runs on a background thread: switch to your UI thread before touching the screen
    Console.WriteLine(status.Online ? $"Online, {status.Waiting} waiting" : $"Offline, {status.Waiting} waiting");
};
```

An exception in your handler is logged and never disturbs sync.

### Events leave at once

While the device is online, a saved event is sent within a moment (a short wait gathers a burst of events into one request), not at the next `Interval`. While the server cannot be reached, sync backs off as before and does not try again after every saved event. Events received from the server wake the handlers at once.

## Choosing what a device receives

By default a device receives every event it did not emit. To receive only some streams, list their name prefixes on the device:

```csharp
pulse.UseSync(sync =>
{
    sync.Server = new Uri("https://cloud.example.com/pulse/sync");
    sync.ApiKey = builder.Configuration["SyncKey"];
    sync.PullStreams.Add("Product-");
    sync.PullStreams.Add("Store-7-");
});
```

The server decides what each device **may** receive. Give the prefixes with the key, or with a rule for all nodes:

```csharp
pulse.UseSyncServer(server =>
{
    server.AllowNode("till-1", tillOneKey, "Product-", "Store-7-");
    server.AllowNode("till-2", tillTwoKey, "Product-", "Store-9-");
    server.PushStreams(node => [$"Sale-{node}-"]);
});
```

```csharp
pulse.UseSyncServer(server =>
{
    server.AllowNode("till-1", tillOneKey);
    server.PullStreams(node => ["Product-", $"Store-{StoreOf(node)}-"]);
    server.PushStreams(node => [$"Sale-{node}-"]);
});
```

A device that asks for a stream it may not receive is refused, and its status shows why.

## Device options

| Option | Default | Meaning |
|---|---|---|
| `Server` | required | The server's sync endpoint |
| `ApiKey` | none | The key the server gave this node |
| `Push` | `true` | Send this node's events to the server |
| `Pull` | `true` | Receive the server's events |
| `PullStreams` | empty (all) | Receive only streams that start with these prefixes |
| `Interval` | 5 seconds | How often to sync while online, even when nothing was saved (to receive the server's events) |
| `BatchSize` | 200 | Events per request; larger backlogs are sent in several requests |
| `Authorize` | none | Changes each request before it is sent, for example to add a bearer token |
| `HttpHandler` | none | The HTTP handler to use, for example with a proxy or client certificate |

`pulse.SyncWithCloud(server, apiKey)` is `UseSync` with `Server` and `ApiKey` set and the defaults for the rest.

## Signing devices in another way

If your app already signs devices in (for example with tokens), let the server turn the signed-in user into a node name, and protect the endpoint with your own policy:

```csharp
pulse.UseSyncServer(server => server
    .NodeFrom(user => user.FindFirst("device")?.Value)
    .PushStreams(node => [$"Sale-{node}-"]));

app.MapPulseSharpSync().RequireAuthorization("devices");
```

On the device, add the token with `sync.Authorize = async (request, ct) => request.Headers.Authorization = new("Bearer", await tokens.Get(ct));`.

## Good to know

- Run sync in **one app instance per node**. A server with several instances is fine: any instance can answer devices.
- Events received by sync reach handlers at once.
- Use the same PulseSharp version on the devices and the server. Both sides check the sync protocol version and explain a mismatch.
- The device and the server must have different node names. Sync refuses to start talking when they don't.
