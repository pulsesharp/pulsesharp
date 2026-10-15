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
}
