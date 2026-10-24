using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Npgsql;
using PulseSharp.PostgreSql;
using PulseSharp.Stores;
using PulseSharp.Testing;

namespace PulseSharp.Tests;

public sealed class EmitAllTests
{
    [Fact]
    public async Task SavesTheEventsInOrderAndHandlersReceiveThemInOrder()
    {
        var received = new ConcurrentQueue<int>();
        await using var host = await TestHost.Start(pulse => pulse.On<SaleCompleted>("recorder", (sale, _, _) =>
        {
            received.Enqueue(sale.Number);
            return Task.CompletedTask;
        }));

        var result = await host.Pulse.EmitAll(new SaleStream("A"), new object[] { new SaleCompleted(1), new SaleCompleted(2), new SaleCompleted(3) });

        Assert.Equal(3, result.StreamPosition);
        await Eventually.True(() => received.Count == 3, "all three events are handled");
        Assert.Equal([1, 2, 3], received);
    }

    [Fact]
    public async Task TheEventsMayBeOfDifferentTypesAndTheParamsFormWorks()
    {
        await using var host = await PulseTestHost.Start(_ => { });

        var first = await host.EmitAll("Sales", new SaleCompleted(1), new SaleRefunded(1), new SaleCompleted(2));
        var second = await host.EmitAll("Sales", new SaleVoided(3));
        await host.WaitUntilIdle();

        Assert.Equal(3, first.StreamPosition);
        Assert.Equal(4, second.StreamPosition);
        Assert.Equal(
            [typeof(SaleCompleted), typeof(SaleRefunded), typeof(SaleCompleted), typeof(SaleVoided)],
            host.Events.Select(emitted => emitted.Event.GetType())
        );
    }

    [Fact]
    public async Task RefusesAnEmptyGroupAndAGroupWithANullEvent()
    {
        await using var host = await TestHost.Start(_ => { });

        var empty = await Assert.ThrowsAsync<ArgumentException>(() => host.Pulse.EmitAll(new SaleStream("A"), Array.Empty<object>()));
        var withNull = await Assert.ThrowsAsync<ArgumentException>(() => host.Pulse.EmitAll(new SaleStream("A"), new object[] { new SaleCompleted(1), null! }));

        Assert.Contains("at least one event", empty.Message, StringComparison.Ordinal);
        Assert.Contains("position 1", withNull.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheExpectedVersionAppliesToTheWholeGroupAndSavesNothingOnAMismatch()
    {
        var received = new ConcurrentQueue<int>();
        await using var host = await TestHost.Start(pulse => pulse.On<SaleCompleted>("recorder", (sale, _, _) =>
        {
            received.Enqueue(sale.Number);
            return Task.CompletedTask;
        }));
        var onlyNew = new EmitOptions { ExpectedVersion = ExpectedVersion.NoStream };

        await host.Pulse.EmitAll(new SaleStream("A"), new object[] { new SaleCompleted(1), new SaleCompleted(2) }, onlyNew);
        await Assert.ThrowsAsync<ExpectedVersionMismatchException>(
            () => host.Pulse.EmitAll(new SaleStream("A"), new object[] { new SaleCompleted(3), new SaleCompleted(4) }, onlyNew)
        );

        await Eventually.True(() => received.Count == 2, "the first group is handled");
        await Eventually.StaysTrue(() => received.Count == 2, "nothing from the refused group is saved");
    }

    [Fact]
    public async Task SqliteSavesAGroupInTheCallersTransactionOrNotAtAll()
    {
        using var database = new TempDatabase();
        var received = new ConcurrentQueue<int>();
        await using var host = await TestHost.Start(pulse =>
        {
            pulse.UseSqlite(database.ConnectionString);
            pulse.On<SaleCompleted>("recorder", (sale, _, _) =>
            {
                received.Enqueue(sale.Number);
                return Task.CompletedTask;
            });
        });
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync();

        await using (var rolledBack = (SqliteTransaction)await connection.BeginTransactionAsync())
        {
            await host.Pulse.EmitAll(new SaleStream("A"), new object[] { new SaleCompleted(1), new SaleCompleted(2) }, new EmitOptions { Transaction = rolledBack });
            await rolledBack.RollbackAsync();
        }

        await using (var committed = (SqliteTransaction)await connection.BeginTransactionAsync())
        {
            await host.Pulse.EmitAll(new SaleStream("A"), new object[] { new SaleCompleted(3), new SaleCompleted(4) }, new EmitOptions { Transaction = committed });
            await committed.CommitAsync();
        }

        await Eventually.True(() => received.Count == 2, "the committed group is handled");
        await Eventually.StaysTrue(() => received.Count == 2, "the rolled-back group never appears");
        Assert.Equal([3, 4], received);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task PostgreSqlSavesAGroupInTheCallersTransactionOrNotAtAll()
    {
        using var schema = TempSchema.CreateOrSkip();
        await using var store = new PostgreSqlStore(schema.ConnectionString, schema.Name);
        await store.Initialize(CancellationToken.None);
        await using var connection = new NpgsqlConnection(schema.ConnectionString);
        await connection.OpenAsync();
        var group = Enumerable
            .Range(1, 3)
            .Select(_ => new NewEvent(Guid.NewGuid(), "Sale-A", "SaleCompleted", "{}"u8.ToArray(), "till-1", DateTimeOffset.UtcNow))
            .ToList();

        await using (var rolledBack = await connection.BeginTransactionAsync())
        {
            await store.AppendAll(group, ExpectedVersion.NoStream, rolledBack, CancellationToken.None);
            await rolledBack.RollbackAsync();
        }

        Assert.Empty(await store.ReadStream("Sale-A", 0, 10, CancellationToken.None));

        await using (var committed = await connection.BeginTransactionAsync())
        {
            var again = group.Select(newEvent => newEvent with { EventId = Guid.NewGuid() }).ToList();
            await store.AppendAll(again, ExpectedVersion.NoStream, committed, CancellationToken.None);
            await committed.CommitAsync();
        }

        Assert.Equal([1, 2, 3], (await store.ReadStream("Sale-A", 0, 10, CancellationToken.None)).Select(stored => stored.StreamPosition));
    }

    [Fact]
    public async Task FakePulseRecordsEveryEventOfAGroupAndChecksTheVersionOnce()
    {
        var pulse = new FakePulse();
        var onlyNew = new EmitOptions { ExpectedVersion = ExpectedVersion.NoStream };

        var result = await pulse.EmitAll("Sales", new object[] { new SaleCompleted(1), new SaleCompleted(2) }, onlyNew);

        Assert.Equal(2, result.StreamPosition);
        Assert.Equal([1, 2], pulse.AllEmitted<SaleCompleted>("Sales").Select(sale => sale.Number));
        await Assert.ThrowsAsync<ExpectedVersionMismatchException>(() => pulse.EmitAll("Sales", new object[] { new SaleCompleted(3) }, onlyNew));
        Assert.Equal(2, pulse.Events.Count);
        pulse.ThrowOnEmit = new TimeoutException();
        await Assert.ThrowsAsync<TimeoutException>(() => pulse.EmitAll("Other", new object[] { new SaleCompleted(4) }));
    }
}
