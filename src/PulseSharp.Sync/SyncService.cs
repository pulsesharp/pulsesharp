using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PulseSharp.Delivery;
using PulseSharp.Stores;

namespace PulseSharp.Sync;

internal sealed class SyncService : BackgroundService, IPulseSync, IRetentionGuard
{
    private const int MaxWaitingCount = 100_000;

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan WaitToGatherABurst = TimeSpan.FromMilliseconds(100);

    private readonly SyncOptions _options;
    private readonly Func<string> _node;
    private readonly ISyncStore _store;
    private readonly TimeProvider _time;
    private readonly WakeUpSignal _signal;
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _statusGate = new();
    private volatile SyncStatus _status = new(false, null, null);
    private string? _remote;
    private int _failures;

    public SyncService(SyncOptions options, Func<string> node, ISyncStore store, TimeProvider time, WakeUpSignal signal, ILogger logger)
    {
        _options = options;
        _node = node;
        _store = store;
        _time = time;
        _signal = signal;
        _logger = logger;

        var handler = options.HttpHandler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        _http = new HttpClient(handler, disposeHandler: options.HttpHandler is null)
        {
            BaseAddress = WithTrailingSlash(options.Server!),
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public SyncStatus Status
    {
        get
        {
            return _status;
        }
    }

    public event Action<SyncStatus>? StatusChanged;

    public async Task<SyncStatus> SyncNow(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RunOnce(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        return _status;
    }

    public override void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var wakeUp = _signal.Next;
                await SyncNow(stoppingToken).ConfigureAwait(false);
                await WaitForNextSync(wakeUp, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task WaitForNextSync(Task wakeUp, CancellationToken stoppingToken)
    {
        using var timeUp = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var timer = Task.Delay(NextWait(), _time, timeUp.Token);

        while (true)
        {
            if (await Task.WhenAny(timer, wakeUp).ConfigureAwait(false) == timer)
            {
                stoppingToken.ThrowIfCancellationRequested();
                return;
            }

            wakeUp = _signal.Next;
            if (await SomethingIsWaitingToBeSent(stoppingToken).ConfigureAwait(false))
            {
                await timeUp.CancelAsync().ConfigureAwait(false);
                return;
            }
        }
    }

    private async Task<bool> SomethingIsWaitingToBeSent(CancellationToken cancellationToken)
    {
        var waiting = await CountWaiting(cancellationToken).ConfigureAwait(false);
        if (waiting is { } count)
        {
            Set(_status with { Waiting = count });
        }

        if (_failures > 0 || !_options.Push || waiting is not > 0)
        {
            return false;
        }

        await Task.Delay(WaitToGatherABurst, _time, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task RunOnce(CancellationToken cancellationToken)
    {
        Exception? failure;
        try
        {
            var remote = _remote ??= await LoadRemote(cancellationToken).ConfigureAwait(false);
            Exception? pushFailure = null;
            if (_options.Push)
            {
                pushFailure = await Attempt(() => PushAll(remote, cancellationToken), cancellationToken).ConfigureAwait(false);
            }

            Exception? pullFailure = null;
            if (_options.Pull)
            {
                pullFailure = await Attempt(() => PullAll(remote, cancellationToken), cancellationToken).ConfigureAwait(false);
            }

            failure = pushFailure ?? pullFailure;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            failure = exception;
        }

        if (failure is not null)
        {
            _failures++;
            if (_failures == 1)
            {
                Log.SyncFailed(_logger, _options.Server!, failure.Message);
            }

            await Publish(_status with { Online = false, LastError = failure.Message }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_failures > 0)
        {
            Log.SyncRecovered(_logger, _options.Server!);
        }

        _failures = 0;
        await Publish(new SyncStatus(true, _time.GetUtcNow(), null), cancellationToken).ConfigureAwait(false);
    }

    private async Task Publish(SyncStatus status, CancellationToken cancellationToken)
    {
        var waiting = await CountWaiting(cancellationToken).ConfigureAwait(false);
        Set(status with { Waiting = waiting ?? status.Waiting });
    }

    private void Set(SyncStatus status)
    {
        lock (_statusGate)
        {
            if (status == _status)
            {
                return;
            }

            _status = status;
        }

        try
        {
            StatusChanged?.Invoke(status);
        }
        catch (Exception exception)
        {
            Log.StatusSubscriberFailed(_logger, exception);
        }
    }

    private async Task<int?> CountWaiting(CancellationToken cancellationToken)
    {
        if (!_options.Push)
        {
            return 0;
        }

        try
        {
            var after = await _store.LoadSyncPosition(_remote ?? AddressKey, SyncDirection.Push, cancellationToken).ConfigureAwait(false);
            return await _store.CountForSync(new SyncQuery(after, _node(), null, [], MaxWaitingCount), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<long?> OwnEventsSentUpTo(CancellationToken cancellationToken)
    {
        if (!_options.Push)
        {
            return null;
        }

        return await _store.LoadSyncPosition(_remote ?? AddressKey, SyncDirection.Push, cancellationToken).ConfigureAwait(false);
    }

    private string AddressKey
    {
        get
        {
            return $"address:{_options.Server}";
        }
    }

    private static async Task<Exception?> Attempt(Func<Task> step, CancellationToken cancellationToken)
    {
        try
        {
            await step().ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            return exception;
        }
    }

    private async Task<string> LoadRemote(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "info");
        var info = await Send(request, WireJson.Default.ServerInfo, cancellationToken).ConfigureAwait(false);

        if (info.Protocol != SyncProtocol.Version)
        {
            throw new InvalidOperationException(
                $"The server speaks sync protocol {info.Protocol} and this app speaks {SyncProtocol.Version}. Use the same PulseSharp version on both."
            );
        }

        if (string.Equals(info.Node, _node(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The server and this app are both called '{info.Node}'. Give each one its own name with pulse.Node."
            );
        }

        return info.Node;
    }

    private async Task PushAll(string remote, CancellationToken cancellationToken)
    {
        var node = _node();
        while (true)
        {
            var after = await _store.LoadSyncPosition(remote, SyncDirection.Push, cancellationToken).ConfigureAwait(false);
            var events = await _store
                .ReadForSync(new SyncQuery(after, node, null, [], _options.BatchSize), cancellationToken)
                .ConfigureAwait(false);
            if (events.Count == 0)
            {
                return;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "push")
            {
                Content = JsonContent.Create(new PushRequest(events.Select(WireEvent.From).ToList()), WireJson.Default.PushRequest)
            };
            var response = await Send(request, WireJson.Default.PushResponse, cancellationToken).ConfigureAwait(false);
            if (response.Refused > 0)
            {
                Log.SyncRefused(_logger, _options.Server!, response.Refused);
            }

            await _store.SaveSyncPosition(remote, SyncDirection.Push, events[^1].GlobalPosition, cancellationToken).ConfigureAwait(false);
            await _store.SaveSyncPosition(AddressKey, SyncDirection.Push, events[^1].GlobalPosition, cancellationToken).ConfigureAwait(false);

            if (events.Count < _options.BatchSize)
            {
                return;
            }
        }
    }

    private async Task PullAll(string remote, CancellationToken cancellationToken)
    {
        while (true)
        {
            var after = await _store.LoadSyncPosition(remote, SyncDirection.Pull, cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, PullAddress(after));
            var page = await Send(request, WireJson.Default.PullResponse, cancellationToken).ConfigureAwait(false);

            if (page.Events.Count > 0)
            {
                var events = page.Events.Select(wire => wire.ToNewEvent()).ToList();
                await _store.AppendReplicated(events, cancellationToken).ConfigureAwait(false);
                _signal.Notify();
            }

            if (page.Next <= after)
            {
                return;
            }

            await _store.SaveSyncPosition(remote, SyncDirection.Pull, page.Next, cancellationToken).ConfigureAwait(false);
            if (!page.HasMore)
            {
                return;
            }
        }
    }

    private string PullAddress(long after)
    {
        var address = new StringBuilder("pull?after=")
            .Append(after.ToString(CultureInfo.InvariantCulture))
            .Append("&max=")
            .Append(_options.BatchSize.ToString(CultureInfo.InvariantCulture));

        foreach (var prefix in _options.PullStreams)
        {
            address.Append("&stream=").Append(Uri.EscapeDataString(prefix));
        }

        return address.ToString();
    }

    private async Task<T> Send<T>(HttpRequestMessage request, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        request.Headers.Add(SyncProtocol.ProtocolHeader, SyncProtocol.Version.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(SyncProtocol.NodeHeader, _node());
        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            request.Headers.Add(SyncProtocol.KeyHeader, _options.ApiKey);
        }

        if (_options.Authorize is not null)
        {
            await _options.Authorize(request, cancellationToken).ConfigureAwait(false);
        }

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var reason = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"The server refused the request ({(int)response.StatusCode} {response.ReasonPhrase}). {reason}".TrimEnd(),
                null,
                response.StatusCode
            );
        }

        return await response.Content.ReadFromJsonAsync(type, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The server sent an empty answer.");
    }

    private TimeSpan NextWait()
    {
        if (_failures == 0)
        {
            return _options.Interval;
        }

        var longest = _options.Interval > MaxBackoff ? _options.Interval : MaxBackoff;
        var backoff = _options.Interval * Math.Pow(2, Math.Min(_failures, 10));
        return backoff < longest ? backoff : longest;
    }

    private static Uri WithTrailingSlash(Uri server)
    {
        var text = server.ToString();
        return text.EndsWith('/') ? server : new Uri(text + "/");
    }
}
