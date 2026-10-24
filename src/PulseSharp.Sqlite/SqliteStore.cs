using System.Data.Common;
using Microsoft.Data.Sqlite;
using PulseSharp.Stores;

namespace PulseSharp.Sqlite;

/// <summary>
/// Keeps PulseSharp's events, cursors and jobs in a SQLite database. Tables are created and upgraded on first use;
/// their names start with <c>pulsesharp_</c>, so they can share a database with your own tables.
/// </summary>
public sealed class SqliteStore : ISyncStore
{
    private const string JobColumns =
        "job_id, job_name, data, state, attempts, done, total, last_error, enqueued_at, run_at, " +
        "started_at, finished_at, lease_owner, lease_until, cancel_requested";

    private const string HeldBy = "job_id = $id AND state = 1 AND lease_owner = $worker";

    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private volatile bool _schemaReady;

    /// <summary>
    /// Creates the store.
    /// </summary>
    /// <param name="database">A file name such as <c>app.db</c>, or a Microsoft.Data.Sqlite connection string such as <c>Data Source=app.db</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="database"/> is empty.</exception>
    public SqliteStore(string database)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        _connectionString = database.Contains('=', StringComparison.Ordinal)
            ? database
            : new SqliteConnectionStringBuilder { DataSource = database }.ToString();
    }

    /// <inheritdoc />
    public Task Initialize(CancellationToken cancellationToken)
    {
        return EnsureSchema(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<StoredEvent> Append(
        NewEvent newEvent,
        ExpectedVersion expectedVersion,
        DbTransaction? transaction,
        CancellationToken cancellationToken
    )
    {
        if (transaction is not null)
        {
            var outer = OuterTransaction(transaction);
            await EnsureSchema(cancellationToken).ConfigureAwait(false);
            return await AppendIn(outer.Connection!, outer, newEvent, expectedVersion, cancellationToken).ConfigureAwait(false);
        }

        await using var connection = await Open(cancellationToken).ConfigureAwait(false);
        await using var own = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var stored = await AppendIn(connection, own, newEvent, expectedVersion, cancellationToken).ConfigureAwait(false);
        await own.CommitAsync(cancellationToken).ConfigureAwait(false);
        return stored;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredEvent>> AppendAll(
        IReadOnlyList<NewEvent> events,
        ExpectedVersion expectedVersion,
        DbTransaction? transaction,
        CancellationToken cancellationToken
    )
    {
        if (transaction is not null)
        {
            var outer = OuterTransaction(transaction);
            await EnsureSchema(cancellationToken).ConfigureAwait(false);
            return await AppendAllIn(outer.Connection!, outer, events, expectedVersion, cancellationToken).ConfigureAwait(false);
        }

        await using var connection = await Open(cancellationToken).ConfigureAwait(false);
        await using var own = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var stored = await AppendAllIn(connection, own, events, expectedVersion, cancellationToken).ConfigureAwait(false);
        await own.CommitAsync(cancellationToken).ConfigureAwait(false);
        return stored;
    }

    private async Task<IReadOnlyList<StoredEvent>> AppendAllIn(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<NewEvent> events,
        ExpectedVersion expectedVersion,
        CancellationToken cancellationToken
    )
    {
        var saved = new List<StoredEvent>(events.Count);
        for (var index = 0; index < events.Count; index++)
        {
            var expected = index == 0 ? expectedVersion : ExpectedVersion.Any;
            saved.Add(await AppendIn(connection, transaction, events[index], expected, cancellationToken).ConfigureAwait(false));
        }

        return saved;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredEvent>> ReadAfter(
        long globalPosition,
        IReadOnlySet<string> eventNames,
        int maxCount,
        CancellationToken cancellationToken
    )
    {
        if (eventNames.Count == 0)
        {
            return [];
        }

        await using var connection = await Open(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT global_position, event_id, stream, stream_position, event_name, data, origin, emitted_at
            FROM pulsesharp_events
            WHERE global_position > $after AND event_name IN ({NameParameters(command, eventNames)})
            ORDER BY global_position
            LIMIT $max;
            """;
        command.Parameters.AddWithValue("$after", globalPosition);
        command.Parameters.AddWithValue("$max", maxCount);

        return await ReadEvents(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HandlerCursor>> LoadCursors(string handlerName, CancellationToken cancellationToken)
    {
        await using var connection = await Open(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT stream, position, state, attempts, last_error, retry_at
            FROM pulsesharp_cursors
            WHERE handler_name = $handler;
            """;
        command.Parameters.AddWithValue("$handler", handlerName);

        var cursors = new List<HandlerCursor>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cursors.Add(new HandlerCursor(
                handlerName,
                reader.GetString(0),
                reader.GetInt64(1),
                (CursorState)reader.GetInt32(2),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : FromTicks(reader.GetInt64(5))
            ));
        }

        return cursors;
    }

    /// <inheritdoc />
    public async Task<bool> SaveCursor(HandlerCursor cursor, long revision, CancellationToken cancellationToken)
    {
        var saved = await ExecuteNonQuery(
            """
            INSERT INTO pulsesharp_cursors (handler_name, stream, position, state, attempts, last_error, retry_at)
            SELECT $handler, $stream, $position, $state, $attempts, $error, $retryAt
            WHERE EXISTS (SELECT 1 FROM pulsesharp_handlers WHERE handler_name = $handler AND revision = $revision)
            ON CONFLICT (handler_name, stream) DO UPDATE SET
                position = excluded.position, state = excluded.state, attempts = excluded.attempts,
                last_error = excluded.last_error, retry_at = excluded.retry_at;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$handler", cursor.HandlerName);
                command.Parameters.AddWithValue("$stream", cursor.Stream);
                command.Parameters.AddWithValue("$position", cursor.Position);
                command.Parameters.AddWithValue("$state", (int)cursor.State);
                command.Parameters.AddWithValue("$attempts", cursor.Attempts);
                command.Parameters.AddWithValue("$error", NullableValue(cursor.LastError));
                command.Parameters.AddWithValue("$retryAt", NullableTicks(cursor.RetryAt));
                command.Parameters.AddWithValue("$revision", revision);
            },
            cancellationToken
        ).ConfigureAwait(false);
        return saved > 0;
    }

    /// <inheritdoc />
    public async Task<bool> SaveCursors(IReadOnlyList<HandlerCursor> cursors, long revision, CancellationToken cancellationToken)
    {
        if (cursors.Count == 0)
        {
            return true;
        }

        await using var connection = await Open(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "UPDATE pulsesharp_handlers SET revision = revision WHERE handler_name = $handler AND revision = $revision;";
            check.Parameters.AddWithValue("$handler", cursors[0].HandlerName);
            check.Parameters.AddWithValue("$revision", revision);
            if (await check.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                return false;
            }
        }

        foreach (var cursor in cursors)
        {
            await using var save = connection.CreateCommand();
            save.Transaction = transaction;
            save.CommandText = """
                INSERT INTO pulsesharp_cursors (handler_name, stream, position, state, attempts, last_error, retry_at)
                VALUES ($handler, $stream, $position, $state, $attempts, $error, $retryAt)
                ON CONFLICT (handler_name, stream) DO UPDATE SET
                    position = excluded.position, state = excluded.state, attempts = excluded.attempts,
                    last_error = excluded.last_error, retry_at = excluded.retry_at;
                """;
            save.Parameters.AddWithValue("$handler", cursor.HandlerName);
            save.Parameters.AddWithValue("$stream", cursor.Stream);
            save.Parameters.AddWithValue("$position", cursor.Position);
            save.Parameters.AddWithValue("$state", (int)cursor.State);
            save.Parameters.AddWithValue("$attempts", cursor.Attempts);
            save.Parameters.AddWithValue("$error", NullableValue(cursor.LastError));
            save.Parameters.AddWithValue("$retryAt", NullableTicks(cursor.RetryAt));
            await save.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<HandlerLeaseStatus> LeaseHandler(
        string handlerName,
        string workerId,
        DateTimeOffset now,
        TimeSpan duration,
        CancellationToken cancellationToken
    )
    {
        await using var connection = await Open(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO pulsesharp_handlers (handler_name, lease_owner, lease_until, revision)
            VALUES ($handler, NULL, NULL, 0)
            ON CONFLICT (handler_name) DO NOTHING;

            UPDATE pulsesharp_handlers SET lease_owner = $worker, lease_until = $until
            WHERE handler_name = $handler AND (lease_owner IS NULL OR lease_owner = $worker OR lease_until < $now);

            SELECT lease_owner = $worker, revision FROM pulsesharp_handlers WHERE handler_name = $handler;
            """;
        command.Parameters.AddWithValue("$handler", handlerName);
        command.Parameters.AddWithValue("$worker", workerId);
        command.Parameters.AddWithValue("$now", now.UtcTicks);
        command.Parameters.AddWithValue("$until", (now + duration).UtcTicks);

        HandlerLeaseStatus status;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            status = new HandlerLeaseStatus(Held: reader.GetInt64(0) != 0, Revision: reader.GetInt64(1));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return status;
    }

    /// <inheritdoc />
    public Task ReleaseHandler(string handlerName, string workerId, CancellationToken cancellationToken)
    {
        return ExecuteNonQuery(
            "UPDATE pulsesharp_handlers SET lease_owner = NULL, lease_until = NULL WHERE handler_name = $handler AND lease_owner = $worker;",
            command =>
            {
                command.Parameters.AddWithValue("$handler", handlerName);
                command.Parameters.AddWithValue("$worker", workerId);
            },
            cancellationToken
        );
    }

    /// <inheritdoc />
    public async Task<long> ChangeCursors(string handlerName, IReadOnlyList<HandlerCursor> cursors, CancellationToken cancellationToken)
    {
        await using var connection = await Open(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        long revision;
        await using (var bump = connection.CreateCommand())
        {
            bump.Transaction = transaction;
            bump.CommandText = """
                INSERT INTO pulsesharp_handlers (handler_name, lease_owner, lease_until, revision)
                VALUES ($handler, NULL, NULL, 1)
                ON CONFLICT (handler_name) DO UPDATE SET revision = revision + 1
                RETURNING revision;
                """;
            bump.Parameters.AddWithValue("$handler", handlerName);
            revision = (long)(await bump.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        foreach (var cursor in cursors)
        {
            await using var save = connection.CreateCommand();
            save.Transaction = transaction;
            save.CommandText = """
                INSERT INTO pulsesharp_cursors (handler_name, stream, position, state, attempts, last_error, retry_at)
                VALUES ($handler, $stream, $position, $state, $attempts, $error, $retryAt)
                ON CONFLICT (handler_name, stream) DO UPDATE SET
                    position = excluded.position, state = excluded.state, attempts = excluded.attempts,
                    last_error = excluded.last_error, retry_at = excluded.retry_at;
                """;
            save.Parameters.AddWithValue("$handler", cursor.HandlerName);
            save.Parameters.AddWithValue("$stream", cursor.Stream);
            save.Parameters.AddWithValue("$position", cursor.Position);
            save.Parameters.AddWithValue("$state", (int)cursor.State);
            save.Parameters.AddWithValue("$attempts", cursor.Attempts);
            save.Parameters.AddWithValue("$error", NullableValue(cursor.LastError));
            save.Parameters.AddWithValue("$retryAt", NullableTicks(cursor.RetryAt));
            await save.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return revision;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredEvent>> ReadStream(
        string stream,
        long afterStreamPosition,
        int maxCount,
        CancellationToken cancellationToken
    )
    {
        await using var connection = await Open(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT global_position, event_id, stream, stream_position, event_name, data, origin, emitted_at
            FROM pulsesharp_events
            WHERE stream = $stream AND stream_position > $after
            ORDER BY stream_position
            LIMIT $max;
            """;
        command.Parameters.AddWithValue("$stream", stream);
        command.Parameters.AddWithValue("$after", afterStreamPosition);
        command.Parameters.AddWithValue("$max", maxCount);
        return await ReadEvents(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredEvent>> AppendReplicated(IReadOnlyList<NewEvent> events, CancellationToken cancellationToken)
    {
        if (events.Count == 0)
        {
            return [];
        }

        await using var connection = await Open(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var saved = new List<StoredEvent>();
        foreach (var newEvent in events)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO pulsesharp_events (event_id, stream, stream_position, event_name, data, origin, emitted_at)
                SELECT $id, $stream, COALESCE(MAX(stream_position), 0) + 1, $name, $data, $origin, $emittedAt
                FROM pulsesharp_events
                WHERE stream = $stream
                ON CONFLICT (event_id) DO NOTHING
                RETURNING global_position, stream_position;
                """;
            insert.Parameters.AddWithValue("$id", newEvent.EventId.ToString());
            insert.Parameters.AddWithValue("$stream", newEvent.Stream);
            insert.Parameters.AddWithValue("$name", newEvent.EventName);
            insert.Parameters.AddWithValue("$data", newEvent.Data);
            insert.Parameters.AddWithValue("$origin", newEvent.Origin);
            insert.Parameters.AddWithValue("$emittedAt", newEvent.EmittedAt.UtcTicks);

            await using var reader = await insert.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                saved.Add(new StoredEvent(
                    reader.GetInt64(0),
                    newEvent.EventId,
                    newEvent.Stream,
                    reader.GetInt64(1),
                    newEvent.EventName,
                    newEvent.Data,
                    newEvent.Origin,
                    newEvent.EmittedAt
                ));
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return saved;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredEvent>> ReadForSync(SyncQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await Open(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT global_position, event_id, stream, stream_position, event_name, data, origin, emitted_at
            FROM pulsesharp_events
            WHERE global_position > $after
              AND ($origin IS NULL OR origin = $origin)
              AND ($except IS NULL OR origin <> $except)
              {PrefixCondition(command, query.StreamPrefixes)}
            ORDER BY global_position
            LIMIT $max;
            """;
        command.Parameters.AddWithValue("$after", query.AfterPosition);
        command.Parameters.AddWithValue("$origin", NullableValue(query.Origin));
        command.Parameters.AddWithValue("$except", NullableValue(query.ExceptOrigin));
        command.Parameters.AddWithValue("$max", query.MaxCount);
        return await ReadEvents(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> CountForSync(SyncQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await Open(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COUNT(*) FROM (
                SELECT 1 FROM pulsesharp_events
                WHERE global_position > $after
                  AND ($origin IS NULL OR origin = $origin)
                  AND ($except IS NULL OR origin <> $except)
                  {PrefixCondition(command, query.StreamPrefixes)}
                LIMIT $max
            );
            """;
        command.Parameters.AddWithValue("$after", query.AfterPosition);
        command.Parameters.AddWithValue("$origin", NullableValue(query.Origin));
        command.Parameters.AddWithValue("$except", NullableValue(query.ExceptOrigin));
        command.Parameters.AddWithValue("$max", query.MaxCount);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public async Task<long> LoadSyncPosition(string remote, SyncDirection direction, CancellationToken cancellationToken)
    {
        await using var connection = await Open(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT position FROM pulsesharp_sync_positions WHERE remote = $remote AND direction = $direction;";
        command.Parameters.AddWithValue("$remote", remote);
        command.Parameters.AddWithValue("$direction", (int)direction);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long position ? position : 0;
    }

    /// <inheritdoc />
    public Task SaveSyncPosition(string remote, SyncDirection direction, long position, CancellationToken cancellationToken)
    {
        return ExecuteNonQuery(
            """
            INSERT INTO pulsesharp_sync_positions (remote, direction, position)
            VALUES ($remote, $direction, $position)
            ON CONFLICT (remote, direction) DO UPDATE SET position = excluded.position;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$remote", remote);
                command.Parameters.AddWithValue("$direction", (int)direction);
                command.Parameters.AddWithValue("$position", position);
            },
            cancellationToken
        );
    }

    /// <inheritdoc />
    public async Task<StoredJob> AddJob(NewJob newJob, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        if (transaction is not null)
        {
            var outer = OuterTransaction(transaction);
            await EnsureSchema(cancellationToken).ConfigureAwait(false);
            return await AddJobIn(outer.Connection!, outer, newJob, cancellationToken).ConfigureAwait(false);
        }

        await using var connection = await Open(cancellationToken).ConfigureAwait(false);
        return await AddJobIn(connection, null, newJob, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<StoredJob?> ClaimNextJob(
        IReadOnlySet<string> jobNames,
        string workerId,
        DateTimeOffset now,
        TimeSpan lease,
        CancellationToken cancellationToken
    )
    {
        if (jobNames.Count == 0)
        {
            return null;
        }

        await using var connection = await Open(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE pulsesharp_jobs
            SET state = 1, attempts = attempts + 1, started_at = $now, lease_owner = $worker, lease_until = $leaseUntil
            WHERE sequence = (
                SELECT sequence FROM pulsesharp_jobs
                WHERE job_name IN ({NameParameters(command, jobNames)})
                  AND ((state IN (0, 2) AND run_at <= $now) OR (state = 1 AND lease_until < $now))
                ORDER BY run_at, sequence
                LIMIT 1
            )
            RETURNING {JobColumns};
            """;
        command.Parameters.AddWithValue("$now", now.UtcTicks);
        command.Parameters.AddWithValue("$worker", workerId);
        command.Parameters.AddWithValue("$leaseUntil", (now + lease).UtcTicks);
        return await ReadSingleJob(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<JobLeaseStatus> RenewLease(Guid jobId, string workerId, DateTimeOffset leaseUntil, CancellationToken cancellationToken)
    {
        await using var connection = await Open(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE pulsesharp_jobs SET lease_until = $leaseUntil WHERE {HeldBy} RETURNING cancel_requested;";
        AddHolder(command, jobId, workerId);
        command.Parameters.AddWithValue("$leaseUntil", leaseUntil.UtcTicks);

        var cancelRequested = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return cancelRequested is null
            ? new JobLeaseStatus(Held: false, CancelRequested: false)
            : new JobLeaseStatus(Held: true, CancelRequested: (long)cancelRequested != 0);
    }

    /// <inheritdoc />
    public Task SaveProgress(Guid jobId, string workerId, int done, int total, CancellationToken cancellationToken)
    {
        return UpdateHeldJob("done = $done, total = $total", jobId, workerId, command =>
        {
            command.Parameters.AddWithValue("$done", done);
            command.Parameters.AddWithValue("$total", total);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task CompleteJob(Guid jobId, string workerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        return FinishHeldJob(JobState.Succeeded, jobId, workerId, now, cancellationToken);
    }

    /// <inheritdoc />
    public Task FailJobAttempt(
        Guid jobId,
        string workerId,
        string error,
        DateTimeOffset? retryAt,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        if (retryAt is { } nextRun)
        {
            return UpdateHeldJob(
                "state = 2, run_at = $retryAt, last_error = $error, lease_owner = NULL, lease_until = NULL",
                jobId,
                workerId,
                command =>
                {
                    command.Parameters.AddWithValue("$retryAt", nextRun.UtcTicks);
                    command.Parameters.AddWithValue("$error", error);
                },
                cancellationToken
            );
        }

        return UpdateHeldJob(
            "state = 4, finished_at = $now, last_error = $error, lease_owner = NULL, lease_until = NULL, cancel_requested = 0",
            jobId,
            workerId,
            command =>
            {
                command.Parameters.AddWithValue("$now", now.UtcTicks);
                command.Parameters.AddWithValue("$error", error);
            },
            cancellationToken
        );
    }

    /// <inheritdoc />
    public Task CancelRunningJob(Guid jobId, string workerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        return FinishHeldJob(JobState.Canceled, jobId, workerId, now, cancellationToken);
    }

    /// <inheritdoc />
    public Task ReleaseJob(Guid jobId, string workerId, CancellationToken cancellationToken)
    {
        return UpdateHeldJob(
            "state = CASE WHEN attempts > 1 THEN 2 ELSE 0 END, attempts = attempts - 1, lease_owner = NULL, lease_until = NULL",
            jobId,
            workerId,
            _ => { },
            cancellationToken
        );
    }

    /// <inheritdoc />
    public async Task<StoredJob?> GetJob(Guid jobId, CancellationToken cancellationToken)
    {
        await using var connection = await Open(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {JobColumns} FROM pulsesharp_jobs WHERE job_id = $id;";
        command.Parameters.AddWithValue("$id", jobId.ToString());
        return await ReadSingleJob(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredJob>> GetJobs(JobState? state, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await Open(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {JobColumns} FROM pulsesharp_jobs
            WHERE $state IS NULL OR state = $state
            ORDER BY sequence DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$state", state is { } value ? (int)value : DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);

        var jobs = new List<StoredJob>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            jobs.Add(ReadJob(reader));
        }

        return jobs;
    }

    /// <inheritdoc />
    public async Task<bool> RequestCancel(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var changed = await ExecuteNonQuery(
            """
            UPDATE pulsesharp_jobs SET
                state = CASE WHEN state IN (0, 2) THEN 5 ELSE state END,
                finished_at = CASE WHEN state IN (0, 2) THEN $now ELSE finished_at END,
                cancel_requested = CASE WHEN state = 1 THEN 1 ELSE cancel_requested END
            WHERE job_id = $id AND state IN (0, 1, 2);
            """,
            command =>
            {
                command.Parameters.AddWithValue("$id", jobId.ToString());
                command.Parameters.AddWithValue("$now", now.UtcTicks);
            },
            cancellationToken
        ).ConfigureAwait(false);
        return changed > 0;
    }

    /// <inheritdoc />
    public Task<int> DeleteFinishedJobs(DateTimeOffset finishedBefore, int maxCount, CancellationToken cancellationToken)
    {
        return ExecuteNonQuery(
            """
            DELETE FROM pulsesharp_jobs
            WHERE sequence IN (
                SELECT sequence FROM pulsesharp_jobs
                WHERE state IN (3, 5) AND finished_at < $before
                LIMIT $max
            );
            """,
            command =>
            {
                command.Parameters.AddWithValue("$before", finishedBefore.UtcTicks);
                command.Parameters.AddWithValue("$max", maxCount);
            },
            cancellationToken
        );
    }

    /// <inheritdoc />
    public async Task<bool> RetryJob(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var changed = await ExecuteNonQuery(
            """
            UPDATE pulsesharp_jobs SET
                state = 0, attempts = 0, done = 0, total = 0, last_error = NULL, run_at = $now,
                started_at = NULL, finished_at = NULL, cancel_requested = 0
            WHERE job_id = $id AND state IN (4, 5);
            """,
            command =>
            {
                command.Parameters.AddWithValue("$id", jobId.ToString());
                command.Parameters.AddWithValue("$now", now.UtcTicks);
            },
            cancellationToken
        ).ConfigureAwait(false);
        return changed > 0;
    }

    private static async Task<StoredEvent> AppendIn(
        SqliteConnection connection,
        SqliteTransaction transaction,
        NewEvent newEvent,
        ExpectedVersion expectedVersion,
        CancellationToken cancellationToken
    )
    {
        await using var current = connection.CreateCommand();
        current.Transaction = transaction;
        current.CommandText = "SELECT COALESCE(MAX(stream_position), 0) FROM pulsesharp_events WHERE stream = $stream;";
        current.Parameters.AddWithValue("$stream", newEvent.Stream);
        var currentPosition = (long)(await current.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

        if (!expectedVersion.Allows(currentPosition))
        {
            throw new ExpectedVersionMismatchException(newEvent.Stream, expectedVersion, currentPosition);
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO pulsesharp_events (event_id, stream, stream_position, event_name, data, origin, emitted_at)
            VALUES ($id, $stream, $position, $name, $data, $origin, $emittedAt)
            RETURNING global_position;
            """;
        insert.Parameters.AddWithValue("$id", newEvent.EventId.ToString());
        insert.Parameters.AddWithValue("$stream", newEvent.Stream);
        insert.Parameters.AddWithValue("$position", currentPosition + 1);
        insert.Parameters.AddWithValue("$name", newEvent.EventName);
        insert.Parameters.AddWithValue("$data", newEvent.Data);
        insert.Parameters.AddWithValue("$origin", newEvent.Origin);
        insert.Parameters.AddWithValue("$emittedAt", newEvent.EmittedAt.UtcTicks);
        var globalPosition = (long)(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

        return new StoredEvent(
            globalPosition,
            newEvent.EventId,
            newEvent.Stream,
            currentPosition + 1,
            newEvent.EventName,
            newEvent.Data,
            newEvent.Origin,
            newEvent.EmittedAt
        );
    }

    private static async Task<StoredJob> AddJobIn(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        NewJob newJob,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO pulsesharp_jobs (job_id, job_name, data, state, attempts, done, total, enqueued_at, run_at)
            VALUES ($id, $name, $data, 0, 0, 0, 0, $enqueuedAt, $runAt)
            RETURNING {JobColumns};
            """;
        command.Parameters.AddWithValue("$id", newJob.JobId.ToString());
        command.Parameters.AddWithValue("$name", newJob.JobName);
        command.Parameters.AddWithValue("$data", newJob.Data);
        command.Parameters.AddWithValue("$enqueuedAt", newJob.EnqueuedAt.UtcTicks);
        command.Parameters.AddWithValue("$runAt", newJob.RunAt.UtcTicks);
        return (await ReadSingleJob(command, cancellationToken).ConfigureAwait(false))!;
    }

    private Task FinishHeldJob(JobState state, Guid jobId, string workerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        return UpdateHeldJob(
            "state = $state, finished_at = $now, lease_owner = NULL, lease_until = NULL, cancel_requested = 0",
            jobId,
            workerId,
            command =>
            {
                command.Parameters.AddWithValue("$state", (int)state);
                command.Parameters.AddWithValue("$now", now.UtcTicks);
            },
            cancellationToken
        );
    }

    private Task UpdateHeldJob(
        string assignments,
        Guid jobId,
        string workerId,
        Action<SqliteCommand> addParameters,
        CancellationToken cancellationToken
    )
    {
        return ExecuteNonQuery(
            $"UPDATE pulsesharp_jobs SET {assignments} WHERE {HeldBy};",
            command =>
            {
                AddHolder(command, jobId, workerId);
                addParameters(command);
            },
            cancellationToken
        );
    }

    private async Task<int> ExecuteNonQuery(string sql, Action<SqliteCommand> addParameters, CancellationToken cancellationToken)
    {
        await using var connection = await Open(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        addParameters(command);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> Open(CancellationToken cancellationToken)
    {
        await EnsureSchema(cancellationToken).ConfigureAwait(false);
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private async Task EnsureSchema(CancellationToken cancellationToken)
    {
        if (_schemaReady)
        {
            return;
        }

        await _schemaGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_schemaReady)
            {
                await using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await SqliteSchema.Apply(connection, cancellationToken).ConfigureAwait(false);
                _schemaReady = true;
            }
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    private static async Task<IReadOnlyList<StoredEvent>> ReadEvents(SqliteCommand command, CancellationToken cancellationToken)
    {
        var events = new List<StoredEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(new StoredEvent(
                GlobalPosition: reader.GetInt64(0),
                EventId: Guid.Parse(reader.GetString(1)),
                Stream: reader.GetString(2),
                StreamPosition: reader.GetInt64(3),
                EventName: reader.GetString(4),
                Data: (byte[])reader.GetValue(5),
                Origin: reader.GetString(6),
                EmittedAt: FromTicks(reader.GetInt64(7))
            ));
        }

        return events;
    }

    private static async Task<StoredJob?> ReadSingleJob(SqliteCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadJob(reader) : null;
    }

    private static StoredJob ReadJob(SqliteDataReader reader)
    {
        return new StoredJob(
            JobId: Guid.Parse(reader.GetString(0)),
            JobName: reader.GetString(1),
            Data: (byte[])reader.GetValue(2),
            State: (JobState)reader.GetInt32(3),
            Attempts: reader.GetInt32(4),
            Done: reader.GetInt32(5),
            Total: reader.GetInt32(6),
            LastError: reader.IsDBNull(7) ? null : reader.GetString(7),
            EnqueuedAt: FromTicks(reader.GetInt64(8)),
            RunAt: FromTicks(reader.GetInt64(9)),
            StartedAt: reader.IsDBNull(10) ? null : FromTicks(reader.GetInt64(10)),
            FinishedAt: reader.IsDBNull(11) ? null : FromTicks(reader.GetInt64(11)),
            LeaseOwner: reader.IsDBNull(12) ? null : reader.GetString(12),
            LeaseUntil: reader.IsDBNull(13) ? null : FromTicks(reader.GetInt64(13)),
            CancelRequested: reader.GetInt64(14) != 0
        );
    }

    private static SqliteTransaction OuterTransaction(DbTransaction transaction)
    {
        if (transaction is SqliteTransaction { Connection: not null } sqlite)
        {
            return sqlite;
        }

        throw new ArgumentException(
            "The SQLite store can only join a Microsoft.Data.Sqlite transaction on an open connection to the same database.",
            nameof(transaction)
        );
    }

    private static string NameParameters(SqliteCommand command, IReadOnlySet<string> names)
    {
        var placeholders = new List<string>();
        foreach (var name in names)
        {
            var placeholder = $"$name{placeholders.Count}";
            command.Parameters.AddWithValue(placeholder, name);
            placeholders.Add(placeholder);
        }

        return string.Join(", ", placeholders);
    }

    private static string PrefixCondition(SqliteCommand command, IReadOnlyList<string> prefixes)
    {
        if (prefixes.Count == 0)
        {
            return string.Empty;
        }

        var conditions = new List<string>();
        foreach (var prefix in prefixes)
        {
            var placeholder = $"$prefix{conditions.Count}";
            command.Parameters.AddWithValue(placeholder, prefix);
            conditions.Add($"substr(stream, 1, length({placeholder})) = {placeholder}");
        }

        return $"AND ({string.Join(" OR ", conditions)})";
    }

    private static void AddHolder(SqliteCommand command, Guid jobId, string workerId)
    {
        command.Parameters.AddWithValue("$id", jobId.ToString());
        command.Parameters.AddWithValue("$worker", workerId);
    }

    private static object NullableValue(string? value)
    {
        return value is null ? DBNull.Value : value;
    }

    private static object NullableTicks(DateTimeOffset? value)
    {
        return value is { } time ? time.UtcTicks : DBNull.Value;
    }

    private static DateTimeOffset FromTicks(long utcTicks)
    {
        return new DateTimeOffset(utcTicks, TimeSpan.Zero);
    }
}
