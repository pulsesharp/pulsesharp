using Microsoft.Extensions.Logging;

namespace PulseSharp.Sync.Server;

internal static partial class Log
{
    [LoggerMessage(
        EventId = 4001,
        Level = LogLevel.Warning,
        Message = "Node {Node} sent {Count} events for streams it may not write, for example '{Stream}'. They were refused and the node's other events were saved. This node may write streams starting with: {Allowed}. Change this with server.PushStreams."
    )]
    public static partial void PushRefused(ILogger logger, string node, int count, string stream, string allowed);
}
