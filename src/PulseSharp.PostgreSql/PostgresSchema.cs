using Npgsql;

namespace PulseSharp.PostgreSql;

internal static class PostgresSchema
{
    private static (int Version, string Sql)[] Migrations(string schema)
    {
        return
        [
            (1, $"""
                CREATE TABLE "{schema}".events (
                    global_position BIGINT      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    event_id        UUID        NOT NULL UNIQUE,
                    stream          TEXT        NOT NULL,
                    stream_position BIGINT      NOT NULL,
                    event_name      TEXT        NOT NULL,
                    data            JSONB       NOT NULL,
                    origin          TEXT        NOT NULL,
                    emitted_at      TIMESTAMPTZ NOT NULL,
                    UNIQUE (stream, stream_position)
                );
                CREATE INDEX events_by_name ON "{schema}".events (event_name, global_position);

                CREATE TABLE "{schema}".cursors (
                    handler_name TEXT        NOT NULL,
                    stream       TEXT        NOT NULL,
                    position     BIGINT      NOT NULL,
                    state        SMALLINT    NOT NULL,
                    attempts     INTEGER     NOT NULL,
                    last_error   TEXT        NULL,
                    retry_at     TIMESTAMPTZ NULL,
                    PRIMARY KEY (handler_name, stream)
                );

                CREATE TABLE "{schema}".handlers (
                    handler_name TEXT        PRIMARY KEY,
                    lease_owner  TEXT        NULL,
                    lease_until  TIMESTAMPTZ NULL,
                    revision     BIGINT      NOT NULL
                );

                CREATE TABLE "{schema}".jobs (
                    sequence         BIGINT      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    job_id           UUID        NOT NULL UNIQUE,
                    job_name         TEXT        NOT NULL,
                    data             JSONB       NOT NULL,
                    state            SMALLINT    NOT NULL,
                    attempts         INTEGER     NOT NULL,
                    done             INTEGER     NOT NULL,
                    total            INTEGER     NOT NULL,
                    last_error       TEXT        NULL,
                    enqueued_at      TIMESTAMPTZ NOT NULL,
                    run_at           TIMESTAMPTZ NOT NULL,
                    started_at       TIMESTAMPTZ NULL,
                    finished_at      TIMESTAMPTZ NULL,
                    lease_owner      TEXT        NULL,
                    lease_until      TIMESTAMPTZ NULL,
                    cancel_requested BOOLEAN     NOT NULL DEFAULT FALSE
                );
                CREATE INDEX jobs_due ON "{schema}".jobs (state, run_at);
                """),
            (2, $"""
                CREATE INDEX events_by_origin ON "{schema}".events (origin, global_position);

                CREATE TABLE "{schema}".sync_positions (
                    remote    TEXT     NOT NULL,
                    direction SMALLINT NOT NULL,
                    position  BIGINT   NOT NULL,
                    PRIMARY KEY (remote, direction)
                );
                """)
        ];
    }

    public static async Task Apply(NpgsqlDataSource dataSource, string schema, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await Execute(connection, $"SELECT pg_advisory_lock(hashtextextended('pulsesharp.migrations.{schema}', 0));", cancellationToken).ConfigureAwait(false);

        try
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await Execute(connection, $"""
                CREATE SCHEMA IF NOT EXISTS "{schema}";
                CREATE TABLE IF NOT EXISTS "{schema}".schema_versions (version INTEGER PRIMARY KEY, applied_at TIMESTAMPTZ NOT NULL DEFAULT now());
                """, cancellationToken).ConfigureAwait(false);

            var current = await CurrentVersion(connection, schema, cancellationToken).ConfigureAwait(false);
            foreach (var (version, sql) in Migrations(schema).Where(migration => migration.Version > current))
            {
                await Execute(connection, sql, cancellationToken).ConfigureAwait(false);
                await Execute(connection, $"INSERT INTO \"{schema}\".schema_versions (version) VALUES ({version});", cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await Execute(connection, $"SELECT pg_advisory_unlock(hashtextextended('pulsesharp.migrations.{schema}', 0));", CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<int> CurrentVersion(NpgsqlConnection connection, string schema, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT COALESCE(MAX(version), 0) FROM \"{schema}\".schema_versions;", connection);
        return (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static async Task Execute(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
