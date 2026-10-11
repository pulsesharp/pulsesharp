using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace PulseSharp.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class SqliteBenchmarks
{
    private const int EventCount = 200;

    [Benchmark(Description = "Emit (SQLite file, pooled, no handlers)", OperationsPerInvoke = EventCount)]
    public async Task Emit()
    {
        var file = Path.Combine(Path.GetTempPath(), $"pulsesharp-bench-{Guid.NewGuid():N}.db");
        try
        {
            await using (var services = new ServiceCollection()
                .AddPulseSharp(pulse => pulse.UseSqlite($"Data Source={file}"))
                .BuildServiceProvider())
            {
                var pulse = services.GetRequiredService<IPulse>();
                for (var number = 0; number < EventCount; number++)
                {
                    await pulse.Emit(new BenchmarkStream(number % 10), new BenchmarkEvent(number));
                }
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                File.Delete(file + suffix);
            }
        }
    }
}
