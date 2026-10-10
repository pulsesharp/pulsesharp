namespace PulseSharp.Tests;

internal static class Eventually
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static async Task True(Func<bool> condition, string description)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting until {description}.");
            }

            await Task.Delay(10);
        }
    }

    public static async Task StaysTrue(Func<bool> condition, string description)
    {
        var until = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(300);
        while (DateTimeOffset.UtcNow < until)
        {
            Assert.True(condition(), $"Expected {description} to stay true.");
            await Task.Delay(10);
        }
    }
}
