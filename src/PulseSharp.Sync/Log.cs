using Microsoft.Extensions.Logging;

namespace PulseSharp.Sync;

internal static partial class Log
{
    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Warning,
        Message = "Sync with {Server} failed: {Reason} Events are kept and sent when the server can be reached."
    )]
    public static partial void SyncFailed(ILogger logger, Uri server, string reason);

    [LoggerMessage(
        EventId = 3002,
        Level = LogLevel.Information,
        Message = "Sync with {Server} works again."
    )]
    public static partial void SyncRecovered(ILogger logger, Uri server);

    [LoggerMessage(
        EventId = 3003,
        Level = LogLevel.Warning,
        Message = "The server {Server} refused {Count} events from this device because their streams are not allowed for this device. They stay here and are not sent again. Check the streams this app emits to, or the server's PushStreams."
    )]
    public static partial void SyncRefused(ILogger logger, Uri server, int count);

    [LoggerMessage(
        EventId = 3004,
        Level = LogLevel.Warning,
        Message = "A handler of IPulseSync.StatusChanged threw an exception. Sync is not affected."
    )]
    public static partial void StatusSubscriberFailed(ILogger logger, Exception exception);
}
