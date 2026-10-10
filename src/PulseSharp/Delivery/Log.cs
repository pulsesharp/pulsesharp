using Microsoft.Extensions.Logging;

namespace PulseSharp.Delivery;

internal static partial class Log
{
    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "Handler {HandlerName} failed on event {EventId} in stream {Stream} (attempt {Attempt} of {MaxAttempts}). Retrying at {RetryAt}."
    )]
    public static partial void HandlerWillRetry(
        ILogger logger,
        Exception exception,
        string handlerName,
        Guid eventId,
        string stream,
        int attempt,
        int maxAttempts,
        DateTimeOffset retryAt
    );

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Error,
        Message = "Handler {HandlerName} failed on event {EventId} in stream {Stream} after {Attempts} attempts. This stream is stopped for this handler."
    )]
    public static partial void HandlerStopped(
        ILogger logger,
        Exception exception,
        string handlerName,
        Guid eventId,
        string stream,
        int attempts
    );

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Error,
        Message = "Handler {HandlerName} hit an unexpected error while reading events. It will try again."
    )]
    public static partial void RunnerError(ILogger logger, Exception exception, string handlerName);
}
