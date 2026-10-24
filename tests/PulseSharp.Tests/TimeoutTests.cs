using System.Collections.Concurrent;

namespace PulseSharp.Tests;

public sealed class TimeoutTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(150);

    [Fact]
    public async Task AHandlerThatIgnoresCancellationIsAbandonedAndEndsFailedWithAClearMessage()
    {
        var other = new ConcurrentQueue<int>();
        await using var host = await TestHost.Start(pulse =>
        {
            pulse.Advanced.HandlerTimeout = Limit;
            pulse.MaxAttempts = 2;
            pulse.On<SaleCompleted>("hanging", async (sale, context, _) =>
            {
                if (context.Stream == "Sale-A")
                {
                    await Task.Delay(Timeout.Infinite);
                }

                other.Enqueue(sale.Number);
            });
        });

        await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(1));
        await host.Pulse.Emit(new SaleStream("B"), new SaleCompleted(2));

        var failed = await WaitForFailedCursor(host, "hanging");
        Assert.Contains("Advanced.HandlerTimeout", failed.LastError, StringComparison.Ordinal);
        Assert.Contains("took longer", failed.LastError, StringComparison.Ordinal);
        await Eventually.True(() => other.Count == 1, "another stream is not held up by the stuck one");
    }

    [Fact]
    public async Task AHandlerThatHonorsItsTokenIsStoppedAtTheTimeout()
    {
        await using var host = await TestHost.Start(pulse =>
        {
            pulse.Advanced.HandlerTimeout = Limit;
            pulse.MaxAttempts = 2;
            pulse.On<SaleCompleted>("polite", (_, _, cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken));
        });

        await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(1));

        var failed = await WaitForFailedCursor(host, "polite");
        Assert.Contains("took longer", failed.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHandlerThatHangsOnceHealsOnTheRetryAndTheStreamGoesOn()
    {
        var handled = new ConcurrentQueue<int>();
        var attempts = 0;
        await using var host = await TestHost.Start(pulse =>
        {
            pulse.Advanced.HandlerTimeout = Limit;
            pulse.On<SaleCompleted>("healing", async (sale, _, _) =>
            {
                if (sale.Number == 1 && Interlocked.Increment(ref attempts) == 1)
                {
                    await Task.Delay(Timeout.Infinite);
                }

                handled.Enqueue(sale.Number);
            });
        });

        await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(1));
        await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(2));

        await Eventually.True(() => handled.Count == 2, "both events are handled after the first attempt was abandoned");
        Assert.Equal([1, 2], handled);
        Assert.DoesNotContain(await host.Control.GetCursors("healing"), cursor => cursor.State == CursorState.Failed);
    }

    [Fact]
    public async Task AHandlerThatIsOnlySlowIsLeftAlone()
    {
        var handled = new ConcurrentQueue<int>();
        await using var host = await TestHost.Start(pulse =>
        {
            pulse.Advanced.HandlerTimeout = TimeSpan.FromSeconds(5);
            pulse.On<SaleCompleted>("slow", async (sale, _, cancellationToken) =>
            {
                await Task.Delay(300, cancellationToken);
                handled.Enqueue(sale.Number);
            });
        });

        await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(1));

        await Eventually.True(() => handled.Count == 1, "a slow handler within its limit finishes");
        Assert.DoesNotContain(await host.Control.GetCursors("slow"), cursor => cursor.State != CursorState.Active);
    }

    [Fact]
    public async Task WithoutALimitAHandlerMayTakeAsLongAsItWants()
    {
        var handled = new ConcurrentQueue<int>();
        await using var host = await TestHost.Start(pulse =>
        {
            pulse.Advanced.HandlerTimeout = null;
            pulse.On<SaleCompleted>("unlimited", async (sale, _, cancellationToken) =>
            {
                await Task.Delay(400, cancellationToken);
                handled.Enqueue(sale.Number);
            });
        });

        await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(1));

        await Eventually.True(() => handled.Count == 1, "the handler finishes");
    }

    [Fact]
    public async Task AJobThatHangsIsAbandonedAndEndsFailedWithAClearMessage()
    {
        await using var host = await TestHost.Start(pulse =>
        {
            pulse.Advanced.JobTimeout = Limit;
            pulse.MaxAttempts = 2;
            pulse.OnJob<ImportProducts>(async (_, _, _) => await Task.Delay(Timeout.Infinite));
        });

        var jobId = await host.Pulse.Enqueue(new ImportProducts("never.csv"));

        JobInfo? job = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            job = await host.Control.GetJob(jobId);
            if (job is { State: JobState.Failed })
            {
                break;
            }

            await Task.Delay(50);
        }

        Assert.NotNull(job);
        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("Advanced.JobTimeout", job.LastError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ALimitOfZeroOrLessIsRefusedAtStartup(int seconds)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TestHost.Start(pulse => pulse.Advanced.HandlerTimeout = TimeSpan.FromSeconds(seconds))
        );

        Assert.Contains("HandlerTimeout", error.Message, StringComparison.Ordinal);
    }

    private static async Task<HandlerCursor> WaitForFailedCursor(TestHost host, string handlerName)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var failed = (await host.Control.GetCursors(handlerName)).FirstOrDefault(cursor => cursor.State == CursorState.Failed);
            if (failed is not null)
            {
                return failed;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Handler '{handlerName}' did not end up failed.");
        return null!;
    }
}
