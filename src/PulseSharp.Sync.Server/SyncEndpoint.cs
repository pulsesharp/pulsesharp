using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using PulseSharp.Stores;

namespace PulseSharp.Sync.Server;

internal sealed class SyncEndpoint(SyncServerOptions options, ISyncStore store, Func<string> node, ILogger logger)
{
    private const int DefaultPullSize = 200;

    public Task Info(HttpContext context)
    {
        return context.Response.WriteAsJsonAsync(new ServerInfo(SyncProtocol.Version, node()), WireJson.Default.ServerInfo);
    }

    public async Task Push(HttpContext context)
    {
        var caller = Identify(context);
        if (caller.Refusal is not null)
        {
            await Refuse(context, caller.Status, caller.Refusal).ConfigureAwait(false);
            return;
        }

        PushRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(WireJson.Default.PushRequest, context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request?.Events is null)
        {
            await Refuse(context, StatusCodes.Status400BadRequest, "The request body is not a valid push request.").ConfigureAwait(false);
            return;
        }

        if (request.Events.Count > options.MaxBatchSize)
        {
            await Refuse(context, StatusCodes.Status400BadRequest, $"A request may carry at most {options.MaxBatchSize} events.").ConfigureAwait(false);
            return;
        }

        var allowedStreams = options.PushStreamsFor(caller.Node!);
        var accepted = new List<WireEvent>(request.Events.Count);
        var refusedStreams = new List<string>();
        foreach (var wire in request.Events)
        {
            if (string.IsNullOrWhiteSpace(wire.Stream) || string.IsNullOrWhiteSpace(wire.Name))
            {
                await Refuse(context, StatusCodes.Status400BadRequest, $"Event {wire.Id} has no stream or name.").ConfigureAwait(false);
                return;
            }

            if (!string.Equals(wire.Origin, caller.Node, StringComparison.Ordinal))
            {
                await Refuse(
                    context,
                    StatusCodes.Status403Forbidden,
                    $"Node '{caller.Node}' may only send its own events, but event {wire.Id} comes from '{wire.Origin}'."
                ).ConfigureAwait(false);
                return;
            }

            if (allowedStreams is null || allowedStreams.Any(prefix => wire.Stream.StartsWith(prefix, StringComparison.Ordinal)))
            {
                accepted.Add(wire);
            }
            else
            {
                refusedStreams.Add(wire.Stream);
            }
        }

        if (refusedStreams.Count > 0)
        {
            Log.PushRefused(logger, caller.Node!, refusedStreams.Count, refusedStreams[0], string.Join(", ", allowedStreams ?? []));
        }

        var saved = accepted.Count == 0
            ? []
            : await store.AppendReplicated(accepted.Select(wire => wire.ToNewEvent()).ToList(), context.RequestAborted).ConfigureAwait(false);

        await context.Response
            .WriteAsJsonAsync(new PushResponse(saved.Count, accepted.Count - saved.Count, refusedStreams.Count), WireJson.Default.PushResponse)
            .ConfigureAwait(false);
    }

    public async Task Pull(HttpContext context)
    {
        var caller = Identify(context);
        if (caller.Refusal is not null)
        {
            await Refuse(context, caller.Status, caller.Refusal).ConfigureAwait(false);
            return;
        }

        var query = context.Request.Query;
        if (!TryReadNumber(query["after"], 0, out var after) || after < 0 || !TryReadNumber(query["max"], DefaultPullSize, out var max) || max < 1)
        {
            await Refuse(context, StatusCodes.Status400BadRequest, "after must be 0 or more, and max 1 or more.").ConfigureAwait(false);
            return;
        }

        max = Math.Min(max, options.MaxBatchSize);
        var requested = query["stream"].Where(prefix => !string.IsNullOrWhiteSpace(prefix)).Select(prefix => prefix!).ToList();
        var allowed = options.PullStreamsFor(caller.Node!);

        var prefixes = requested;
        if (allowed is not null)
        {
            var refused = requested.FirstOrDefault(prefix => !allowed.Any(allowedPrefix => prefix.StartsWith(allowedPrefix, StringComparison.Ordinal)));
            if (refused is not null)
            {
                await Refuse(context, StatusCodes.Status403Forbidden, $"Node '{caller.Node}' may not receive streams starting with '{refused}'.").ConfigureAwait(false);
                return;
            }

            if (requested.Count == 0)
            {
                prefixes = [.. allowed];
            }
        }

        IReadOnlyList<StoredEvent> events = allowed is { Count: 0 }
            ? []
            : await store.ReadForSync(new SyncQuery(after, null, caller.Node, prefixes, (int)max), context.RequestAborted).ConfigureAwait(false);

        var next = events.Count > 0 ? events[^1].GlobalPosition : after;
        await context.Response
            .WriteAsJsonAsync(new PullResponse(events.Select(WireEvent.From).ToList(), next, events.Count == max), WireJson.Default.PullResponse)
            .ConfigureAwait(false);
    }

    private Caller Identify(HttpContext context)
    {
        var headers = context.Request.Headers;
        if (headers[SyncProtocol.ProtocolHeader] != SyncProtocol.Version.ToString(CultureInfo.InvariantCulture))
        {
            return Caller.Refused(StatusCodes.Status400BadRequest, $"This server speaks sync protocol {SyncProtocol.Version}. Use the same PulseSharp version on both sides.");
        }

        string? node;
        var key = headers[SyncProtocol.KeyHeader].ToString();
        if (key.Length > 0)
        {
            node = NodeWithKey(key);
            if (node is null)
            {
                return Caller.Refused(StatusCodes.Status401Unauthorized, "The sync key is not known to this server.");
            }
        }
        else
        {
            node = context.User.Identity?.IsAuthenticated == true ? options.NodeFromUser?.Invoke(context.User) : null;
            if (string.IsNullOrWhiteSpace(node))
            {
                return Caller.Refused(StatusCodes.Status401Unauthorized, "Send the node's sync key, or sign in as a node that may sync.");
            }
        }

        var claimed = headers[SyncProtocol.NodeHeader].ToString();
        if (claimed.Length > 0 && !string.Equals(claimed, node, StringComparison.Ordinal))
        {
            return Caller.Refused(StatusCodes.Status403Forbidden, $"These credentials belong to node '{node}', but the app calls itself '{claimed}'. Check pulse.Node.");
        }

        return new Caller(node, StatusCodes.Status200OK, null);
    }

    private string? NodeWithKey(string key)
    {
        var given = Encoding.UTF8.GetBytes(key);
        string? found = null;
        foreach (var allowed in options.Nodes)
        {
            if (CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(allowed.ApiKey)))
            {
                found = allowed.Node;
            }
        }

        return found;
    }

    private static bool TryReadNumber(string? text, long fallback, out long value)
    {
        if (string.IsNullOrEmpty(text))
        {
            value = fallback;
            return true;
        }

        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static Task Refuse(HttpContext context, int status, string reason)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync(reason, context.RequestAborted);
    }

    private sealed record Caller(string? Node, int Status, string? Refusal)
    {
        public static Caller Refused(int status, string reason)
        {
            return new Caller(null, status, reason);
        }
    }
}
