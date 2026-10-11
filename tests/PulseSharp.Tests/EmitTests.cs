namespace PulseSharp.Tests;

public sealed class EmitTests
{
    [Fact]
    public async Task NumbersEventsPerStream()
    {
        await using var host = await TestHost.Start(_ => { });

        var first = await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(1));
        var other = await host.Pulse.Emit(new SaleStream("B"), new SaleCompleted(2));
        var second = await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(3));

        Assert.Equal(1, first.StreamPosition);
        Assert.Equal(1, other.StreamPosition);
        Assert.Equal(2, second.StreamPosition);
    }

    [Fact]
    public async Task GivesEveryEventAUniqueVersion7Id()
    {
        await using var host = await TestHost.Start(_ => { });

        var first = await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(1));
        var second = await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(2));

        Assert.NotEqual(first.EventId, second.EventId);
        Assert.Equal('7', first.EventId.ToString()[14]);
    }

    [Fact]
    public async Task NoStreamSucceedsOnlyForANewStream()
    {
        await using var host = await TestHost.Start(_ => { });
        var options = new EmitOptions { ExpectedVersion = ExpectedVersion.NoStream };

        await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(1), options);
        var exception = await Assert.ThrowsAsync<ExpectedVersionMismatchException>(
            () => host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(2), options)
        );

        Assert.Equal("Sale-A", exception.Stream);
        Assert.Equal(1, exception.ActualPosition);
    }

    [Fact]
    public async Task StreamExistsFailsForANewStream()
    {
        await using var host = await TestHost.Start(_ => { });
        var options = new EmitOptions { ExpectedVersion = ExpectedVersion.StreamExists };

        var exception = await Assert.ThrowsAsync<ExpectedVersionMismatchException>(
            () => host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(1), options)
        );

        Assert.Equal(0, exception.ActualPosition);
    }

    [Fact]
    public async Task ExactlySucceedsOnlyAtThatPosition()
    {
        await using var host = await TestHost.Start(_ => { });
        await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(1));
        await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(2));

        var stale = new EmitOptions { ExpectedVersion = ExpectedVersion.Exactly(1) };
        var current = new EmitOptions { ExpectedVersion = ExpectedVersion.Exactly(2) };

        await Assert.ThrowsAsync<ExpectedVersionMismatchException>(
            () => host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(3), stale)
        );
        var result = await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(3), current);

        Assert.Equal(3, result.StreamPosition);
    }

    [Fact]
    public async Task RejectsAnEmptyStreamName()
    {
        await using var host = await TestHost.Start(_ => { });

        await Assert.ThrowsAsync<ArgumentException>(
            () => host.Pulse.Emit(new BlankStream(), new SaleCompleted(1))
        );
    }
}
