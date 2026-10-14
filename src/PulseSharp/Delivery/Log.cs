using Microsoft.Extensions.Logging;

namespace PulseSharp.Delivery;

internal static partial class Log
{
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Information,
        Message = "PulseSharp is ready. Store: {Store}, node: {Node}, event handlers: {HandlerCount}, job types: {JobCount}."
    )]
    public static partial void Ready(ILogger logger, string store, string node, int handlerCount, int jobCount);

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

    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Warning,
        Message = "Job {JobName} {JobId} failed (attempt {Attempt} of {MaxAttempts}). Retrying at {RetryAt}."
    )]
    public static partial void JobWillRetry(
        ILogger logger,
        Exception exception,
        string jobName,
        Guid jobId,
        int attempt,
        int maxAttempts,
        DateTimeOffset retryAt
    );

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Error,
        Message = "Job {JobName} {JobId} failed after {Attempts} attempts. Use IPulseControl.RetryJob to run it again."
    )]
    public static partial void JobFailed(ILogger logger, Exception exception, string jobName, Guid jobId, int attempts);

    [LoggerMessage(
        EventId = 2003,
        Level = LogLevel.Error,
        Message = "The job runner hit an unexpected error. It will try again."
    )]
    public static partial void JobRunnerError(ILogger logger, Exception exception);
}
