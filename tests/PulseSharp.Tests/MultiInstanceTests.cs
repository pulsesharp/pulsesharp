using System.Collections.Concurrent;

namespace PulseSharp.Tests;

public abstract class MultiInstanceTests : IDisposable
{
    protected abstract void UseSharedStore(PulseSharpBuilder pulse);

    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task EachEventIsHandledOnceAcrossTwoInstances()
    {
        var handled = new ConcurrentQueue<(string Instance, string Stream, int Number)>();
        await using var first = await StartInstance("first", handled);
        await using var second = await StartInstance("second", handled);

        for (var number = 0; number < 30; number++)
        {
            var emitter = number % 2 == 0 ? first : second;
            await emitter.Pulse.Emit(new SaleStream($"{number % 5}"), new SaleCompleted(number));
        }

        await Eventually.True(() => handled.Count >= 30, "all thirty events are handled");
        await Eventually.StaysTrue(() => handled.Count == 30, "no event is handled twice");
        Assert.Equal(30, handled.Select(item => item.Number).Distinct().Count());
        foreach (var stream in handled.GroupBy(item => item.Stream))
        {
            var numbers = stream.Select(item => item.Number).ToList();
            Assert.Equal(numbers.Order(), numbers);
        }
    }

    [Fact]
    public async Task AnotherInstanceTakesOverWhenTheHolderStops()
    {
        var handled = new ConcurrentQueue<(string Instance, string Stream, int Number)>();
        var first = await StartInstance("first", handled);
        await using var second = await StartInstance("second", handled);

        await first.Pulse.Emit(new SaleStream("A"), new SaleCompleted(1));
        await Eventually.True(() => handled.Count == 1, "the first event is handled");
        var holder = handled.Single().Instance;

        var survivor = holder == "first" ? second : first;
        await (holder == "first" ? first : second).DisposeAsync();

        await survivor.Pulse.Emit(new SaleStream("A"), new SaleCompleted(2));
        await Eventually.True(() => handled.Count == 2, "the second event is handled by the other instance");
        await Eventually.StaysTrue(() => handled.Count == 2, "nothing is handled twice");
        Assert.NotEqual(holder, handled.Last().Instance);

        if (holder != "first")
        {
            await first.DisposeAsync();
        }
    }

    [Fact]
    public async Task ControlFromAnotherInstanceReachesTheHolder()
    {
        var fixedIt = 0;
        var handled = new ConcurrentQueue<int>();
        await using var first = await StartFailingInstance(handled, () => Volatile.Read(ref fixedIt) == 1);
        await using var second = await StartFailingInstance(handled, () => Volatile.Read(ref fixedIt) == 1);

        await first.Pulse.Emit(new SaleStream("A"), new SaleCompleted(1));
        await Eventually.True(
            () => second.Control.GetCursors("picky").Result.SingleOrDefault()?.State == CursorState.Failed,
            "the stream fails"
        );

        Volatile.Write(ref fixedIt, 1);
        await second.Control.Retry("picky", "Sale-A");

        await Eventually.True(() => handled.Count == 1, "the retried event is handled");
        await Eventually.True(
            () => first.Control.GetCursors("picky").Result.Single() is { State: CursorState.Active, Position: 1 },
            "the cursor moves on"
        );
    }

    private Task<TestHost> StartInstance(string instance, ConcurrentQueue<(string, string, int)> handled)
    {
        return TestHost.Start(pulse =>
        {
            UseSharedStore(pulse);
            pulse.Node = instance;
            pulse.On<SaleCompleted>("ledger", (sale, context, _) =>
            {
                handled.Enqueue((instance, context.Stream, sale.Number));
                return Task.CompletedTask;
            });
        });
    }

    private Task<TestHost> StartFailingInstance(ConcurrentQueue<int> handled, Func<bool> isFixed)
    {
        return TestHost.Start(pulse =>
        {
            UseSharedStore(pulse);
            pulse.MaxAttempts = 1;
            pulse.On<SaleCompleted>("picky", (sale, _, _) =>
            {
                if (!isFixed())
                {
                    throw new InvalidOperationException("Not yet.");
                }

                handled.Enqueue(sale.Number);
                return Task.CompletedTask;
            });
        });
    }
}

public sealed class SqliteMultiInstanceTests : MultiInstanceTests
{
    private readonly TempDatabase _database = new();

    protected override void UseSharedStore(PulseSharpBuilder pulse)
    {
        pulse.UseSqlite(_database.ConnectionString);
    }

    public override void Dispose()
    {
        _database.Dispose();
        base.Dispose();
    }
}

public sealed class PostgreSqlMultiInstanceTests : MultiInstanceTests
{
    private readonly TempSchema _schema = TempSchema.CreateOrSkip();

    protected override void UseSharedStore(PulseSharpBuilder pulse)
    {
        pulse.UsePostgreSql(_schema.ConnectionString, _schema.Name);
    }

    public override void Dispose()
    {
        _schema.Dispose();
        base.Dispose();
    }
}
