using System.Collections.Concurrent;
using PulseSharp.PostgreSql;
using PulseSharp.Sqlite;
using PulseSharp.Stores;

namespace PulseSharp.Tests;

/// <summary>
/// Makes a database with an older, published PulseSharp (tests/PulseSharp.OldVersionApp), then opens it with the current
/// code, the way an app does after an update.
/// </summary>
public abstract class UpgradeTests : IDisposable
{
    protected abstract IReadOnlyList<string> OldAppStore { get; }

    protected abstract void UseSameStore(PulseSharpBuilder pulse);

    protected abstract ISyncStore OpenStore();

    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ADatabaseMadeByAnOlderVersionOpensAndEverythingCarriesOn()
    {
        await RunOldApp();
        var recorded = new ConcurrentQueue<int>();
        var printed = new ConcurrentQueue<int>();
        var imported = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await TestHost.Start(pulse =>
        {
            UseSameStore(pulse);
            pulse.On<SaleCompleted>("recorder", (sale, _, _) =>
            {
                recorded.Enqueue(sale.Number);
                return Task.CompletedTask;
            });
            pulse.On<SaleCompleted>("printer", (sale, _, _) =>
            {
                printed.Enqueue(sale.Number);
                return Task.CompletedTask;
            });
            pulse.OnJob<ImportProducts>((job, _, _) =>
            {
                imported.TrySetResult(job.File);
                return Task.CompletedTask;
            });
        });

        var next = await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(4));
        Assert.Equal(4, next.StreamPosition);
        await Eventually.True(() => recorded.Contains(4), "the recorder handles the new sale");
        await Eventually.StaysTrue(() => recorded.SequenceEqual([4]), "the recorder does not handle the old sales again");

        var printer = Assert.Single(await host.Control.GetCursors("printer"));
        Assert.Equal(CursorState.Failed, printer.State);
        Assert.Contains("out of paper", printer.LastError, StringComparison.Ordinal);
        await host.Control.RetryFailed("printer");
        await Eventually.True(() => printed.Count == 4, "the printer handles every sale after the retry");
        Assert.Equal([1, 2, 3, 4], printed);

        Assert.Equal("prices.csv", await imported.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, await OpenStore().LoadSyncPosition("server", SyncDirection.Push, CancellationToken.None));
    }

    private async Task RunOldApp()
    {
        using var app = TestApps.Start("PulseSharp.OldVersionApp", OldAppStore);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var output = await app.StandardOutput.ReadToEndAsync(timeout.Token);
        await app.WaitForExitAsync(timeout.Token);
        Assert.True(app.ExitCode == 0, $"The old app failed: {output}");
    }
}

public sealed class SqliteUpgradeTests : UpgradeTests
{
    private readonly TempDatabase _database = new();

    protected override IReadOnlyList<string> OldAppStore
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

    protected override ISyncStore OpenStore()
    {
        return new SqliteStore(_database.ConnectionString);
    }

    public override void Dispose()
    {
        _database.Dispose();
        base.Dispose();
    }
}

public sealed class PostgreSqlUpgradeTests : UpgradeTests
{
    private readonly TempSchema _schema = TempSchema.CreateOrSkip();

    protected override IReadOnlyList<string> OldAppStore
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

    protected override ISyncStore OpenStore()
    {
        return new PostgreSqlStore(_schema.ConnectionString, _schema.Name);
    }

    public override void Dispose()
    {
        _schema.Dispose();
        base.Dispose();
    }
}
