using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace PulseSharp.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class PostgresBenchmarks
{
    private const int EventCount = 200;
    private readonly string _schema = $"bench_{Guid.NewGuid():N}";
    private string _connectionString = "";
    private ServiceProvider? _services;
    private IPulse? _pulse;

    [GlobalSetup]
    public async Task Setup()
    {
        _connectionString = Environment.GetEnvironmentVariable("PULSESHARP_TEST_POSTGRES")
            ?? throw new InvalidOperationException("Set PULSESHARP_TEST_POSTGRES to run the PostgreSQL benchmark.");
        _services = new ServiceCollection()
            .AddPulseSharp(pulse => pulse.UsePostgreSql(_connectionString, _schema))
            .BuildServiceProvider();
        _pulse = _services.GetRequiredService<IPulse>();
        await _pulse.Emit(new BenchmarkStream(0), new BenchmarkEvent(0));
    }

    [Benchmark(Description = "Emit (PostgreSQL, no handlers)", OperationsPerInvoke = EventCount)]
    public async Task Emit()
    {
        for (var number = 0; number < EventCount; number++)
        {
            await _pulse!.Emit(new BenchmarkStream(number % 10), new BenchmarkEvent(number));
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _services!.DisposeAsync();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE;", connection);
        await command.ExecuteNonQueryAsync();
    }
}
