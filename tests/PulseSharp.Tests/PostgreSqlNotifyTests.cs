using System.Text;
using Npgsql;
using PulseSharp.PostgreSql;
using PulseSharp.Stores;

namespace PulseSharp.Tests;

public sealed class PostgreSqlNotifyTests : IDisposable
{
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);
    private static readonly CancellationToken None = CancellationToken.None;
    private readonly TempSchema _schema = TempSchema.CreateOrSkip();

    public void Dispose()
    {
        _schema.Dispose();
    }

    [Fact]
    public async Task EveryWriteWakesTheOtherInstancesButARollbackDoesNot()
    {
        await using var listener = new PostgreSqlStore(_schema.ConnectionString, _schema.Name);
        await using var writer = new PostgreSqlStore(_schema.ConnectionString, _schema.Name);
        await writer.Initialize(None);
        using var signals = new SemaphoreSlim(0);
        using var stop = new CancellationTokenSource();
        var listening = listener.ListenForChanges(() => signals.Release(), stop.Token);
        Assert.True(await signals.WaitAsync(Soon), "the listener is ready");

        await writer.Append(NewEvent(), ExpectedVersion.Any, null, None);
        Assert.True(await signals.WaitAsync(Soon), "an emitted event wakes the listener");

        await writer.AppendReplicated([NewEvent()], None);
        Assert.True(await signals.WaitAsync(Soon), "a synced event wakes the listener");

        await writer.AddJob(new NewJob(Guid.NewGuid(), "ImportProducts", "{}"u8.ToArray(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), null, None);
        Assert.True(await signals.WaitAsync(Soon), "an enqueued job wakes the listener");

        await writer.ChangeCursors("receipts", [], None);
        Assert.True(await signals.WaitAsync(Soon), "a control change wakes the listener");

        await using (var connection = new NpgsqlConnection(_schema.ConnectionString))
        {
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await writer.Append(NewEvent(), ExpectedVersion.Any, transaction, None);
            await transaction.RollbackAsync();
        }

        Assert.False(await signals.WaitAsync(TimeSpan.FromMilliseconds(500)), "a rolled back event wakes nobody");

        await stop.CancelAsync();
        await listening.WaitAsync(Soon);
    }

    [Fact]
    public async Task AnotherInstanceStartsNewWorkAtOnceInsteadOfAtTheNextPoll()
    {
        var handled = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var imported = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var worker = await TestHost.Start(pulse =>
        {
            pulse.UsePostgreSql(_schema.ConnectionString, _schema.Name);
            pulse.Advanced.PollInterval = TimeSpan.FromSeconds(30);
            pulse.When<SaleCompleted>(sale => handled.TrySetResult(sale.Number));
            pulse.OnJob<ImportProducts>(job =>
            {
                imported.TrySetResult(job.File);
                return Task.CompletedTask;
            });
        });
        await using var web = await TestHost.Start(pulse => pulse.UsePostgreSql(_schema.ConnectionString, _schema.Name));

        await web.Pulse.Emit(new SaleCompleted(7));
        await web.Pulse.Enqueue(new ImportProducts("prices.csv"));

        Assert.Equal(7, await handled.Task.WaitAsync(Soon));
        Assert.Equal("prices.csv", await imported.Task.WaitAsync(Soon));
    }

    private static NewEvent NewEvent()
    {
        return new NewEvent(Guid.NewGuid(), "Sale-1", "SaleCompleted", Encoding.UTF8.GetBytes("{}"), "web", DateTimeOffset.UtcNow);
    }
}
