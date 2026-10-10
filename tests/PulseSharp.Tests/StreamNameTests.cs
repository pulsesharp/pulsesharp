namespace PulseSharp.Tests;

public sealed class StreamNameTests
{
    [Fact]
    public void FromKeepsThePlainName()
    {
        var stream = StreamName.From("Sale-1");

        Assert.Equal("Sale-1", stream.Value());
        Assert.Equal("Sale-1", stream.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FromRejectsAnEmptyName(string value)
    {
        Assert.Throws<ArgumentException>(() => StreamName.From(value));
    }

    [Fact]
    public void TypedStreamNamesAreEqualByValue()
    {
        Assert.Equal(new SaleStream("1"), new SaleStream("1"));
        Assert.Equal("Sale-1", new SaleStream("1").ToString());
    }

    [Fact]
    public void DailyStreamNameEndsWithTheDate()
    {
        var stream = new SettingsStream(new DateOnly(2026, 10, 11));

        Assert.Equal("Settings-2026-10-11", stream.Value());
    }

    [Fact]
    public void ExpectedVersionDefaultsToAny()
    {
        Assert.Equal(ExpectedVersionKind.Any, default(ExpectedVersion).Kind);
        Assert.Equal(ExpectedVersion.Any, new EmitOptions().ExpectedVersion);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ExactlyRejectsPositionsBelowOne(long position)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ExpectedVersion.Exactly(position));
    }
}
