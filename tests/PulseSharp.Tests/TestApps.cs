using System.Diagnostics;

namespace PulseSharp.Tests;

/// <summary>
/// Starts one of the small apps in the tests folder (PulseSharp.CrashApp, PulseSharp.OldVersionApp) as a real process.
/// The test project builds them first, into folders next to its own.
/// </summary>
internal static class TestApps
{
    public static Process Start(string project, IEnumerable<string> arguments)
    {
        var folder = AppContext.BaseDirectory.Replace("PulseSharp.Tests", project, StringComparison.Ordinal);
        var start = new ProcessStartInfo(Path.Combine(folder, OperatingSystem.IsWindows() ? project + ".exe" : project))
        {
            RedirectStandardOutput = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start)!;
    }
}
