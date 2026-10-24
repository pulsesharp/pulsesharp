using System.Security.Claims;

namespace PulseSharp.Sync.Server;

/// <summary>
/// Who may sync with this server and what they may receive. Set them in <c>pulse.UseSyncServer(server => { ... })</c>.
/// </summary>
/// <remarks>
/// Every request must be from a known node: one with a key from <see cref="AllowNode"/>, or a signed-in user that
/// <see cref="NodeFrom"/> turns into a node name. A node may only send events it emitted itself. By default it
/// receives every event it did not emit; <see cref="AllowNode"/> and <see cref="PullStreams"/> narrow that down.
/// </remarks>
public sealed class SyncServerOptions
{
    private readonly Dictionary<string, AllowedNode> _nodes = new(StringComparer.Ordinal);

    internal IReadOnlyCollection<AllowedNode> Nodes
    {
        get
        {
            return _nodes.Values;
        }
    }

    internal Func<ClaimsPrincipal, string?>? NodeFromUser { get; private set; }

    internal Func<string, IReadOnlyList<string>>? AllowedStreams { get; private set; }

    /// <summary>
    /// The most events one request may carry. The default is 1000.
    /// </summary>
    public int MaxBatchSize { get; set; } = 1000;

    /// <summary>
    /// Lets a node sync with a key, for example a till that calls <c>pulse.SyncWithCloud(url, key)</c>.
    /// </summary>
    /// <param name="node">The node's name: the <c>pulse.Node</c> of that app.</param>
    /// <param name="apiKey">The node's secret key. Keep it out of source code, for example in configuration.</param>
    /// <param name="pullStreams">
    /// The stream name prefixes this node may receive, for example <c>Product-</c>. None means every event it did not emit.
    /// </param>
    /// <returns>These options.</returns>
    /// <example>
    /// <code>
    /// server.AllowNode("till-1", configuration["Tills:till-1"]!, "Product-", "Price-");
    /// </code>
    /// </example>
    public SyncServerOptions AllowNode(string node, string apiKey, params string[] pullStreams)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentNullException.ThrowIfNull(pullStreams);

        if (pullStreams.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("A stream prefix must not be empty.", nameof(pullStreams));
        }

        if (!_nodes.TryAdd(node, new AllowedNode(node, apiKey, pullStreams)))
        {
            throw new ArgumentException($"The node '{node}' is allowed twice.", nameof(node));
        }

        return this;
    }

    /// <summary>
    /// Lets signed-in users sync, for apps that already authenticate devices (for example with tokens).
    /// </summary>
    /// <param name="node">Returns the node name of a signed-in user, or <see langword="null"/> when the user may not sync.</param>
    /// <returns>These options.</returns>
    /// <example>
    /// <code>
    /// server.NodeFrom(user => user.FindFirst("device")?.Value);
    /// </code>
    /// </example>
    public SyncServerOptions NodeFrom(Func<ClaimsPrincipal, string?> node)
    {
        ArgumentNullException.ThrowIfNull(node);
        NodeFromUser = node;
        return this;
    }

    /// <summary>
    /// Limits what each node may receive, by stream name prefix. Used for nodes that <see cref="AllowNode"/> gave no
    /// prefixes.
    /// </summary>
    /// <param name="allowedStreams">Returns the prefixes a node may receive. An empty list means nothing.</param>
    /// <returns>These options.</returns>
    /// <example>
    /// <code>
    /// server.PullStreams(node => [$"Store-{StoreOf(node)}-", "Product-"]);
    /// </code>
    /// </example>
    public SyncServerOptions PullStreams(Func<string, IReadOnlyList<string>> allowedStreams)
    {
        ArgumentNullException.ThrowIfNull(allowedStreams);
        AllowedStreams = allowedStreams;
        return this;
    }

    /// <summary>
    /// Says which streams each device may write. A device that sends an event for any other stream has that event refused
    /// (the server logs a warning and the device logs one too); its other events are saved as usual. This keeps a device
    /// from writing into streams that belong to the server or to another device. The names are prefixes.
    /// </summary>
    /// <param name="allowedStreams">Gets a node's name and returns the stream name prefixes it may write.</param>
    /// <returns>These options.</returns>
    /// <example>
    /// <code>
    /// server.PushStreams(node => [$"Sale-{node}-"]);
    /// </code>
    /// </example>
    public SyncServerOptions PushStreams(Func<string, IReadOnlyList<string>> allowedStreams)
    {
        ArgumentNullException.ThrowIfNull(allowedStreams);
        AllowedPushStreams = allowedStreams;
        return this;
    }

    /// <summary>
    /// Lets every device write every stream. Only choose this when you trust all devices completely: a device could then
    /// write into streams that belong to the server, for example a price list.
    /// </summary>
    /// <returns>These options.</returns>
    public SyncServerOptions PushAnyStream()
    {
        AnyPushStream = true;
        return this;
    }

    internal bool PushIsConfigured
    {
        get
        {
            return AnyPushStream || AllowedPushStreams is not null;
        }
    }

    internal bool AnyPushStream { get; private set; }

    internal Func<string, IReadOnlyList<string>>? AllowedPushStreams { get; private set; }

    internal IReadOnlyList<string>? PushStreamsFor(string node)
    {
        return AnyPushStream ? null : AllowedPushStreams!(node);
    }

    internal IReadOnlyList<string>? PullStreamsFor(string node)
    {
        if (_nodes.TryGetValue(node, out var allowed) && allowed.PullStreams.Count > 0)
        {
            return allowed.PullStreams;
        }

        return AllowedStreams?.Invoke(node);
    }
}

internal sealed record AllowedNode(string Node, string ApiKey, IReadOnlyList<string> PullStreams);
