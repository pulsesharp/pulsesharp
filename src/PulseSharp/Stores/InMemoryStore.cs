using System.Data.Common;

namespace PulseSharp.Stores;

internal sealed class InMemoryStore : ISyncStore
{
    private readonly object _gate = new();
    private readonly List<StoredEvent> _events = [];
    private readonly HashSet<Guid> _eventIds = [];
    private readonly Dictionary<(string Remote, SyncDirection Direction), long> _syncPositions = [];
    private readonly Dictionary<string, long> _streamPositions = new(StringComparer.Ordinal);
    private readonly Dictionary<(string HandlerName, string Stream), HandlerCursor> _cursors = [];
    private readonly Dictionary<Guid, StoredJob> _jobs = [];
    private readonly Dictionary<string, HandlerRecord> _handlers = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, long> _jobOrder = [];

    public Task Initialize(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task<StoredEvent> Append(
        NewEvent newEvent,
        ExpectedVersion expectedVersion,
        DbTransaction? transaction,
        CancellationToken cancellationToken
    )
    {
        lock (_gate)
        {
            _streamPositions.TryGetValue(newEvent.Stream, out var currentPosition);
            if (!expectedVersion.Allows(currentPosition))
            {
                throw new ExpectedVersionMismatchException(newEvent.Stream, expectedVersion, currentPosition);
            }

            var stored = new StoredEvent(
                GlobalPosition: _events.Count + 1,
                EventId: newEvent.EventId,
                Stream: newEvent.Stream,
                StreamPosition: currentPosition + 1,
                EventName: newEvent.EventName,
                Data: newEvent.Data,
                Origin: newEvent.Origin,
                EmittedAt: newEvent.EmittedAt
            );

            _events.Add(stored);
            _eventIds.Add(stored.EventId);
            _streamPositions[newEvent.Stream] = stored.StreamPosition;
            return Task.FromResult(stored);
        }
    }

    public Task<IReadOnlyList<StoredEvent>> AppendReplicated(IReadOnlyList<NewEvent> events, CancellationToken cancellationToken)
    {
        var saved = new List<StoredEvent>();
        lock (_gate)
        {
            foreach (var newEvent in events)
            {
                if (!_eventIds.Add(newEvent.EventId))
                {
                    continue;
                }

                _streamPositions.TryGetValue(newEvent.Stream, out var currentPosition);
                var stored = new StoredEvent(
                    GlobalPosition: _events.Count + 1,
                    EventId: newEvent.EventId,
                    Stream: newEvent.Stream,
                    StreamPosition: currentPosition + 1,
                    EventName: newEvent.EventName,
                    Data: newEvent.Data,
                    Origin: newEvent.Origin,
                    EmittedAt: newEvent.EmittedAt
                );

                _events.Add(stored);
                _streamPositions[newEvent.Stream] = stored.StreamPosition;
                saved.Add(stored);
            }
        }

        return Task.FromResult<IReadOnlyList<StoredEvent>>(saved);
    }

    public Task<IReadOnlyList<StoredEvent>> ReadForSync(SyncQuery query, CancellationToken cancellationToken)
    {
        var found = new List<StoredEvent>();
        lock (_gate)
        {
            for (var index = (int)query.AfterPosition; index < _events.Count && found.Count < query.MaxCount; index++)
            {
                var stored = _events[index];
                var matchesOrigin = query.Origin is null || stored.Origin == query.Origin;
                var matchesExcept = query.ExceptOrigin is null || stored.Origin != query.ExceptOrigin;
                var matchesStream = query.StreamPrefixes.Count == 0
                    || query.StreamPrefixes.Any(prefix => stored.Stream.StartsWith(prefix, StringComparison.Ordinal));

                if (matchesOrigin && matchesExcept && matchesStream)
                {
                    found.Add(stored);
                }
            }
        }

        return Task.FromResult<IReadOnlyList<StoredEvent>>(found);
    }

    public Task<long> LoadSyncPosition(string remote, SyncDirection direction, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_syncPositions.GetValueOrDefault((remote, direction)));
        }
    }

    public Task SaveSyncPosition(string remote, SyncDirection direction, long position, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _syncPositions[(remote, direction)] = position;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StoredEvent>> ReadAfter(
        long globalPosition,
        IReadOnlySet<string> eventNames,
        int maxCount,
        CancellationToken cancellationToken
    )
    {
        var found = new List<StoredEvent>();
        lock (_gate)
        {
            for (var index = (int)globalPosition; index < _events.Count && found.Count < maxCount; index++)
            {
                var stored = _events[index];
                if (eventNames.Contains(stored.EventName))
                {
                    found.Add(stored);
                }
            }
        }

        return Task.FromResult<IReadOnlyList<StoredEvent>>(found);
    }

    public Task<IReadOnlyList<HandlerCursor>> LoadCursors(string handlerName, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<HandlerCursor> cursors = _cursors.Values.Where(cursor => cursor.HandlerName == handlerName).ToList();
            return Task.FromResult(cursors);
        }
    }

    public Task<bool> SaveCursor(HandlerCursor cursor, long revision, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_handlers.TryGetValue(cursor.HandlerName, out var handler) || handler.Revision != revision)
            {
                return Task.FromResult(false);
            }

            _cursors[(cursor.HandlerName, cursor.Stream)] = cursor;
            return Task.FromResult(true);
        }
    }

    public Task<HandlerLeaseStatus> LeaseHandler(
        string handlerName,
        string workerId,
        DateTimeOffset now,
        TimeSpan duration,
        CancellationToken cancellationToken
    )
    {
        lock (_gate)
        {
            var handler = _handlers.GetValueOrDefault(handlerName) ?? new HandlerRecord(null, null, 0);
            var free = handler.Owner is null || handler.Owner == workerId || handler.LeaseUntil < now;
            if (free)
            {
                handler = handler with { Owner = workerId, LeaseUntil = now + duration };
            }

            _handlers[handlerName] = handler;
            return Task.FromResult(new HandlerLeaseStatus(free, handler.Revision));
        }
    }

    public Task ReleaseHandler(string handlerName, string workerId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_handlers.TryGetValue(handlerName, out var handler) && handler.Owner == workerId)
            {
                _handlers[handlerName] = handler with { Owner = null, LeaseUntil = null };
            }
        }

        return Task.CompletedTask;
    }

    public Task<long> ChangeCursors(string handlerName, IReadOnlyList<HandlerCursor> cursors, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var handler = _handlers.GetValueOrDefault(handlerName) ?? new HandlerRecord(null, null, 0);
            handler = handler with { Revision = handler.Revision + 1 };
            _handlers[handlerName] = handler;
            foreach (var cursor in cursors)
            {
                _cursors[(cursor.HandlerName, cursor.Stream)] = cursor;
            }

            return Task.FromResult(handler.Revision);
        }
    }

    public Task<IReadOnlyList<StoredEvent>> ReadStream(
        string stream,
        long afterStreamPosition,
        int maxCount,
        CancellationToken cancellationToken
    )
    {
        lock (_gate)
        {
            IReadOnlyList<StoredEvent> events = _events
                .Where(stored => stored.Stream == stream && stored.StreamPosition > afterStreamPosition)
                .Take(maxCount)
                .ToList();
            return Task.FromResult(events);
        }
    }

    public Task<StoredJob> AddJob(NewJob newJob, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        var job = new StoredJob(
            Id: newJob.JobId,
            JobName: newJob.JobName,
            Data: newJob.Data,
            State: JobState.Pending,
            Attempts: 0,
            Done: 0,
            Total: 0,
            LastError: null,
            EnqueuedAt: newJob.EnqueuedAt,
            RunAt: newJob.RunAt,
            StartedAt: null,
            FinishedAt: null,
            LeaseOwner: null,
            LeaseUntil: null,
            CancelRequested: false
        );

        lock (_gate)
        {
            _jobs.Add(job.Id, job);
            _jobOrder.Add(job.Id, _jobOrder.Count);
        }

        return Task.FromResult(job);
    }

    public Task<StoredJob?> ClaimNextJob(
        IReadOnlySet<string> jobNames,
        string workerId,
        DateTimeOffset now,
        TimeSpan lease,
        CancellationToken cancellationToken
    )
    {
        lock (_gate)
        {
            var next = _jobs.Values
                .Where(job => jobNames.Contains(job.JobName) && job.IsClaimableAt(now))
                .OrderBy(job => job.RunAt)
                .ThenBy(job => _jobOrder[job.Id])
                .FirstOrDefault();

            if (next is null)
            {
                return Task.FromResult<StoredJob?>(null);
            }

            var claimed = next with
            {
                State = JobState.Running,
                Attempts = next.Attempts + 1,
                StartedAt = now,
                LeaseOwner = workerId,
                LeaseUntil = now + lease
            };

            _jobs[claimed.Id] = claimed;
            return Task.FromResult<StoredJob?>(claimed);
        }
    }

    public Task<LeaseStatus> RenewLease(Guid jobId, string workerId, DateTimeOffset leaseUntil, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var job) || !job.IsHeldBy(workerId))
            {
                return Task.FromResult(new LeaseStatus(Held: false, CancelRequested: false));
            }

            _jobs[jobId] = job with { LeaseUntil = leaseUntil };
            return Task.FromResult(new LeaseStatus(Held: true, CancelRequested: job.CancelRequested));
        }
    }

    public Task SaveProgress(Guid jobId, string workerId, int done, int total, CancellationToken cancellationToken)
    {
        return UpdateHeldJob(jobId, workerId, job => job with { Done = done, Total = total });
    }

    public Task CompleteJob(Guid jobId, string workerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        return UpdateHeldJob(jobId, workerId, job => Finished(job, JobState.Succeeded, now));
    }

    public Task FailJobAttempt(
        Guid jobId,
        string workerId,
        string error,
        DateTimeOffset? retryAt,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        return UpdateHeldJob(jobId, workerId, job => retryAt is { } nextRun
            ? job with { State = JobState.Retrying, RunAt = nextRun, LastError = error, LeaseOwner = null, LeaseUntil = null }
            : Finished(job, JobState.Failed, now) with { LastError = error });
    }

    public Task CancelRunningJob(Guid jobId, string workerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        return UpdateHeldJob(jobId, workerId, job => Finished(job, JobState.Canceled, now));
    }

    public Task ReleaseJob(Guid jobId, string workerId, CancellationToken cancellationToken)
    {
        return UpdateHeldJob(jobId, workerId, job => job with
        {
            State = job.Attempts > 1 ? JobState.Retrying : JobState.Pending,
            Attempts = job.Attempts - 1,
            LeaseOwner = null,
            LeaseUntil = null
        });
    }

    public Task<StoredJob?> GetJob(Guid jobId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_jobs.GetValueOrDefault(jobId));
        }
    }

    public Task<IReadOnlyList<StoredJob>> GetJobs(JobState? state, int limit, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<StoredJob> jobs = _jobs.Values
                .Where(job => state is null || job.State == state)
                .OrderByDescending(job => _jobOrder[job.Id])
                .Take(limit)
                .ToList();
            return Task.FromResult(jobs);
        }
    }

    public Task<bool> RequestCancel(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var job))
            {
                return Task.FromResult(false);
            }

            if (job.State is JobState.Pending or JobState.Retrying)
            {
                _jobs[jobId] = Finished(job, JobState.Canceled, now);
                return Task.FromResult(true);
            }

            if (job.State == JobState.Running)
            {
                _jobs[jobId] = job with { CancelRequested = true };
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }
    }

    public Task<bool> RetryJob(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var job) || job.State is not (JobState.Failed or JobState.Canceled))
            {
                return Task.FromResult(false);
            }

            _jobs[jobId] = job with
            {
                State = JobState.Pending,
                Attempts = 0,
                Done = 0,
                Total = 0,
                LastError = null,
                RunAt = now,
                StartedAt = null,
                FinishedAt = null,
                CancelRequested = false
            };
            return Task.FromResult(true);
        }
    }

    private Task UpdateHeldJob(Guid jobId, string workerId, Func<StoredJob, StoredJob> update)
    {
        lock (_gate)
        {
            if (_jobs.TryGetValue(jobId, out var job) && job.IsHeldBy(workerId))
            {
                _jobs[jobId] = update(job);
            }
        }

        return Task.CompletedTask;
    }

    private static StoredJob Finished(StoredJob job, JobState state, DateTimeOffset now)
    {
        return job with
        {
            State = state,
            FinishedAt = now,
            LeaseOwner = null,
            LeaseUntil = null,
            CancelRequested = false
        };
    }

    private sealed record HandlerRecord(string? Owner, DateTimeOffset? LeaseUntil, long Revision);
}
