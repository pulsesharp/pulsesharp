using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace PulseSharp.Tests;

public sealed class SqliteTests : IDisposable
{
    private readonly TempDatabase _database = new();

    public void Dispose()
    {
        _database.Dispose();
    }

    [Fact]
    public async Task HandlersContinueFromTheirCursorsAfterARestart()
    {
        var received = new ConcurrentQueue<int>();

        await using (var first = await StartHost(received))
        {
            await first.Pulse.Emit(new SaleStream("A"), new SaleCompleted(1));
            await first.Pulse.Emit(new SaleStream("A"), new SaleCompleted(2));
            await Eventually.True(() => received.Count == 2, "the first app handles both events");
        }

        await using (var second = await StartHost(received))
        {
            await second.Pulse.Emit(new SaleStream("A"), new SaleCompleted(3));
            await Eventually.True(() => received.Count == 3, "the restarted app handles only the new event");
            await Eventually.StaysTrue(() => received.Count == 3, "old events are not handled again");
        }

        Assert.Equal([1, 2, 3], received);
    }

    [Fact]
    public async Task APendingJobRunsAfterARestart()
    {
        var ran = new ConcurrentQueue<string>();
        Guid jobId;

        await using (var first = await StartHost(new ConcurrentQueue<int>(), ran))
        {
            jobId = await first.Pulse.Enqueue(
                new ImportProducts("tomorrow.csv"),
                new EnqueueOptions { RunAt = DateTimeOffset.UtcNow.AddMilliseconds(600) }
            );
        }

        Assert.Empty(ran);

        await using (var second = await StartHost(new ConcurrentQueue<int>(), ran))
        {
            await Eventually.True(() => ran.Contains("tomorrow.csv"), "the restarted app runs the waiting job");
            await Eventually.True(() => second.Control.GetJob(jobId).Result?.State == JobState.Succeeded, "the job succeeds");
        }
    }

    [Fact]
    public async Task SavesTheEventInTheCallersTransaction()
    {
        var received = new ConcurrentQueue<int>();
        await using var host = await StartHost(received);
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync();

        await using (var rolledBack = (SqliteTransaction)await connection.BeginTransactionAsync())
        {
            await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(1), new EmitOptions { Transaction = rolledBack });
            await rolledBack.RollbackAsync();
        }

        await using (var committed = (SqliteTransaction)await connection.BeginTransactionAsync())
        {
            await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(2), new EmitOptions { Transaction = committed });
            await committed.CommitAsync();
        }

        await Eventually.True(() => received.Count == 1, "the committed event is handled");
        await Eventually.StaysTrue(() => received.Count == 1, "the rolled-back event never appears");
        Assert.Equal([2], received);
    }

    [Fact]
    public async Task RefusesATransactionFromAnotherProvider()
    {
        await using var host = await StartHost(new ConcurrentQueue<int>());

        await Assert.ThrowsAsync<ArgumentException>(
            () => host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(1), new EmitOptions { Transaction = new ForeignTransaction() })
        );
    }

    [Fact]
    public async Task AnEventThatCannotBeSerializedFailsAtEmit()
    {
        await using var host = await StartHost(new ConcurrentQueue<int>());

        await Assert.ThrowsAnyAsync<Exception>(
            () => host.Pulse.Emit(new SaleStream("A"), new NotSerializable(typeof(string)))
        );
    }

    private Task<TestHost> StartHost(ConcurrentQueue<int> received, ConcurrentQueue<string>? jobs = null)
    {
        return TestHost.Start(pulse =>
        {
            pulse.UseSqlite(_database.ConnectionString);
            pulse.On<SaleCompleted>("recorder", (sale, _, _) =>
            {
                received.Enqueue(sale.Number);
                return Task.CompletedTask;
            });
            pulse.OnJob<ImportProducts>((job, _, _) =>
            {
                jobs?.Enqueue(job.File);
                return Task.CompletedTask;
            });
        });
    }

    private sealed record NotSerializable(Type Type);

    private sealed class ForeignTransaction : System.Data.Common.DbTransaction
    {
        public override System.Data.IsolationLevel IsolationLevel
        {
            get
            {
                return System.Data.IsolationLevel.Unspecified;
            }
        }

        protected override System.Data.Common.DbConnection? DbConnection
        {
            get
            {
                return null;
            }
        }

        public override void Commit()
        {
        }

        public override void Rollback()
        {
        }
    }
}
