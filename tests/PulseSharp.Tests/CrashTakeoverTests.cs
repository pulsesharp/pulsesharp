using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using PulseSharp.Stores;

namespace PulseSharp.Tests;

public sealed class CrashTakeoverTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task ARestartedAppTakesOverAtOnceFromAProcessOnThisMachineThatIsGone()
    {
        var store = new InMemoryStore();
        var gone = await WorkerOfAProcessThatHasEnded();
        await store.LeaseHandler("recorder", gone, DateTimeOffset.UtcNow, TimeSpan.FromHours(1), None);
        var handled = new ConcurrentQueue<int>();

        await using var host = await StartHost(store, handled);
        await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(1));

        await Eventually.True(() => handled.Count == 1, "the handler runs at once, without waiting an hour for the old lease");
    }

    [Fact]
    public async Task AHandlerHeldByAnotherLiveProcessIsLeftAlone()
    {
        var store = new InMemoryStore();
        await store.LeaseHandler("recorder", WorkerIds.Create("other-app"), DateTimeOffset.UtcNow, TimeSpan.FromHours(1), None);
        var handled = new ConcurrentQueue<int>();

        await using var host = await StartHost(store, handled);
        await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(1));

        await Eventually.StaysTrue(() => handled.IsEmpty, "the handler stays with its live holder");
    }

    [Fact]
    public async Task AHandlerHeldFromAnotherMachineIsLeftAloneUntilItsLeaseRunsOut()
    {
        var store = new InMemoryStore();
        var gone = await WorkerOfAProcessThatHasEnded();
        var otherMachine = gone.Replace($":{Environment.MachineName}:", ":some-other-machine:", StringComparison.Ordinal);
        await store.LeaseHandler("recorder", otherMachine, DateTimeOffset.UtcNow, TimeSpan.FromHours(1), None);
        var handled = new ConcurrentQueue<int>();

        await using var host = await StartHost(store, handled);
        await host.Pulse.Emit(new SaleStream("A"), new SaleCompleted(1));

        await Eventually.StaysTrue(() => handled.IsEmpty, "a lease from another machine is only given up when it runs out");
    }

    [Fact]
    public async Task AJobOfAProcessThatIsGoneRunsAgainAtOnceAndCountsAsAnAttempt()
    {
        var store = new InMemoryStore();
        var gone = await WorkerOfAProcessThatHasEnded();
        var job = await store.AddJob(
            new NewJob(Guid.NewGuid(), "ImportProducts", Encoding.UTF8.GetBytes("""{"file":"x.csv"}"""), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            null,
            None
        );
        await store.ClaimNextJob(new HashSet<string> { "ImportProducts" }, gone, DateTimeOffset.UtcNow, TimeSpan.FromHours(1), None);
        var ran = new ConcurrentQueue<string>();

        await using var host = await TestHost.Start(pulse =>
        {
            pulse.UseStore(store);
            pulse.OnJob<ImportProducts>((import, _, _) =>
            {
                ran.Enqueue(import.File);
                return Task.CompletedTask;
            });
        });

        await Eventually.True(() => ran.Count == 1, "the job runs again at once, without waiting an hour for the old lease");
        var finished = await host.Control.GetJob(job.JobId);
        Assert.Equal(2, finished!.Attempts);
    }

    [Fact]
    public async Task TheLeaseStatusSaysWhoHoldsTheHandler()
    {
        var store = new InMemoryStore();
        await store.LeaseHandler("recorder", "worker-a", DateTimeOffset.UtcNow, TimeSpan.FromHours(1), None);

        var status = await store.LeaseHandler("recorder", "worker-b", DateTimeOffset.UtcNow, TimeSpan.FromHours(1), None);

        Assert.False(status.Held);
        Assert.Equal("worker-a", status.Owner);
    }

    private static Task<TestHost> StartHost(InMemoryStore store, ConcurrentQueue<int> handled)
    {
        return TestHost.Start(pulse =>
        {
            pulse.UseStore(store);
            pulse.On<SaleCompleted>("recorder", (sale, _, _) =>
            {
                handled.Enqueue(sale.Number);
                return Task.CompletedTask;
            });
        });
    }

    private static async Task<string> WorkerOfAProcessThatHasEnded()
    {
        using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--version") { RedirectStandardOutput = true })!;
        var startTicks = process.StartTime.ToUniversalTime().Ticks;
        await process.WaitForExitAsync();
        return $"crashed-app:{Environment.MachineName}:{process.Id}:{startTicks}:{Guid.NewGuid():N}";
    }
}
