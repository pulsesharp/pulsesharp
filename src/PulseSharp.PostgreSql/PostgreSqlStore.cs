using System.Data.Common;
using System.Text.RegularExpressions;
using Npgsql;
using PulseSharp.Stores;

namespace PulseSharp.PostgreSql;

/// <summary>
/// Keeps PulseSharp's events, cursors and jobs in PostgreSQL, in their own schema (default <c>pulsesharp</c>).
/// Several app instances can share one database: each handler is delivered by one instance at a time, and jobs are
/// claimed with <c>FOR UPDATE SKIP LOCKED</c>.
/// </summary>
/// <remarks>
/// Appends in one schema run one transaction at a time (a transaction-level advisory lock), so events always become
/// visible in position order and readers never skip one. Keep transactions that emit events short.
/// </remarks>
public sealed partial class PostgreSqlStore : ISyncStore, INotifyingStore, IAsyncDisposable
{
    private const int MaxChannelLength = 63;
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _schema;
    private readonly string _events;
    private readonly string _cursors;
    private readonly string _handlers;
    private readonly string _jobs;
    private readonly string _jobColumns;
    private readonly string _channel;
    private readonly string _listenConnectionString;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private volatile bool _schemaReady;

    /// <summary>
    /// Creates the store.
    /// </summary>
    /// <param name="connectionString">An Npgsql connection string.</param>
    /// <param name="schema">The PostgreSQL schema for PulseSharp's tables. Letters, digits and underscores only.</param>
    /// <exception cref="ArgumentException">The connection string is empty or the schema name is not valid.</exception>
    public PostgreSqlStore(string connectionString, string schema = "pulsesharp")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        if (!SchemaName().IsMatch(schema))
        {
            throw new ArgumentException("The schema name may contain only letters, digits and underscores, and must not start with a digit.", nameof(schema));
        }

        var builder = new NpgsqlSlimDataSourceBuilder(connectionString);
        builder.EnableArrays();
        _dataSource = builder.Build();
        _schema = schema;
        _events = $"\"{schema}\".events";
        _cursors = $"\"{schema}\".cursors";
        _handlers = $"\"{schema}\".handlers";
        _jobs = $"\"{schema}\".jobs";
        _jobColumns =
            "job_id, job_name, convert_to(data::text, 'UTF8'), state, attempts, done, total, last_error, enqueued_at, run_at, " +
            "started_at, finished_at, lease_owner, lease_until, cancel_requested";

