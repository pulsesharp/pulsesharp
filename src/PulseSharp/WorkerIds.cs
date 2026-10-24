using System.Diagnostics;

namespace PulseSharp;

/// <summary>
/// The name an app instance claims handlers and jobs under. It says which machine and process it is, so another instance on
/// the same machine can tell that a holder crashed, and take over at once instead of waiting for the lease to run out.
/// </summary>
internal static class WorkerIds
{
    private static readonly TimeSpan SameProcessTolerance = TimeSpan.FromSeconds(5);
    private static readonly long StartTicks = ReadOwnStartTicks();

    public static string Create(string node)
    {
        return $"{node}:{Environment.MachineName}:{Environment.ProcessId}:{StartTicks}:{Guid.NewGuid():N}";
    }

    /// <summary>
    /// Whether the worker was a process on this machine that no longer exists. When anything is unclear (another machine,
    /// an unknown name, a process we may not look at), the answer is no, and the lease simply runs out as before.
    /// </summary>
    public static bool IsGoneFromThisMachine(string? workerId)
    {
        if (workerId is null)
        {
            return false;
        }

        var parts = workerId.Split(':');
        if (parts.Length < 5
            || !string.Equals(parts[^4], Environment.MachineName, StringComparison.Ordinal)
            || !int.TryParse(parts[^3], out var processId)
            || !long.TryParse(parts[^2], out var startTicks)
            || processId == Environment.ProcessId)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            if (startTicks == 0 || process.HasExited)
            {
                return process.HasExited;
            }

            var started = process.StartTime.ToUniversalTime();
            return (started - new DateTime(startTicks, DateTimeKind.Utc)).Duration() > SameProcessTolerance;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static long ReadOwnStartTicks()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return 0;
        }
    }
}
