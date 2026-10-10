namespace PulseSharp.Tests;

public sealed record SaleCompleted(int Number);

public sealed record SaleRefunded(int Number);

[EventName("sale-voided")]
public sealed record SaleVoided(int Number);

public sealed record SaleStream(string Id) : StreamName
{
    public override string Value()
    {
        return $"Sale-{Id}";
    }
}

public sealed record BlankStream : StreamName
{
    public override string Value()
    {
        return " ";
    }
}

public sealed record SettingsStream(DateOnly Date) : DailyStreamName(Date)
{
    protected override string Category
    {
        get
        {
            return "Settings";
        }
    }
}
