using Npgsql;

namespace PulseSharp.Tests;

internal sealed class TempSchema : IDisposable
{
    public const string ConnectionVariable = "PULSESHARP_TEST_POSTGRES";

    private TempSchema(string connectionString)
    {
        ConnectionString = connectionString;
    }

    public string ConnectionString { get; }

    public string Name { get; } = $"t_{Guid.NewGuid():N}";

    public static TempSchema CreateOrSkip()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(connectionString), $"Set {ConnectionVariable} to run the PostgreSQL tests.");
        return new TempSchema(connectionString!);
    }

    public void Dispose()
    {
        using var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();
        using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{Name}\" CASCADE;", connection);
        command.ExecuteNonQuery();
    }
}
