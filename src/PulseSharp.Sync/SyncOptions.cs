namespace PulseSharp.Sync;

/// <summary>
/// How this app syncs its events with a server. Set them in <c>pulse.UseSync(sync => { ... })</c>.
/// </summary>
public sealed class SyncOptions
{
    /// <summary>
    /// The address of the server's sync endpoint, for example <c>https://cloud.example.com/pulse/sync</c>. Required.
    /// </summary>
    public Uri? Server { get; set; }

    /// <summary>
    /// The key the server gave this node, sent with every request. Leave it empty when <see cref="Authorize"/> signs
    /// requests another way.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Whether this node sends the events it emits to the server. The default is <see langword="true"/>.
    /// </summary>
    public bool Push { get; set; } = true;

    /// <summary>
    /// Whether this node receives the server's events. The default is <see langword="true"/>.
    /// </summary>
    public bool Pull { get; set; } = true;

    /// <summary>
    /// Receive only the events whose stream name starts with one of these prefixes, for example <c>Product-</c>.
    /// Empty means every event the server allows this node to receive.
    /// </summary>
    public IList<string> PullStreams { get; } = [];

    /// <summary>
    /// How often to sync while online. While the server can't be reached, the wait grows up to a minute (or this
    /// interval, when it is longer). The default is 5 seconds.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How many events one request carries. Larger backlogs are sent in several requests. The default is 200.
    /// </summary>
    public int BatchSize { get; set; } = 200;

    /// <summary>
    /// Changes every request before it is sent, for example to add a bearer token. Optional.
    /// </summary>
    public Func<HttpRequestMessage, CancellationToken, Task>? Authorize { get; set; }

    /// <summary>
    /// The HTTP handler to send requests with, for example one with a proxy or client certificate. Optional.
    /// </summary>
    public HttpMessageHandler? HttpHandler { get; set; }
}
