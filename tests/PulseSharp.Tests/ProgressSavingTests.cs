using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace PulseSharp.Tests;

public sealed class ProgressSavingTests : IDisposable
{
    private const int BacklogSize = 300;

    private readonly TempDatabase _database = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _database.Dispose();
    }

    [Fact]
    public async Task StoppingTheAppInTheMiddleOfABacklogRepeatsNothingAfterTheRestart()
    {
        var firstRun = new ConcurrentQueue<int>();
        await using (var first = await StartHost(firstRun, blockAt: 250))
        {
            await EmitBacklog(first);
            await Eventually.True(() => firstRun.Count == 249, "the first app handles events 1 to 249 and then waits on 250");
        }

        var secondRun = new ConcurrentQueue<int>();
        await using (var second = await StartHost(secondRun))
        {
            await Eventually.True(() => secondRun.Count == BacklogSize - 249, "the restarted app handles the rest");
            await Eventually.StaysTrue(() => secondRun.Count == BacklogSize - 249, "no event is handled twice");
        }

        Assert.Equal(Enumerable.Range(250, BacklogSize - 249), secondRun);
    }

    [Fact]
    public async Task AFailureInTheMiddleOfABacklogKeepsEverythingBeforeItDone()
    {
        var firstRun = new ConcurrentQueue<int>();
        await using (var first = await StartHost(firstRun, failAt: 150))
        {
            await EmitBacklog(first);
            await WaitUntilFailed(first);

            var cursor = Assert.Single(await first.Control.GetCursors("recorder"));
            Assert.Equal(149, cursor.Position);
            Assert.Equal(149, firstRun.Count);
        }

        var secondRun = new ConcurrentQueue<int>();
        await using (var second = await StartHost(secondRun))
        {
            await second.Control.Retry("recorder", "Sale-A");
            try
            {
                await Eventually.True(() => secondRun.Count == BacklogSize - 149, "after the retry, the rest is handled");
            }
            catch
            {
                var cursors = string.Join("; ", (await second.Control.GetCursors("recorder")).Select(cursor => $"{cursor.Stream} pos={cursor.Position} {cursor.State} attempts={cursor.Attempts}"));
                Assert.Fail($"handled {secondRun.Count} of {BacklogSize - 149}: first={secondRun.FirstOrDefault()}, last={secondRun.LastOrDefault()}, distinct={secondRun.Distinct().Count()}. Cursors: {cursors}");
            }

            await Eventually.StaysTrue(() => secondRun.Count == BacklogSize - 149, "nothing before event 150 is handled again");
        }

        Assert.Equal(Enumerable.Range(150, BacklogSize - 149), secondRun);
    }

    private static async Task WaitUntilFailed(TestHost host)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var cursors = await host.Control.GetCursors("recorder");
            if (cursors.Any(cursor => cursor.State == CursorState.Failed))
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("The handler did not give up on event 150.");
    }

    private static async Task EmitBacklog(TestHost host)
    {
        for (var number = 1; number <= BacklogSize; number++)
        {
            await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(number));
        }
    }

    private Task<TestHost> StartHost(ConcurrentQueue<int> received, int blockAt = 0, int failAt = 0)
    {
        return TestHost.Start(pulse =>
        {
            pulse.UseSqlite(_database.ConnectionString);
            pulse.MaxAttempts = 2;
            pulse.On<SaleCompleted>("recorder", async (sale, _, cancellationToken) =>
            {
                if (sale.Number == blockAt)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                if (sale.Number == failAt)
                {
                    throw new InvalidOperationException("Failing on purpose.");
                }

                received.Enqueue(sale.Number);
            });
        });
    }
}
