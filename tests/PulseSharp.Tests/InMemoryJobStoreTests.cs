using PulseSharp.Stores;

namespace PulseSharp.Tests;

public sealed class InMemoryJobStoreTests
{
    private static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.Ordinal) { "ImportProducts" };
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AJobWhoseLeaseExpiredCanBeClaimedByAnotherWorker()
    {
        var store = new InMemoryStore();
        var id = await AddJob(store);

        var first = await store.ClaimNextJob(Names, "worker-a", Now, TimeSpan.FromSeconds(30), CancellationToken.None);
        var tooEarly = await store.ClaimNextJob(Names, "worker-b", Now.AddSeconds(10), TimeSpan.FromSeconds(30), CancellationToken.None);
        var afterExpiry = await store.ClaimNextJob(Names, "worker-b", Now.AddSeconds(31), TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(id, first!.Id);
        Assert.Null(tooEarly);
        Assert.Equal(id, afterExpiry!.Id);
        Assert.Equal(2, afterExpiry.Attempts);
        Assert.Equal("worker-b", afterExpiry.LeaseOwner);
    }

    [Fact]
    public async Task AWorkerThatLostItsLeaseCannotFinishTheJob()
    {
        var store = new InMemoryStore();
        var id = await AddJob(store);
        await store.ClaimNextJob(Names, "worker-a", Now, TimeSpan.FromSeconds(30), CancellationToken.None);
        await store.ClaimNextJob(Names, "worker-b", Now.AddSeconds(31), TimeSpan.FromSeconds(30), CancellationToken.None);

        await store.CompleteJob(id, "worker-a", Now.AddSeconds(32), CancellationToken.None);
        var lease = await store.RenewLease(id, "worker-a", Now.AddSeconds(60), CancellationToken.None);

        Assert.Equal(JobState.Running, (await store.GetJob(id, CancellationToken.None))!.State);
        Assert.False(lease.Held);
    }

    [Fact]
    public async Task ReleasingAJobDoesNotCountTheAttempt()
    {
        var store = new InMemoryStore();
        var id = await AddJob(store);
        await store.ClaimNextJob(Names, "worker-a", Now, TimeSpan.FromSeconds(30), CancellationToken.None);

        await store.ReleaseJob(id, "worker-a", CancellationToken.None);

        var job = (await store.GetJob(id, CancellationToken.None))!;
        Assert.Equal(JobState.Pending, job.State);
        Assert.Equal(0, job.Attempts);
    }

    private static async Task<Guid> AddJob(InMemoryStore store)
    {
        var job = await store.AddJob(
            new NewJob(Guid.NewGuid(), "ImportProducts", new ImportProducts("a.csv"), Now, Now),
            CancellationToken.None
        );
        return job.Id;
    }
}
