namespace PulseSharp;

/// <summary>
/// Runs work for a limited time. When the time is up, the work is told to stop. When it still has not stopped after a
/// grace period, PulseSharp stops waiting for it, so one stuck handler or job can never block everything behind it.
/// </summary>
internal static class TimeLimit
{
    public static async Task Run(
        Func<CancellationToken, Task> work,
        TimeSpan? limit,
        TimeSpan grace,
        string what,
        string setting,
        TimeProvider time,
        CancellationToken cancellationToken
    )
    {
        if (limit is not { } allowed)
        {
            await work(cancellationToken).ConfigureAwait(false);
            return;
        }

        using var stopWork = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var task = Task.Run(() => work(stopWork.Token), CancellationToken.None);
        var timeUp = Task.Delay(allowed, time, timer.Token);

        if (await Task.WhenAny(task, timeUp).ConfigureAwait(false) == task)
        {
            await timer.CancelAsync().ConfigureAwait(false);
            await task.ConfigureAwait(false);
            return;
        }

        await stopWork.CancelAsync().ConfigureAwait(false);
        var graceOver = Task.Delay(grace, time);
        if (await Task.WhenAny(task, graceOver).ConfigureAwait(false) != task)
        {
            _ = task.ContinueWith(static finished => finished.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            ThrowStopped(what, setting, allowed, cancellationToken);
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ThrowStopped(what, setting, allowed, cancellationToken);
        }
    }

    private static void ThrowStopped(string what, string setting, TimeSpan allowed, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException($"{what} took longer than {allowed.TotalSeconds:0.##} seconds and was stopped. If it needs more time, raise {setting}.");
    }
}
