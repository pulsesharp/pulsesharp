using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;

namespace PulseSharp.Tests;

/// <summary>
/// Kills a real app process (tests/PulseSharp.CrashApp) in the middle of its work, then starts a new app on the same database.
/// The new app's clock runs ahead, so the claims of the killed app have run out without waiting 10 or 30 seconds.
/// </summary>
public abstract class CrashTests : IDisposable
{
    protected abstract IReadOnlyList<string> CrashAppStore { get; }

    protected abstract void UseSameStore(PulseSharpBuilder pulse);

    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task AnAppKilledInTheMiddleOfAnEventLosesNothingAndTheNextAppCarriesOnInOrder()
    {
        await using (var writer = await TestHost.Start(UseSameStore))
        {
            for (var number = 1; number <= 10; number++)
            {
                await writer.Pulse.Emit(new SaleStream("A"), new SaleCompleted(number));
            }
        }

        var handledBeforeTheKill = await RunCrashAppAndKill(stuckOnSale: 5, killWhen: "stuck 5");
        var handled = new ConcurrentQueue<int>();
        await using var restarted = await StartWithClockAhead(TimeSpan.FromSeconds(11), pulse => pulse.On<SaleCompleted>(
            "recorder",
            (sale, _, _) =>
            {
                handled.Enqueue(sale.Number);
                return Task.CompletedTask;
            }
        ));

        await Eventually.True(() => handled.Contains(10), "the restarted app handles the last sale");
        var numbers = handled.ToList();
        Assert.True(numbers[0] <= 5, "the sale the killed app was stuck on is handled again");
        Assert.Equal(Enumerable.Range(numbers[0], 11 - numbers[0]), numbers);
        Assert.Equal(Enumerable.Range(1, 10), handledBeforeTheKill.Union(numbers).Order());
    }

    [Fact]
    public async Task AJobKilledWhileItRunsRunsAgainAndTheKillCountsAsAnAttempt()
    {
        await using (var writer = await TestHost.Start(UseSameStore))
        {
            await writer.Pulse.Enqueue(new ImportProducts("prices.csv"));
        }

        await RunCrashAppAndKill(stuckOnSale: 0, killWhen: "job started 1");
        var attempt = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var restarted = await StartWithClockAhead(TimeSpan.FromSeconds(31), pulse => pulse.OnJob<ImportProducts>(
            (_, context, _) =>
            {
                attempt.TrySetResult(context.Attempt);
                return Task.CompletedTask;
            }
        ));

        Assert.Equal(2, await attempt.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private async Task<IReadOnlyList<int>> RunCrashAppAndKill(int stuckOnSale, string killWhen)
    {
        var folder = AppContext.BaseDirectory.Replace("PulseSharp.Tests", "PulseSharp.CrashApp", StringComparison.Ordinal);
        var start = new ProcessStartInfo(Path.Combine(folder, OperatingSystem.IsWindows() ? "PulseSharp.CrashApp.exe" : "PulseSharp.CrashApp"))
        {
            RedirectStandardOutput = true
        };
        foreach (var argument in CrashAppStore)
        {
            start.ArgumentList.Add(argument);
        }

        start.ArgumentList.Add(stuckOnSale.ToString(CultureInfo.InvariantCulture));
        using var app = Process.Start(start)!;
        var handled = new List<int>();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            string? line;
            while ((line = await app.StandardOutput.ReadLineAsync(timeout.Token)) != killWhen)
            {
                Assert.NotNull(line);
                if (line.StartsWith("handled ", StringComparison.Ordinal))
                {
                    handled.Add(int.Parse(line["handled ".Length..], CultureInfo.InvariantCulture));
                }
            }
        }
        finally
        {
            app.Kill(entireProcessTree: true);
            await app.WaitForExitAsync();
        }

        return handled;
    }

    private Task<TestHost> StartWithClockAhead(TimeSpan ahead, Action<PulseSharpBuilder> configure)
    {
        return TestHost.Start(
            pulse =>
            {
                UseSameStore(pulse);
                configure(pulse);
            },
            services => services.AddSingleton<TimeProvider>(new ClockAhead(ahead))
        );
    }

    private sealed class ClockAhead(TimeSpan ahead) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return base.GetUtcNow() + ahead;
        }
    }
}

public sealed class SqliteCrashTests : CrashTests
{
    private readonly TempDatabase _database = new();

    protected override IReadOnlyList<string> CrashAppStore
    {
        get
        {
            return ["sqlite", _database.ConnectionString];
        }
    }

    protected override void UseSameStore(PulseSharpBuilder pulse)
    {
        pulse.UseSqlite(_database.ConnectionString);
    }

    public override void Dispose()
    {
        _database.Dispose();
        base.Dispose();
    }
}

public sealed class PostgreSqlCrashTests : CrashTests
{
    private readonly TempSchema _schema = TempSchema.CreateOrSkip();

    protected override IReadOnlyList<string> CrashAppStore
    {
        get
        {
            return ["postgres", _schema.ConnectionString, _schema.Name];
        }
    }

    protected override void UseSameStore(PulseSharpBuilder pulse)
    {
        pulse.UsePostgreSql(_schema.ConnectionString, _schema.Name);
    }

    public override void Dispose()
    {
        _schema.Dispose();
        base.Dispose();
    }
}
