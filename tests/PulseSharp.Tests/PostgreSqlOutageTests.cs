using System.Collections.Concurrent;
using Npgsql;

namespace PulseSharp.Tests;

/// <summary>
/// Takes the database away while the app runs: every connection is cut and new ones are refused for a while, which is what
/// the app sees when PostgreSQL restarts. Uses a database of its own, so the other tests keep running.
/// </summary>
public sealed class PostgreSqlOutageTests : IDisposable
{
    private readonly TempSchema _server = TempSchema.CreateOrSkip();
    private readonly string _database = $"pulsesharp_outage_{Guid.NewGuid():N}";
    private readonly string _connectionString;

    public PostgreSqlOutageTests()
    {
        _connectionString = new NpgsqlConnectionStringBuilder(_server.ConnectionString) { Database = _database }.ConnectionString;
        OnServer($"CREATE DATABASE {_database};");
    }

    [Fact]
    public async Task TheAppCarriesOnByItselfAfterTheDatabaseWasGoneForAWhile()
    {
        var handled = new ConcurrentQueue<int>();
        var imported = new ConcurrentQueue<string>();
        await using var app = await TestHost.Start(pulse =>
        {
            pulse.UsePostgreSql(_connectionString);
            pulse.When<SaleCompleted>(sale => handled.Enqueue(sale.Number));
            pulse.OnJob<ImportProducts>(job =>
            {
                imported.Enqueue(job.File);
                return Task.CompletedTask;
            });
        });
        await app.Pulse.Emit(new SaleCompleted(1));
        await Eventually.True(() => handled.Contains(1), "the first sale is handled");

        OnServer($"ALTER DATABASE {_database} ALLOW_CONNECTIONS false;");
        OnServer($"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{_database}';");
        await Task.Delay(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<NpgsqlException>(() => app.Pulse.Emit(new SaleCompleted(99)));
        OnServer($"ALTER DATABASE {_database} ALLOW_CONNECTIONS true;");

        await app.Pulse.Emit(new SaleCompleted(2));
        await app.Pulse.Enqueue(new ImportProducts("prices.csv"));
        await Eventually.True(() => handled.Contains(2) && imported.Contains("prices.csv"), "the app handles new work again");
        Assert.Equal([1, 2], handled.Distinct().Order());
    }

    public void Dispose()
    {
        NpgsqlConnection.ClearAllPools();
        OnServer($"DROP DATABASE IF EXISTS {_database} WITH (FORCE);");
    }

    private void OnServer(string sql)
    {
        using var connection = new NpgsqlConnection(_server.ConnectionString);
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }
}
