using System.Globalization;

namespace PulseSharp.Tests;

public sealed class EventIdsTests
{
    [Fact]
    public void CreatesVersion7Ids()
    {
        var text = EventIds.NewVersion7(DateTimeOffset.UtcNow).ToString();

        Assert.Equal('7', text[14]);
        Assert.Contains(text[19], "89ab");
    }

    [Fact]
    public void StartsWithTheUnixTimeInMilliseconds()
    {
        var now = new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

        var text = EventIds.NewVersion7(now).ToString("N");

        Assert.Equal(now.ToUnixTimeMilliseconds(), long.Parse(text[..12], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void LaterIdsSortAfterEarlierIds()
    {
        var earlier = EventIds.NewVersion7(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
        var later = EventIds.NewVersion7(new DateTimeOffset(2026, 10, 11, 12, 0, 1, TimeSpan.Zero));

        Assert.True(string.CompareOrdinal(earlier.ToString(), later.ToString()) < 0);
    }
}