        var channel = $"pulsesharp_{schema}";
        _channel = channel.Length > MaxChannelLength ? channel[..MaxChannelLength] : channel;
        _listenConnectionString = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false, KeepAlive = 30 }.ToString();
    }

    /// <inheritdoc />
    public async Task ListenForChanges(Action changed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changed);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_listenConnectionString);
                connection.Notification += (_, _) => changed();
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using (var listen = new NpgsqlCommand($"LISTEN \"{_channel}\";", connection))
                {
                    await listen.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                changed();
                while (true)
                {
                    await connection.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is NpgsqlException or System.Net.Sockets.SocketException or IOException or TimeoutException)
            {
                try
                {
                    await Task.Delay(ReconnectDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
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
        await EnsureSchema(cancellationToken).ConfigureAwait(false);

        var outer = transaction is null ? null : OuterTransaction(transaction);
        await using var batch = outer is null ? _dataSource.CreateBatch() : new NpgsqlBatch(outer.Connection, outer);
        batch.BatchCommands.Add(BatchCommand("SELECT pg_advisory_xact_lock(hashtextextended($1, 0));", $"pulsesharp.append.{_schema}"));
        batch.BatchCommands.Add(BatchCommand(
            $"""
            WITH current AS (SELECT COALESCE(MAX(stream_position), 0) AS position FROM {_events} WHERE stream = $1)
            INSERT INTO {_events} (event_id, stream, stream_position, event_name, data, origin, emitted_at)
            SELECT $2, $1, current.position + 1, $3, convert_from($4, 'UTF8')::jsonb, $5, $6
            FROM current
            WHERE $7 = 0
               OR ($7 = 1 AND current.position = 0)
               OR ($7 = 2 AND current.position > 0)
               OR ($7 = 3 AND current.position = $8)
            RETURNING global_position, stream_position;
            """,
            newEvent.Stream,
            newEvent.EventId,
            newEvent.EventName,
            newEvent.Data,
            newEvent.Origin,
            newEvent.EmittedAt,
            (int)expectedVersion.Kind,
            expectedVersion.Position
        ));
        batch.BatchCommands.Add(NotifyCommand());

        await using (var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new StoredEvent(
                    reader.GetInt64(0),
                    newEvent.EventId,
                    newEvent.Stream,
                    reader.GetInt64(1),
                    newEvent.EventName,
                    newEvent.Data,
                    newEvent.Origin,
                    newEvent.EmittedAt
                );
            }
        }

        var actual = await CurrentStreamPosition(newEvent.Stream, outer, cancellationToken).ConfigureAwait(false);
        throw new ExpectedVersionMismatchException(newEvent.Stream, expectedVersion, actual);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredEvent>> AppendAll(
        IReadOnlyList<NewEvent> events,
        ExpectedVersion expectedVersion,
        DbTransaction? transaction,
        CancellationToken cancellationToken
    )
    {
        await EnsureSchema(cancellationToken).ConfigureAwait(false);

        var stream = events[0].Stream;
        var outer = transaction is null ? null : OuterTransaction(transaction);
        await using var batch = outer is null ? _dataSource.CreateBatch() : new NpgsqlBatch(outer.Connection, outer);
        batch.BatchCommands.Add(BatchCommand("SELECT pg_advisory_xact_lock(hashtextextended($1, 0));", $"pulsesharp.append.{_schema}"));
        batch.BatchCommands.Add(BatchCommand(
            $"""
            WITH current AS (SELECT COALESCE(MAX(stream_position), 0) AS position FROM {_events} WHERE stream = $1)
            INSERT INTO {_events} (event_id, stream, stream_position, event_name, data, origin, emitted_at)
            SELECT e.event_id, $1, current.position + e.ordinal, e.event_name, convert_from(e.data, 'UTF8')::jsonb, e.origin, e.emitted_at
            FROM current, unnest($2::uuid[], $3::int[], $4::text[], $5::bytea[], $6::text[], $7::timestamptz[])
                AS e(event_id, ordinal, event_name, data, origin, emitted_at)
            WHERE $8 = 0
               OR ($8 = 1 AND current.position = 0)
               OR ($8 = 2 AND current.position > 0)
               OR ($8 = 3 AND current.position = $9)
            ORDER BY e.ordinal
            RETURNING global_position, stream_position;
            """,
            stream,
            events.Select(newEvent => newEvent.EventId).ToArray(),
            Enumerable.Range(1, events.Count).ToArray(),
            events.Select(newEvent => newEvent.EventName).ToArray(),
            events.Select(newEvent => newEvent.Data).ToArray(),
            events.Select(newEvent => newEvent.Origin).ToArray(),
            events.Select(newEvent => newEvent.EmittedAt).ToArray(),
            (int)expectedVersion.Kind,
            expectedVersion.Position
        ));
        batch.BatchCommands.Add(NotifyCommand());

        var positions = new List<(long Global, long InStream)>(events.Count);
        await using (var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                positions.Add((reader.GetInt64(0), reader.GetInt64(1)));
            }
        }

        if (positions.Count != events.Count)
        {
            var actual = await CurrentStreamPosition(stream, outer, cancellationToken).ConfigureAwait(false);
            throw new ExpectedVersionMismatchException(stream, expectedVersion, actual);
        }

        var ordered = positions.OrderBy(position => position.InStream).ToList();
        var saved = new List<StoredEvent>(events.Count);
        for (var index = 0; index < events.Count; index++)
        {
            var newEvent = events[index];
            saved.Add(new StoredEvent(
                ordered[index].Global,
                newEvent.EventId,
                newEvent.Stream,
                ordered[index].InStream,
                newEvent.EventName,
                newEvent.Data,
                newEvent.Origin,
                newEvent.EmittedAt
            ));
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

        await using var command = await Command($"""
            SELECT global_position, event_id, stream, stream_position, event_name, convert_to(data::text, 'UTF8'), origin, emitted_at
            FROM {_events}
            WHERE global_position > $1 AND event_name = ANY($2)
            ORDER BY global_position
            LIMIT $3;
            """, cancellationToken, globalPosition, eventNames.ToArray(), maxCount).ConfigureAwait(false);
        return await ReadEvents(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredEvent>> ReadStream(
        string stream,
        long afterStreamPosition,
        int maxCount,
        CancellationToken cancellationToken
    )
    {
        await using var command = await Command($"""
            SELECT global_position, event_id, stream, stream_position, event_name, convert_to(data::text, 'UTF8'), origin, emitted_at
            FROM {_events}
            WHERE stream = $1 AND stream_position > $2
            ORDER BY stream_position
            LIMIT $3;
            """, cancellationToken, stream, afterStreamPosition, maxCount).ConfigureAwait(false);
        return await ReadEvents(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredEvent>> AppendReplicated(IReadOnlyList<NewEvent> events, CancellationToken cancellationToken)
    {
        if (events.Count == 0)
        {
            return [];
        }

        await EnsureSchema(cancellationToken).ConfigureAwait(false);

        await using var batch = _dataSource.CreateBatch();
        batch.BatchCommands.Add(BatchCommand("SELECT pg_advisory_xact_lock(hashtextextended($1, 0));", $"pulsesharp.append.{_schema}"));
        foreach (var newEvent in events)
        {
            batch.BatchCommands.Add(BatchCommand(
                $"""
                WITH current AS (SELECT COALESCE(MAX(stream_position), 0) AS position FROM {_events} WHERE stream = $1)
                INSERT INTO {_events} (event_id, stream, stream_position, event_name, data, origin, emitted_at)
                SELECT $2, $1, current.position + 1, $3, convert_from($4, 'UTF8')::jsonb, $5, $6
                FROM current
                ON CONFLICT (event_id) DO NOTHING
                RETURNING global_position, stream_position;
                """,
                newEvent.Stream,
                newEvent.EventId,
                newEvent.EventName,
                newEvent.Data,
                newEvent.Origin,
                newEvent.EmittedAt
            ));
        }

        batch.BatchCommands.Add(NotifyCommand());

        var saved = new List<StoredEvent>();
        await using var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        foreach (var newEvent in events)
        {
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
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

        return saved;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredEvent>> ReadForSync(SyncQuery query, CancellationToken cancellationToken)
    {
        await using var command = await Command($"""
            SELECT global_position, event_id, stream, stream_position, event_name, convert_to(data::text, 'UTF8'), origin, emitted_at
            FROM {_events}
            WHERE global_position > $1
              AND ($2::text IS NULL OR origin = $2::text)
              AND ($3::text IS NULL OR origin <> $3::text)
              AND (cardinality($4::text[]) = 0 OR EXISTS (SELECT 1 FROM unnest($4::text[]) AS prefix WHERE left(stream, length(prefix)) = prefix))
            ORDER BY global_position
            LIMIT $5;
            """,
            cancellationToken,
            query.AfterPosition,
            (object?)query.Origin ?? DBNull.Value,
            (object?)query.ExceptOrigin ?? DBNull.Value,
            query.StreamPrefixes.ToArray(),
            query.MaxCount
        ).ConfigureAwait(false);
        return await ReadEvents(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> CountForSync(SyncQuery query, CancellationToken cancellationToken)
    {
        await using var command = await Command($"""
            SELECT count(*) FROM (
                SELECT 1 FROM {_events}
                WHERE global_position > $1
                  AND ($2::text IS NULL OR origin = $2::text)
                  AND ($3::text IS NULL OR origin <> $3::text)
                  AND (cardinality($4::text[]) = 0 OR EXISTS (SELECT 1 FROM unnest($4::text[]) AS prefix WHERE left(stream, length(prefix)) = prefix))
                LIMIT $5
            ) AS counted;
            """,
            cancellationToken,
            query.AfterPosition,
            (object?)query.Origin ?? DBNull.Value,
            (object?)query.ExceptOrigin ?? DBNull.Value,
            query.StreamPrefixes.ToArray(),
            query.MaxCount
        ).ConfigureAwait(false);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public async Task<long> LoadSyncPosition(string remote, SyncDirection direction, CancellationToken cancellationToken)
    {
        await using var command = await Command(
            $"SELECT position FROM \"{_schema}\".sync_positions WHERE remote = $1 AND direction = $2;",
            cancellationToken,
            remote,
            (short)direction
        ).ConfigureAwait(false);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long position ? position : 0;
    }

    /// <inheritdoc />
    public Task SaveSyncPosition(string remote, SyncDirection direction, long position, CancellationToken cancellationToken)
    {
        return Execute($"""
            INSERT INTO "{_schema}".sync_positions (remote, direction, position)
            VALUES ($1, $2, $3)
            ON CONFLICT (remote, direction) DO UPDATE SET position = excluded.position;
            """, cancellationToken, remote, (short)direction, position);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HandlerCursor>> LoadCursors(string handlerName, CancellationToken cancellationToken)
    {
        await using var command = await Command($"""
            SELECT stream, position, state, attempts, last_error, retry_at
            FROM {_cursors}
            WHERE handler_name = $1;
            """, cancellationToken, handlerName).ConfigureAwait(false);

        var cursors = new List<HandlerCursor>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cursors.Add(new HandlerCursor(
                handlerName,
                reader.GetString(0),
                reader.GetInt64(1),
                (CursorState)reader.GetInt16(2),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5)
            ));
        }

        return cursors;
    }

    /// <inheritdoc />
    public async Task<bool> SaveCursor(HandlerCursor cursor, long revision, CancellationToken cancellationToken)
    {
        var saved = await Execute($"""
            INSERT INTO {_cursors} (handler_name, stream, position, state, attempts, last_error, retry_at)
            SELECT $1, $2, $3, $4, $5, $6, $7
            WHERE EXISTS (SELECT 1 FROM {_handlers} WHERE handler_name = $1 AND revision = $8)
            ON CONFLICT (handler_name, stream) DO UPDATE SET
                position = excluded.position, state = excluded.state, attempts = excluded.attempts,
                last_error = excluded.last_error, retry_at = excluded.retry_at;
            """,
            cancellationToken,
            cursor.HandlerName,
            cursor.Stream,
            cursor.Position,
            (short)cursor.State,
            cursor.Attempts,
            Nullable(cursor.LastError),
            Nullable(cursor.RetryAt),
            revision
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

        await EnsureSchema(cancellationToken).ConfigureAwait(false);

        await using var batch = _dataSource.CreateBatch();
        batch.BatchCommands.Add(BatchCommand($"SELECT 1 FROM {_handlers} WHERE handler_name = $1 AND revision = $2 FOR SHARE;", cursors[0].HandlerName, revision));
        foreach (var cursor in cursors)
        {
            batch.BatchCommands.Add(BatchCommand(
                $"""
                INSERT INTO {_cursors} (handler_name, stream, position, state, attempts, last_error, retry_at)
                SELECT $1, $2, $3, $4, $5, $6, $7
                WHERE EXISTS (SELECT 1 FROM {_handlers} WHERE handler_name = $1 AND revision = $8)
                ON CONFLICT (handler_name, stream) DO UPDATE SET
                    position = excluded.position, state = excluded.state, attempts = excluded.attempts,
                    last_error = excluded.last_error, retry_at = excluded.retry_at;
                """,
                cursor.HandlerName,
                cursor.Stream,
                cursor.Position,
                (short)cursor.State,
                cursor.Attempts,
                Nullable(cursor.LastError),
                Nullable(cursor.RetryAt),
                revision
            ));
        }

        await using var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
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
        await using var command = await Command($"""
            INSERT INTO {_handlers} AS handler (handler_name, lease_owner, lease_until, revision)
            VALUES ($1, $2, $4, 0)
            ON CONFLICT (handler_name) DO UPDATE SET lease_owner = $2, lease_until = $4
                WHERE handler.lease_owner IS NULL OR handler.lease_owner = $2 OR handler.lease_until < $3
            RETURNING TRUE, revision;
            """, cancellationToken, handlerName, workerId, now, now + duration).ConfigureAwait(false);

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new HandlerLeaseStatus(Held: true, Revision: reader.GetInt64(1));
            }
        }

        await using var current = await Command($"SELECT revision, lease_owner FROM {_handlers} WHERE handler_name = $1;", cancellationToken, handlerName).ConfigureAwait(false);
        await using var holder = await current.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await holder.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new HandlerLeaseStatus(Held: false, Revision: holder.GetInt64(0), Owner: holder.IsDBNull(1) ? null : holder.GetString(1));
    }

    /// <inheritdoc />
    public Task ReleaseHandler(string handlerName, string workerId, CancellationToken cancellationToken)
    {
        return Execute(
            $"UPDATE {_handlers} SET lease_owner = NULL, lease_until = NULL WHERE handler_name = $1 AND lease_owner = $2;",
            cancellationToken,
            handlerName,
            workerId
        );
    }

    /// <inheritdoc />
    public async Task<long> ChangeCursors(string handlerName, IReadOnlyList<HandlerCursor> cursors, CancellationToken cancellationToken)
    {
        await EnsureSchema(cancellationToken).ConfigureAwait(false);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        long revision;
        await using (var bump = Prepare(new NpgsqlCommand($"""
            INSERT INTO {_handlers} AS handler (handler_name, lease_owner, lease_until, revision)
            VALUES ($1, NULL, NULL, 1)
            ON CONFLICT (handler_name) DO UPDATE SET revision = handler.revision + 1
            RETURNING revision;
            """, connection, transaction), [handlerName]))
        {
            revision = (long)(await bump.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        foreach (var cursor in cursors)
        {
            await using var save = Prepare(new NpgsqlCommand($"""
                INSERT INTO {_cursors} (handler_name, stream, position, state, attempts, last_error, retry_at)
                VALUES ($1, $2, $3, $4, $5, $6, $7)
                ON CONFLICT (handler_name, stream) DO UPDATE SET
                    position = excluded.position, state = excluded.state, attempts = excluded.attempts,
                    last_error = excluded.last_error, retry_at = excluded.retry_at;
                """, connection, transaction),
                [cursor.HandlerName, cursor.Stream, cursor.Position, (short)cursor.State, cursor.Attempts, Nullable(cursor.LastError), Nullable(cursor.RetryAt)]);
            await save.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var notify = Prepare(new NpgsqlCommand("SELECT pg_notify($1, '');", connection, transaction), [_channel]))
        {
            await notify.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return revision;
    }

    /// <inheritdoc />
    public async Task<StoredJob> AddJob(NewJob newJob, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        await EnsureSchema(cancellationToken).ConfigureAwait(false);
        var sql = $"""
            INSERT INTO {_jobs} (job_id, job_name, data, state, attempts, done, total, enqueued_at, run_at)
            VALUES ($1, $2, convert_from($3, 'UTF8')::jsonb, 0, 0, 0, 0, $4, $5)
            RETURNING {_jobColumns};
            """;
        var outer = transaction is null ? null : OuterTransaction(transaction);
        await using var batch = outer is null ? _dataSource.CreateBatch() : new NpgsqlBatch(outer.Connection, outer);
        batch.BatchCommands.Add(BatchCommand(sql, newJob.JobId, newJob.JobName, newJob.Data, newJob.EnqueuedAt, newJob.RunAt));
        batch.BatchCommands.Add(NotifyCommand());

        await using var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return ReadJob(reader);
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

        await using var command = await Command($"""
            UPDATE {_jobs}
            SET state = 1, attempts = attempts + 1, started_at = $3, lease_owner = $2, lease_until = $4
            WHERE sequence = (
                SELECT sequence FROM {_jobs}
                WHERE job_name = ANY($1)
                  AND ((state IN (0, 2) AND run_at <= $3) OR (state = 1 AND lease_until < $3))
                ORDER BY run_at, sequence
                LIMIT 1
                FOR UPDATE SKIP LOCKED
            )
            RETURNING {_jobColumns};
            """, cancellationToken, jobNames.ToArray(), workerId, now, now + lease).ConfigureAwait(false);
        return await ReadSingleJob(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<JobLeaseStatus> RenewLease(Guid jobId, string workerId, DateTimeOffset leaseUntil, CancellationToken cancellationToken)
    {
        await using var command = await Command(
            $"UPDATE {_jobs} SET lease_until = $3 WHERE job_id = $1 AND state = 1 AND lease_owner = $2 RETURNING cancel_requested;",
            cancellationToken,
            jobId,
            workerId,
            leaseUntil
        ).ConfigureAwait(false);

        var cancelRequested = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return cancelRequested is bool requested
            ? new JobLeaseStatus(Held: true, CancelRequested: requested)
            : new JobLeaseStatus(Held: false, CancelRequested: false);
    }

    /// <inheritdoc />
    public Task SaveProgress(Guid jobId, string workerId, int done, int total, CancellationToken cancellationToken)
    {
        return UpdateHeldJob("done = $3, total = $4", cancellationToken, jobId, workerId, done, total);
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
                "state = 2, run_at = $3, last_error = $4, lease_owner = NULL, lease_until = NULL",
                cancellationToken,
                jobId,
                workerId,
                nextRun,
                error
            );
        }

        return UpdateHeldJob(
            "state = 4, finished_at = $3, last_error = $4, lease_owner = NULL, lease_until = NULL, cancel_requested = FALSE",
            cancellationToken,
            jobId,
            workerId,
            now,
            error
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
            cancellationToken,
            jobId,
            workerId
        );
    }

    /// <inheritdoc />
    public async Task<StoredJob?> GetJob(Guid jobId, CancellationToken cancellationToken)
    {
        await using var command = await Command($"SELECT {_jobColumns} FROM {_jobs} WHERE job_id = $1;", cancellationToken, jobId).ConfigureAwait(false);
        return await ReadSingleJob(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredJob>> GetJobs(JobState? state, int limit, CancellationToken cancellationToken)
    {
        await using var command = await Command($"""
            SELECT {_jobColumns} FROM {_jobs}
            WHERE $1::smallint IS NULL OR state = $1
            ORDER BY sequence DESC
            LIMIT $2;
            """, cancellationToken, state is { } value ? (short)value : DBNull.Value, limit).ConfigureAwait(false);

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
        var changed = await Execute($"""
            UPDATE {_jobs} SET
                state = CASE WHEN state IN (0, 2) THEN 5 ELSE state END,
                finished_at = CASE WHEN state IN (0, 2) THEN $2 ELSE finished_at END,
                cancel_requested = CASE WHEN state = 1 THEN TRUE ELSE cancel_requested END
            WHERE job_id = $1 AND state IN (0, 1, 2);
            """, cancellationToken, jobId, now).ConfigureAwait(false);
        return changed > 0;
    }

    /// <inheritdoc />
    public async Task<int> DeleteHandledEvents(
        DateTimeOffset emittedBefore,
        IReadOnlyList<EventListener> listeners,
        string ownNode,
        long? ownEventsSentUpTo,
        int maxCount,
        CancellationToken cancellationToken
    )
    {
        await using var command = await Command($"""
            WITH listener AS (SELECT * FROM unnest($1::text[], $2::text[]) AS l(handler, name)),
            doomed AS (
                SELECT e.global_position FROM {_events} e
                WHERE e.emitted_at < $3
                  AND e.stream_position < (SELECT MAX(x.stream_position) FROM {_events} x WHERE x.stream = e.stream)
                  AND ($4::bigint IS NULL OR e.origin <> $5::text OR e.global_position <= $4::bigint)
                  AND NOT EXISTS (
                      SELECT 1 FROM listener l
                      WHERE l.name = e.event_name
                        AND COALESCE((SELECT c.position FROM {_cursors} c WHERE c.handler_name = l.handler AND c.stream = e.stream), 0) < e.stream_position
                  )
                ORDER BY e.global_position
                LIMIT $6
            )
            DELETE FROM {_events} WHERE global_position IN (SELECT global_position FROM doomed);
            """,
            cancellationToken,
            listeners.Select(listener => listener.HandlerName).ToArray(),
            listeners.Select(listener => listener.EventName).ToArray(),
            emittedBefore,
            (object?)ownEventsSentUpTo ?? DBNull.Value,
            ownNode,
            maxCount
        ).ConfigureAwait(false);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<int> DeleteFinishedJobs(DateTimeOffset finishedBefore, int maxCount, CancellationToken cancellationToken)
    {
        return Execute($"""
            DELETE FROM {_jobs}
            WHERE sequence IN (
                SELECT sequence FROM {_jobs}
                WHERE state IN (3, 5) AND finished_at < $1
                LIMIT $2
            );
            """, cancellationToken, finishedBefore, maxCount);
    }

    /// <inheritdoc />
    public async Task<bool> RetryJob(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var changed = await Execute($"""
            UPDATE {_jobs} SET
                state = 0, attempts = 0, done = 0, total = 0, last_error = NULL, run_at = $2,
                started_at = NULL, finished_at = NULL, cancel_requested = FALSE
            WHERE job_id = $1 AND state IN (4, 5);
            """, cancellationToken, jobId, now).ConfigureAwait(false);
        if (changed > 0)
        {
            await Execute("SELECT pg_notify($1, '');", cancellationToken, _channel).ConfigureAwait(false);
        }

        return changed > 0;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _schemaGate.Dispose();
        return _dataSource.DisposeAsync();
    }

    private async Task<long> CurrentStreamPosition(string stream, NpgsqlTransaction? outer, CancellationToken cancellationToken)
    {
        var sql = $"SELECT COALESCE(MAX(stream_position), 0) FROM {_events} WHERE stream = $1;";
        await using var command = outer is null
            ? Prepare(_dataSource.CreateCommand(sql), [stream])
            : Prepare(new NpgsqlCommand(sql, outer.Connection, outer), [stream]);
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private NpgsqlBatchCommand NotifyCommand()
    {
        return BatchCommand("SELECT pg_notify($1, '');", _channel);
    }

    private static NpgsqlBatchCommand BatchCommand(string sql, params object[] values)
    {
        var command = new NpgsqlBatchCommand(sql);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        return command;
    }

    private Task FinishHeldJob(JobState state, Guid jobId, string workerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        return UpdateHeldJob(
            "state = $3, finished_at = $4, lease_owner = NULL, lease_until = NULL, cancel_requested = FALSE",
            cancellationToken,
            jobId,
            workerId,
            (short)state,
            now
        );
    }

    private Task<int> UpdateHeldJob(string assignments, CancellationToken cancellationToken, params object[] values)
    {
        return Execute(
            $"UPDATE {_jobs} SET {assignments} WHERE job_id = $1 AND state = 1 AND lease_owner = $2;",
            cancellationToken,
            values
        );
    }

    private async Task<int> Execute(string sql, CancellationToken cancellationToken, params object[] values)
    {
        await using var command = await Command(sql, cancellationToken, values).ConfigureAwait(false);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<NpgsqlCommand> Command(string sql, CancellationToken cancellationToken, params object[] values)
    {
        await EnsureSchema(cancellationToken).ConfigureAwait(false);
        return Prepare(_dataSource.CreateCommand(sql), values);
    }

    private static NpgsqlCommand Prepare(NpgsqlCommand command, object[] values)
    {
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        return command;
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
                await PostgresSchema.Apply(_dataSource, _schema, cancellationToken).ConfigureAwait(false);
                _schemaReady = true;
            }
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    private static async Task<IReadOnlyList<StoredEvent>> ReadEvents(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var events = new List<StoredEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(new StoredEvent(
                GlobalPosition: reader.GetInt64(0),
                EventId: reader.GetGuid(1),
                Stream: reader.GetString(2),
                StreamPosition: reader.GetInt64(3),
                EventName: reader.GetString(4),
                Data: reader.GetFieldValue<byte[]>(5),
                Origin: reader.GetString(6),
                EmittedAt: reader.GetFieldValue<DateTimeOffset>(7)
            ));
        }

        return events;
    }

    private static async Task<StoredJob?> ReadSingleJob(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadJob(reader) : null;
    }

    private static StoredJob ReadJob(NpgsqlDataReader reader)
    {
        return new StoredJob(
            JobId: reader.GetGuid(0),
            JobName: reader.GetString(1),
            Data: reader.GetFieldValue<byte[]>(2),
            State: (JobState)reader.GetInt16(3),
            Attempts: reader.GetInt32(4),
            Done: reader.GetInt32(5),
            Total: reader.GetInt32(6),
            LastError: reader.IsDBNull(7) ? null : reader.GetString(7),
            EnqueuedAt: reader.GetFieldValue<DateTimeOffset>(8),
            RunAt: reader.GetFieldValue<DateTimeOffset>(9),
            StartedAt: reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
            FinishedAt: reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
            LeaseOwner: reader.IsDBNull(12) ? null : reader.GetString(12),
            LeaseUntil: reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
            CancelRequested: reader.GetBoolean(14)
        );
    }

    private static NpgsqlTransaction OuterTransaction(DbTransaction transaction)
    {
        if (transaction is NpgsqlTransaction { Connection: not null } npgsql)
        {
            return npgsql;
        }

        throw new ArgumentException(
            "The PostgreSQL store can only join an Npgsql transaction on an open connection to the same database.",
            nameof(transaction)
        );
    }

    private static object Nullable(string? value)
    {
        return value is null ? DBNull.Value : value;
    }

    private static object Nullable(DateTimeOffset? value)
    {
        return value is { } time ? time : DBNull.Value;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,62}$")]
    private static partial Regex SchemaName();
}
