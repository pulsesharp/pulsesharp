using System.Text;
using PulseSharp.PostgreSql;
using PulseSharp.Sqlite;
using PulseSharp.Stores;

namespace PulseSharp.Tests;

public abstract class StoreConformanceTests : IDisposable
{
    protected static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, 123, TimeSpan.Zero);
    private static readonly IReadOnlySet<string> Import = Names("ImportProducts");
    private static readonly CancellationToken None = CancellationToken.None;

    protected abstract IPulseStore Store { get; }

    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task NumbersEventsPerStreamAndGlobally()
    {
        await Store.Initialize(None);

        var first = await Append("Sale-A", "SaleCompleted");
        var other = await Append("Sale-B", "SaleCompleted");
        var second = await Append("Sale-A", "SaleCompleted");

        Assert.Equal([1, 1, 2], [first.StreamPosition, other.StreamPosition, second.StreamPosition]);
        Assert.True(first.GlobalPosition < other.GlobalPosition && other.GlobalPosition < second.GlobalPosition);
    }

    [Fact]
    public async Task ChecksTheExpectedVersionAtomically()
    {
        await Append("Sale-A", "SaleCompleted", ExpectedVersion.NoStream);

        var conflict = await Assert.ThrowsAsync<ExpectedVersionMismatchException>(
            () => Append("Sale-A", "SaleCompleted", ExpectedVersion.NoStream)
        );
        var next = await Append("Sale-A", "SaleCompleted", ExpectedVersion.Exactly(1));

        Assert.Equal(1, conflict.ActualPosition);
        Assert.Equal(2, next.StreamPosition);
    }

    [Fact]
    public async Task ReadsEventsAfterAPositionFilteredByName()
    {
        var first = await Append("Sale-A", "SaleCompleted", data: "{\"number\":1}");
        await Append("Sale-A", "SaleRefunded");
        var third = await Append("Sale-B", "SaleCompleted", data: "{\"number\":3}");

        var all = await Store.ReadAfter(0, Names("SaleCompleted"), 100, None);
        var afterFirst = await Store.ReadAfter(first.GlobalPosition, Names("SaleCompleted"), 100, None);
        var limited = await Store.ReadAfter(0, Names("SaleCompleted", "SaleRefunded"), 2, None);

        Assert.Equal([first.EventId, third.EventId], all.Select(stored => stored.EventId));
        Assert.Contains("\"number\":", Encoding.UTF8.GetString(all[0].Data), StringComparison.Ordinal);
        Assert.Contains("1", Encoding.UTF8.GetString(all[0].Data), StringComparison.Ordinal);
        Assert.Equal(Now, all[0].EmittedAt);
        Assert.Equal("till-1", all[0].Origin);
        Assert.Equal([third.EventId], afterFirst.Select(stored => stored.EventId));
        Assert.Equal(2, limited.Count);
    }

    [Fact]
    public async Task SavesAndReplacesCursorsForTheCurrentRevision()
    {
        var revision = (await Store.LeaseHandler("receipts", "worker-a", Now, TimeSpan.FromMinutes(1), None)).Revision;
        var other = (await Store.LeaseHandler("other", "worker-a", Now, TimeSpan.FromMinutes(1), None)).Revision;

        Assert.True(await Store.SaveCursor(new HandlerCursor("receipts", "Sale-A", 1, CursorState.Active, 0, null, null), revision, None));
        Assert.True(await Store.SaveCursor(new HandlerCursor("receipts", "Sale-A", 2, CursorState.Retrying, 1, "boom", Now), revision, None));
        Assert.True(await Store.SaveCursor(new HandlerCursor("other", "Sale-A", 5, CursorState.Active, 0, null, null), other, None));

        var cursor = Assert.Single(await Store.LoadCursors("receipts", None));
        Assert.Equal(new HandlerCursor("receipts", "Sale-A", 2, CursorState.Retrying, 1, "boom", Now), cursor);
    }

    [Fact]
    public async Task RejectsCursorWritesFromAnOldRevision()
    {
        var old = (await Store.LeaseHandler("receipts", "worker-a", Now, TimeSpan.FromMinutes(1), None)).Revision;
        var current = await Store.ChangeCursors(
            "receipts",
            [new HandlerCursor("receipts", "Sale-A", 7, CursorState.Active, 0, null, null)],
            None
        );
        var afterChange = Assert.Single(await Store.LoadCursors("receipts", None));

        var stale = await Store.SaveCursor(new HandlerCursor("receipts", "Sale-A", 9, CursorState.Active, 0, null, null), old, None);
        var fresh = await Store.SaveCursor(new HandlerCursor("receipts", "Sale-A", 3, CursorState.Active, 0, null, null), current, None);

        Assert.Equal(7, afterChange.Position);
        Assert.False(stale);
        Assert.True(fresh);
        Assert.Equal(current, old + 1);
        Assert.Equal(3, Assert.Single(await Store.LoadCursors("receipts", None)).Position);
        Assert.Equal(current, (await Store.LeaseHandler("receipts", "worker-a", Now, TimeSpan.FromMinutes(1), None)).Revision);
        Assert.False(await Store.SaveCursor(new HandlerCursor("unknown", "Sale-A", 1, CursorState.Active, 0, null, null), 0, None));
    }

    [Fact]
    public async Task GivesAHandlerLeaseToOneWorkerAtATime()
    {
        var first = await Store.LeaseHandler("receipts", "worker-a", Now, TimeSpan.FromSeconds(30), None);
        var renewed = await Store.LeaseHandler("receipts", "worker-a", Now.AddSeconds(10), TimeSpan.FromSeconds(30), None);
        var refused = await Store.LeaseHandler("receipts", "worker-b", Now.AddSeconds(20), TimeSpan.FromSeconds(30), None);
        var takenOver = await Store.LeaseHandler("receipts", "worker-b", Now.AddSeconds(41), TimeSpan.FromSeconds(30), None);

        Assert.True(first.Held);
        Assert.True(renewed.Held);
        Assert.False(refused.Held);
        Assert.True(takenOver.Held);
        Assert.False((await Store.LeaseHandler("receipts", "worker-a", Now.AddSeconds(42), TimeSpan.FromSeconds(30), None)).Held);
    }

    [Fact]
    public async Task AReleasedHandlerLeaseCanBeTakenAtOnce()
    {
        await Store.LeaseHandler("receipts", "worker-a", Now, TimeSpan.FromMinutes(5), None);

        await Store.ReleaseHandler("receipts", "worker-b", None);
        var stillHeld = await Store.LeaseHandler("receipts", "worker-b", Now.AddSeconds(1), TimeSpan.FromMinutes(5), None);
        await Store.ReleaseHandler("receipts", "worker-a", None);
        var taken = await Store.LeaseHandler("receipts", "worker-b", Now.AddSeconds(2), TimeSpan.FromMinutes(5), None);

        Assert.False(stillHeld.Held);
        Assert.True(taken.Held);
    }

    [Fact]
    public async Task ReadsOneStreamInOrder()
    {
        await Append("Sale-A", "SaleCompleted");
        await Append("Sale-B", "SaleCompleted");
        await Append("Sale-A", "SaleRefunded");
        await Append("Sale-A", "SaleCompleted");

        var afterFirst = await Store.ReadStream("Sale-A", 1, 10, None);
        var limited = await Store.ReadStream("Sale-A", 0, 2, None);

        Assert.Equal([2L, 3L], afterFirst.Select(stored => stored.StreamPosition));
        Assert.Equal(["SaleRefunded", "SaleCompleted"], afterFirst.Select(stored => stored.EventName));
        Assert.Equal([1L, 2L], limited.Select(stored => stored.StreamPosition));
        Assert.Empty(await Store.ReadStream("Sale-Z", 0, 10, None));
    }

    [Fact]
    public async Task AddsAPendingJob()
    {
        var id = await AddJob(data: "{\"file\":\"products.csv\"}");

        var job = (await Store.GetJob(id, None))!;

        Assert.Equal(JobState.Pending, job.State);
        Assert.Contains("products.csv", Encoding.UTF8.GetString(job.Data), StringComparison.Ordinal);
        Assert.Equal(0, job.Attempts);
        Assert.Equal(Now, job.EnqueuedAt);
    }

    [Fact]
    public async Task ClaimsDueJobsEarliestFirstAndNeverBeforeRunAt()
    {
        var later = await AddJob(runAt: Now.AddMinutes(5));
        var due = await AddJob();

        var first = await Store.ClaimNextJob(Import, "worker-a", Now, TimeSpan.FromHours(1), None);
        var nothingElse = await Store.ClaimNextJob(Import, "worker-a", Now, TimeSpan.FromHours(1), None);
        var afterRunAt = await Store.ClaimNextJob(Import, "worker-a", Now.AddMinutes(5), TimeSpan.FromHours(1), None);

        Assert.Equal(due, first!.Id);
        Assert.Equal(JobState.Running, first.State);
        Assert.Equal(1, first.Attempts);
        Assert.Null(nothingElse);
        Assert.Equal(later, afterRunAt!.Id);
        Assert.Null(await Store.ClaimNextJob(Names("OtherJob"), "worker-a", Now.AddDays(1), TimeSpan.FromMinutes(1), None));
    }

    [Fact]
    public async Task LetsAnotherWorkerClaimAJobWhoseLeaseExpired()
    {
        var id = await AddJob();
        await Store.ClaimNextJob(Import, "worker-a", Now, TimeSpan.FromSeconds(30), None);

        var tooEarly = await Store.ClaimNextJob(Import, "worker-b", Now.AddSeconds(10), TimeSpan.FromSeconds(30), None);
        var taken = await Store.ClaimNextJob(Import, "worker-b", Now.AddSeconds(31), TimeSpan.FromSeconds(30), None);

        Assert.Null(tooEarly);
        Assert.Equal(id, taken!.Id);
        Assert.Equal(2, taken.Attempts);
        Assert.Equal("worker-b", taken.LeaseOwner);
    }

    [Fact]
    public async Task IgnoresAWorkerThatLostItsLease()
    {
        var id = await AddJob();
        await Store.ClaimNextJob(Import, "worker-a", Now, TimeSpan.FromSeconds(30), None);
        await Store.ClaimNextJob(Import, "worker-b", Now.AddSeconds(31), TimeSpan.FromSeconds(30), None);

        await Store.CompleteJob(id, "worker-a", Now.AddSeconds(32), None);
        var lease = await Store.RenewLease(id, "worker-a", Now.AddMinutes(1), None);

        Assert.False(lease.Held);
        Assert.Equal(JobState.Running, (await Store.GetJob(id, None))!.State);
    }

    [Fact]
    public async Task SavesProgressAndCompletes()
    {
        var id = await AddJob();
        await Store.ClaimNextJob(Import, "worker-a", Now, TimeSpan.FromMinutes(1), None);

        await Store.SaveProgress(id, "worker-a", 5, 10, None);
        var running = (await Store.GetJob(id, None))!;
        await Store.CompleteJob(id, "worker-a", Now.AddSeconds(5), None);
        var done = (await Store.GetJob(id, None))!;

        Assert.Equal((5, 10), (running.Done, running.Total));
        Assert.Equal(JobState.Succeeded, done.State);
        Assert.Equal(Now.AddSeconds(5), done.FinishedAt);
        Assert.Null(done.LeaseOwner);
    }

    [Fact]
    public async Task RetriesThenFailsAJob()
    {
        var id = await AddJob();
        await Store.ClaimNextJob(Import, "worker-a", Now, TimeSpan.FromMinutes(1), None);

        await Store.FailJobAttempt(id, "worker-a", "first error", Now.AddSeconds(2), Now, None);
        var retrying = (await Store.GetJob(id, None))!;
        var again = await Store.ClaimNextJob(Import, "worker-a", Now.AddSeconds(2), TimeSpan.FromMinutes(1), None);
        await Store.FailJobAttempt(id, "worker-a", "second error", null, Now.AddSeconds(3), None);
        var failed = (await Store.GetJob(id, None))!;

        Assert.Equal(JobState.Retrying, retrying.State);
        Assert.Equal(Now.AddSeconds(2), retrying.RunAt);
        Assert.Equal(2, again!.Attempts);
        Assert.Equal(JobState.Failed, failed.State);
        Assert.Equal("second error", failed.LastError);
    }

    [Fact]
    public async Task RetryJobMakesAFailedJobPendingAgain()
    {
        var id = await AddJob();
        await Store.ClaimNextJob(Import, "worker-a", Now, TimeSpan.FromMinutes(1), None);
        await Store.FailJobAttempt(id, "worker-a", "error", null, Now, None);

        Assert.True(await Store.RetryJob(id, Now.AddMinutes(1), None));
        Assert.False(await Store.RetryJob(id, Now.AddMinutes(1), None));

        var job = (await Store.GetJob(id, None))!;
        Assert.Equal(JobState.Pending, job.State);
        Assert.Equal(0, job.Attempts);
        Assert.Null(job.LastError);
        Assert.Equal(Now.AddMinutes(1), job.RunAt);
    }

    [Fact]
    public async Task CancelsWaitingJobsAtOnceAndFlagsRunningJobs()
    {
        var waiting = await AddJob(runAt: Now.AddHours(1));
        var running = await AddJob();
        await Store.ClaimNextJob(Import, "worker-a", Now, TimeSpan.FromMinutes(1), None);

        Assert.True(await Store.RequestCancel(waiting, Now, None));
        Assert.True(await Store.RequestCancel(running, Now, None));
        var lease = await Store.RenewLease(running, "worker-a", Now.AddMinutes(1), None);
        await Store.CancelRunningJob(running, "worker-a", Now.AddSeconds(1), None);

        Assert.Equal(JobState.Canceled, (await Store.GetJob(waiting, None))!.State);
        Assert.True(lease.CancelRequested);
        Assert.Equal(JobState.Canceled, (await Store.GetJob(running, None))!.State);
        Assert.False(await Store.RequestCancel(running, Now, None));
        Assert.False(await Store.RequestCancel(Guid.NewGuid(), Now, None));
    }

    [Fact]
    public async Task ReleasingAJobDoesNotCountTheAttempt()
    {
        var id = await AddJob();
        await Store.ClaimNextJob(Import, "worker-a", Now, TimeSpan.FromMinutes(1), None);

        await Store.ReleaseJob(id, "worker-a", None);

        var job = (await Store.GetJob(id, None))!;
        Assert.Equal(JobState.Pending, job.State);
        Assert.Equal(0, job.Attempts);
    }

    [Fact]
    public async Task ListsJobsNewestFirstAndByState()
    {
        var first = await AddJob();
        var second = await AddJob();
        await Store.ClaimNextJob(Import, "worker-a", Now, TimeSpan.FromMinutes(1), None);

        var all = await Store.GetJobs(null, 10, None);
        var pending = await Store.GetJobs(JobState.Pending, 10, None);

        Assert.Equal([second, first], all.Select(job => job.Id));
        Assert.Equal([second], pending.Select(job => job.Id));
        Assert.Single(await Store.GetJobs(null, 1, None));
    }

    protected Task<StoredEvent> Append(string stream, string eventName, ExpectedVersion expected = default, string data = "{}")
    {
        var newEvent = new NewEvent(Guid.NewGuid(), stream, eventName, Encoding.UTF8.GetBytes(data), "till-1", Now);
        return Store.Append(newEvent, expected, null, None);
    }

    private async Task<Guid> AddJob(DateTimeOffset? runAt = null, string data = "{}")
    {
        var job = await Store.AddJob(
            new NewJob(Guid.NewGuid(), "ImportProducts", Encoding.UTF8.GetBytes(data), Now, runAt ?? Now),
            null,
            None
        );
        return job.Id;
    }

    private static IReadOnlySet<string> Names(params string[] names)
    {
        return new HashSet<string>(names, StringComparer.Ordinal);
    }
}

public sealed class InMemoryStoreTests : StoreConformanceTests
{
    protected override IPulseStore Store { get; } = new InMemoryStore();
}

public sealed class SqliteStoreTests : StoreConformanceTests
{
    private readonly TempDatabase _database = new();

    public SqliteStoreTests()
    {
        Store = new SqliteStore(_database.ConnectionString);
    }

    protected override IPulseStore Store { get; }

    public override void Dispose()
    {
        _database.Dispose();
        base.Dispose();
    }
}

public sealed class PostgresStoreTests : StoreConformanceTests
{
    private readonly TempSchema _schema = TempSchema.CreateOrSkip();

    public PostgresStoreTests()
    {
        Store = new PostgresStore(_schema.ConnectionString, _schema.Name);
    }

    protected override IPulseStore Store { get; }

    public override void Dispose()
    {
        ((PostgresStore)Store).DisposeAsync().AsTask().GetAwaiter().GetResult();
        _schema.Dispose();
        base.Dispose();
    }
}
