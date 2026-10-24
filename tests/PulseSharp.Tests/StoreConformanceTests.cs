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

    protected abstract ISyncStore Store { get; }

    protected virtual bool DeletesEvents
    {
        get
        {
            return true;
        }
    }

    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task AppendsReplicatedEventsKeepingTheirIdentity()
    {
        await Append("Sale-A", "SaleCompleted");
        var fromTill = Replicated("Sale-A", "till-2");
        var other = Replicated("Product-1", "cloud");

        var saved = await Store.AppendReplicated([fromTill, other], None);
        var stored = await Store.ReadStream("Sale-A", 0, 10, None);

        Assert.Equal([fromTill.EventId, other.EventId], saved.Select(stored => stored.EventId));
        Assert.Equal(2, saved[0].StreamPosition);
        Assert.Equal(1, saved[1].StreamPosition);
        Assert.True(saved[0].GlobalPosition < saved[1].GlobalPosition);
        Assert.Equal("till-2", stored[1].Origin);
        Assert.Equal(Now.AddMinutes(-5), stored[1].EmittedAt);
        Assert.Contains("\"total\":", Encoding.UTF8.GetString(stored[1].Data), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SkipsReplicatedEventsItAlreadyHas()
    {
        var first = Replicated("Sale-A", "till-2");
        var second = Replicated("Sale-A", "till-2");
        await Store.AppendReplicated([first], None);

        var saved = await Store.AppendReplicated([first, second, second], None);

        Assert.Equal([second.EventId], saved.Select(stored => stored.EventId));
        Assert.Equal(2, saved[0].StreamPosition);
        Assert.Equal(2, (await Store.ReadStream("Sale-A", 0, 10, None)).Count);
        Assert.Empty(await Store.AppendReplicated([], None));
    }

    [Fact]
    public async Task ReadsEventsForSyncByOriginAndStreamPrefix()
    {
        var mine = await Append("Sale-A", "SaleCompleted");
        await Store.AppendReplicated([Replicated("Product-1", "cloud"), Replicated("Tenant-7-Price", "cloud")], None);
        var later = await Append("Product-2", "ProductChanged");

        var push = await Store.ReadForSync(new SyncQuery(0, "till-1", null, [], 100), None);
        var pull = await Store.ReadForSync(new SyncQuery(0, null, "till-1", ["Tenant-7-"], 100), None);
        var products = await Store.ReadForSync(new SyncQuery(0, null, null, ["Product-", "Nothing-"], 100), None);
        var afterMine = await Store.ReadForSync(new SyncQuery(mine.GlobalPosition, "till-1", null, [], 1), None);

        Assert.Equal([mine.EventId, later.EventId], push.Select(stored => stored.EventId));
        Assert.Equal(["Tenant-7-Price"], pull.Select(stored => stored.Stream));
        Assert.Equal(["Product-1", "Product-2"], products.Select(stored => stored.Stream));
        Assert.Equal([later.EventId], afterMine.Select(stored => stored.EventId));
    }

    [Fact]
    public async Task RemembersSyncPositionsPerRemoteAndDirection()
    {
        Assert.Equal(0, await Store.LoadSyncPosition("cloud", SyncDirection.Push, None));

        await Store.SaveSyncPosition("cloud", SyncDirection.Push, 5, None);
        await Store.SaveSyncPosition("cloud", SyncDirection.Pull, 40, None);
        await Store.SaveSyncPosition("cloud", SyncDirection.Push, 9, None);

        Assert.Equal(9, await Store.LoadSyncPosition("cloud", SyncDirection.Push, None));
        Assert.Equal(40, await Store.LoadSyncPosition("cloud", SyncDirection.Pull, None));
        Assert.Equal(0, await Store.LoadSyncPosition("other", SyncDirection.Pull, None));
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
    public async Task SavesSeveralCursorsAtOnceAndOnlyWhileTheRevisionMatches()
    {
        var revision = (await Store.LeaseHandler("receipts", "worker-a", Now, TimeSpan.FromSeconds(30), None)).Revision;
        var first = new HandlerCursor("receipts", "Sale-A", 3, CursorState.Active, 0, null, null);
        var second = new HandlerCursor("receipts", "Sale-B", 5, CursorState.Retrying, 2, "boom", Now);

        Assert.True(await Store.SaveCursors([first, second], revision, None));
        var saved = (await Store.LoadCursors("receipts", None)).OrderBy(cursor => cursor.Stream).ToList();

        Assert.Equal([3, 5], saved.Select(cursor => cursor.Position));
        Assert.Equal(CursorState.Retrying, saved[1].State);
        Assert.Equal("boom", saved[1].LastError);

        await Store.ChangeCursors("receipts", [], None);
        var later = new HandlerCursor("receipts", "Sale-A", 9, CursorState.Active, 0, null, null);
        var another = new HandlerCursor("receipts", "Sale-C", 1, CursorState.Active, 0, null, null);

        Assert.False(await Store.SaveCursors([later, another], revision, None));
        var afterwards = await Store.LoadCursors("receipts", None);
        Assert.Equal(3, afterwards.Single(cursor => cursor.Stream == "Sale-A").Position);
        Assert.DoesNotContain(afterwards, cursor => cursor.Stream == "Sale-C");
    }

    [Fact]
    public async Task SavingNoCursorsAtOnceIsFine()
    {
        var revision = (await Store.LeaseHandler("receipts", "worker-a", Now, TimeSpan.FromSeconds(30), None)).Revision;

        Assert.True(await Store.SaveCursors([], revision, None));
    }

    [Fact]
    public async Task AppendsSeveralEventsAtOnceInOrderAfterTheEventsAlreadyThere()
    {
        await Append("Sale-A", "SaleCompleted");
        var group = Group("Sale-A", 3);

        var saved = await Store.AppendAll(group, ExpectedVersion.Exactly(1), null, None);
        var stream = await Store.ReadStream("Sale-A", 0, 10, None);

        Assert.Equal(group.Select(newEvent => newEvent.EventId), saved.Select(stored => stored.EventId));
        Assert.Equal([2, 3, 4], saved.Select(stored => stored.StreamPosition));
        Assert.True(saved[0].GlobalPosition < saved[1].GlobalPosition && saved[1].GlobalPosition < saved[2].GlobalPosition);
        Assert.Equal([1, 2, 3, 4], stream.Select(stored => stored.StreamPosition));
        Assert.Equal(saved.Select(stored => stored.GlobalPosition), stream.Skip(1).Select(stored => stored.GlobalPosition));
    }

    [Fact]
    public async Task SavesNoneOfTheEventsWhenTheExpectedVersionDoesNotMatch()
    {
        await Append("Sale-A", "SaleCompleted");

        var error = await Assert.ThrowsAsync<ExpectedVersionMismatchException>(() => Store.AppendAll(Group("Sale-A", 3), ExpectedVersion.NoStream, null, None));

        Assert.Equal(1, error.ActualPosition);
        Assert.Single(await Store.ReadStream("Sale-A", 0, 10, None));
    }

    [Fact]
    public async Task AGroupOfEventsOnANewStreamStartsAtPositionOne()
    {
        var saved = await Store.AppendAll(Group("Menu", 2), ExpectedVersion.NoStream, null, None);

        Assert.Equal([1, 2], saved.Select(stored => stored.StreamPosition));
    }

    [Fact]
    public async Task CountsTheEventsForSyncExactlyAsManyAsItReads()
    {
        await Append("Sale-till-1-a", "SaleCompleted");
        await Store.AppendReplicated([Replicated("Sale-till-2-a", "till-2"), Replicated("Product-1", "cloud")], None);
        await Append("Sale-till-1-b", "SaleCompleted");
        await Append("Menu", "ProductAdded");
        var queries = new[]
        {
            new SyncQuery(0, null, null, [], 100),
            new SyncQuery(0, "till-1", null, [], 100),
            new SyncQuery(0, null, "till-1", [], 100),
            new SyncQuery(0, null, null, ["Sale-till-1-"], 100),
            new SyncQuery(0, null, null, ["Sale-till-1-", "Menu"], 100),
            new SyncQuery(2, "till-1", null, [], 100),
            new SyncQuery(0, null, null, [], 3),
            new SyncQuery(100, null, null, [], 100),
        };

        foreach (var query in queries)
        {
            var read = await Store.ReadForSync(query, None);
            Assert.Equal(read.Count, await Store.CountForSync(query, None));
        }

        Assert.Equal(3, await Store.CountForSync(new SyncQuery(0, "till-1", null, [], 100), None));
    }

    [Fact]
    public async Task DeletesOldEventsOnlyWhenEveryListeningHandlerHasHandledThemAndKeepsTheNewestOfEachStream()
    {
        Assert.SkipUnless(DeletesEvents, "This store keeps events only in memory and deletes nothing.");
        var old = Now.AddDays(-10);
        for (var number = 1; number <= 5; number++)
        {
            await AppendAt("Sale-A", "SaleCompleted", old);
        }

        await AppendAt("Menu", "SomethingNobodyListensTo", old);
        await AppendAt("Menu", "SomethingNobodyListensTo", old);
        await AppendAt("Fresh", "SaleCompleted", Now);
        await AppendAt("Fresh", "SaleCompleted", Now);
        await SetCursor("receipts", "Sale-A", 4);
        await SetCursor("ledger", "Sale-A", 2);
        var listeners = new[] { new EventListener("receipts", "SaleCompleted"), new EventListener("ledger", "SaleCompleted") };

        var deleted = await Store.DeleteHandledEvents(Now.AddDays(-1), listeners, "cloud", null, 100, None);

        Assert.Equal(3, deleted);
        Assert.Equal([3, 4, 5], (await Store.ReadStream("Sale-A", 0, 10, None)).Select(stored => stored.StreamPosition));
        Assert.Equal([2], (await Store.ReadStream("Menu", 0, 10, None)).Select(stored => stored.StreamPosition));
        Assert.Equal([1, 2], (await Store.ReadStream("Fresh", 0, 10, None)).Select(stored => stored.StreamPosition));
    }

    [Fact]
    public async Task KeepsOldEventsThatAHandlerHasNotReachedOrHasNoCursorFor()
    {
        Assert.SkipUnless(DeletesEvents, "This store keeps events only in memory and deletes nothing.");
        var old = Now.AddDays(-10);
        for (var number = 1; number <= 4; number++)
        {
            await AppendAt("Sale-A", "SaleCompleted", old);
        }

        var deleted = await Store.DeleteHandledEvents(Now.AddDays(-1), [new EventListener("receipts", "SaleCompleted")], "cloud", null, 100, None);

        Assert.Equal(0, deleted);
        Assert.Equal(4, (await Store.ReadStream("Sale-A", 0, 10, None)).Count);
    }

    [Fact]
    public async Task DoesNotDeleteTheDevicesOwnEventsBeforeTheyWereSent()
    {
        Assert.SkipUnless(DeletesEvents, "This store keeps events only in memory and deletes nothing.");
        var old = Now.AddDays(-10);
        var saved = new List<StoredEvent>();
        for (var number = 1; number <= 4; number++)
        {
            saved.Add(await AppendAt("Sale-till-1-a", "SaleCompleted", old, "till-1"));
        }

        await Store.AppendReplicated([Replicated("Menu", "cloud"), Replicated("Menu", "cloud")], None);
        var sentUpToTheSecond = saved[1].GlobalPosition;

        var deleted = await Store.DeleteHandledEvents(Now.AddDays(1), [], "till-1", sentUpToTheSecond, 100, None);

        Assert.Equal(3, deleted);
        Assert.Equal([3, 4], (await Store.ReadStream("Sale-till-1-a", 0, 10, None)).Select(stored => stored.StreamPosition));
        Assert.Single(await Store.ReadStream("Menu", 0, 10, None));
    }

    [Fact]
    public async Task WithoutSyncAllOldHandledEventsMayGoExceptTheNewestOfEachStream()
    {
        Assert.SkipUnless(DeletesEvents, "This store keeps events only in memory and deletes nothing.");
        var old = Now.AddDays(-10);
        for (var number = 1; number <= 4; number++)
        {
            await AppendAt("Sale-till-1-a", "SaleCompleted", old, "till-1");
        }

        var deleted = await Store.DeleteHandledEvents(Now.AddDays(-1), [], "till-1", null, 100, None);

        Assert.Equal(3, deleted);
        Assert.Equal([4], (await Store.ReadStream("Sale-till-1-a", 0, 10, None)).Select(stored => stored.StreamPosition));
    }

    [Fact]
    public async Task DeletesAtMostTheRequestedNumberOfEventsPerCall()
    {
        Assert.SkipUnless(DeletesEvents, "This store keeps events only in memory and deletes nothing.");
        for (var number = 1; number <= 10; number++)
        {
            await AppendAt("Sale-A", "SaleCompleted", Now.AddDays(-10));
        }

        var first = await Store.DeleteHandledEvents(Now.AddDays(-1), [], "cloud", null, 4, None);
        var second = await Store.DeleteHandledEvents(Now.AddDays(-1), [], "cloud", null, 4, None);
        var third = await Store.DeleteHandledEvents(Now.AddDays(-1), [], "cloud", null, 4, None);
        var nothingLeft = await Store.DeleteHandledEvents(Now.AddDays(-1), [], "cloud", null, 4, None);

        Assert.Equal((4, 4, 1, 0), (first, second, third, nothingLeft));
    }

    [Fact]
    public async Task AStreamKeepsCountingFromItsLastPositionAfterOldEventsWereDeleted()
    {
        Assert.SkipUnless(DeletesEvents, "This store keeps events only in memory and deletes nothing.");
        for (var number = 1; number <= 5; number++)
        {
            await AppendAt("Sale-A", "SaleCompleted", Now.AddDays(-10));
        }

        await Store.DeleteHandledEvents(Now.AddDays(-1), [], "cloud", null, 100, None);
        var next = await AppendAt("Sale-A", "SaleCompleted", Now);

        Assert.Equal(6, next.StreamPosition);
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
    public async Task TheLeaseStatusNamesTheHolderWhenSomeoneElseHoldsTheHandler()
    {
        var holder = await Store.LeaseHandler("receipts", "worker-a", Now, TimeSpan.FromSeconds(30), None);
        var refused = await Store.LeaseHandler("receipts", "worker-b", Now.AddSeconds(5), TimeSpan.FromSeconds(30), None);

        Assert.True(holder.Held);
        Assert.False(refused.Held);
        Assert.Equal("worker-a", refused.Owner);

        await Store.ReleaseHandler("receipts", "worker-a", None);
        var afterRelease = await Store.LeaseHandler("receipts", "worker-b", Now.AddSeconds(6), TimeSpan.FromSeconds(30), None);
        Assert.True(afterRelease.Held);
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

        Assert.Equal(due, first!.JobId);
        Assert.Equal(JobState.Running, first.State);
        Assert.Equal(1, first.Attempts);
        Assert.Null(nothingElse);
        Assert.Equal(later, afterRunAt!.JobId);
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
        Assert.Equal(id, taken!.JobId);
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

        Assert.Equal([second, first], all.Select(job => job.JobId));
        Assert.Equal([second], pending.Select(job => job.JobId));
        Assert.Single(await Store.GetJobs(null, 1, None));
    }

    [Fact]
    public async Task DeletesOnlyJobsThatSucceededOrWereCancelledBeforeTheCutoff()
    {
        var succeeded = await AddJob();
        await Store.ClaimNextJob(Import, "worker-a", Now, TimeSpan.FromMinutes(1), None);
        await Store.CompleteJob(succeeded, "worker-a", Now, None);
        var failed = await AddJob();
        await Store.ClaimNextJob(Import, "worker-a", Now, TimeSpan.FromMinutes(1), None);
        await Store.FailJobAttempt(failed, "worker-a", "boom", null, Now, None);
        var cancelled = await AddJob();
        await Store.RequestCancel(cancelled, Now, None);
        var recent = await AddJob();
        await Store.ClaimNextJob(Import, "worker-a", Now, TimeSpan.FromMinutes(1), None);
        await Store.CompleteJob(recent, "worker-a", Now.AddHours(2), None);
        var pending = await AddJob(runAt: Now.AddDays(-30));

        var firstBatch = await Store.DeleteFinishedJobs(Now.AddHours(1), 1, None);
        var secondBatch = await Store.DeleteFinishedJobs(Now.AddHours(1), 100, None);
        var nothingLeft = await Store.DeleteFinishedJobs(Now.AddHours(1), 100, None);

        Assert.Equal((1, 1, 0), (firstBatch, secondBatch, nothingLeft));
        Assert.Null(await Store.GetJob(succeeded, None));
        Assert.Null(await Store.GetJob(cancelled, None));
        Assert.Equal(JobState.Failed, (await Store.GetJob(failed, None))!.State);
        Assert.Equal(JobState.Succeeded, (await Store.GetJob(recent, None))!.State);
        Assert.Equal(JobState.Pending, (await Store.GetJob(pending, None))!.State);
        Assert.Equal(3, (await Store.GetJobs(null, 10, None)).Count);
    }

    private static List<NewEvent> Group(string stream, int count)
    {
        return Enumerable
            .Range(1, count)
            .Select(number => new NewEvent(Guid.NewGuid(), stream, "SaleCompleted", Encoding.UTF8.GetBytes($$"""{"number":{{number}}}"""), "till-1", Now))
            .ToList();
    }

    private Task<StoredEvent> AppendAt(string stream, string eventName, DateTimeOffset emittedAt, string origin = "cloud")
    {
        var newEvent = new NewEvent(Guid.NewGuid(), stream, eventName, "{}"u8.ToArray(), origin, emittedAt);
        return Store.Append(newEvent, ExpectedVersion.Any, null, None);
    }

    private async Task SetCursor(string handler, string stream, long position)
    {
        var revision = (await Store.LeaseHandler(handler, "worker-a", Now, TimeSpan.FromMinutes(5), None)).Revision;
        await Store.SaveCursor(new HandlerCursor(handler, stream, position, CursorState.Active, 0, null, null), revision, None);
    }

    protected Task<StoredEvent> Append(string stream, string eventName, ExpectedVersion expected = default, string data = "{}")
    {
        var newEvent = new NewEvent(Guid.NewGuid(), stream, eventName, Encoding.UTF8.GetBytes(data), "till-1", Now);
        return Store.Append(newEvent, expected, null, None);
    }

    private static NewEvent Replicated(string stream, string origin)
    {
        return new NewEvent(Guid.NewGuid(), stream, "Replicated", Encoding.UTF8.GetBytes("{\"total\":5}"), origin, Now.AddMinutes(-5));
    }

    private async Task<Guid> AddJob(DateTimeOffset? runAt = null, string data = "{}")
    {
        var job = await Store.AddJob(
            new NewJob(Guid.NewGuid(), "ImportProducts", Encoding.UTF8.GetBytes(data), Now, runAt ?? Now),
            null,
            None
        );
        return job.JobId;
    }

    private static IReadOnlySet<string> Names(params string[] names)
    {
        return new HashSet<string>(names, StringComparer.Ordinal);
    }
}

public sealed class InMemoryStoreTests : StoreConformanceTests
{
    protected override ISyncStore Store { get; } = new InMemoryStore();

    protected override bool DeletesEvents
    {
        get
        {
            return false;
        }
    }

    [Fact]
    public async Task TheInMemoryStoreDeletesNoEvents()
    {
        await Append("Sale-A", "SaleCompleted");
        await Append("Sale-A", "SaleCompleted");

        Assert.Equal(0, await Store.DeleteHandledEvents(Now.AddDays(1), [], "cloud", null, 100, CancellationToken.None));
        Assert.Equal(2, (await Store.ReadStream("Sale-A", 0, 10, CancellationToken.None)).Count);
    }
}

public sealed class SqliteStoreTests : StoreConformanceTests
{
    private readonly TempDatabase _database = new();

    public SqliteStoreTests()
    {
        Store = new SqliteStore(_database.ConnectionString);
    }

    protected override ISyncStore Store { get; }

    public override void Dispose()
    {
        _database.Dispose();
        base.Dispose();
    }
}

public sealed class PostgreSqlStoreTests : StoreConformanceTests
{
    private readonly TempSchema _schema = TempSchema.CreateOrSkip();

    public PostgreSqlStoreTests()
    {
        Store = new PostgreSqlStore(_schema.ConnectionString, _schema.Name);
    }

    protected override ISyncStore Store { get; }

    public override void Dispose()
    {
        ((PostgreSqlStore)Store).DisposeAsync().AsTask().GetAwaiter().GetResult();
        _schema.Dispose();
        base.Dispose();
    }
}
