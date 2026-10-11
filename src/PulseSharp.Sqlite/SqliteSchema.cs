using Microsoft.Data.Sqlite;

namespace PulseSharp.Sqlite;

internal static class SqliteSchema
{
    private static readonly (int Version, string Sql)[] Migrations =
    [
        (1, """
            CREATE TABLE pulsesharp_events (
                global_position INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id        TEXT    NOT NULL UNIQUE,
                stream          TEXT    NOT NULL,
                stream_position INTEGER NOT NULL,
                event_name      TEXT    NOT NULL,
                data            BLOB    NOT NULL,
                origin          TEXT    NOT NULL,
                emitted_at      INTEGER NOT NULL,
                UNIQUE (stream, stream_position)
            );
            CREATE INDEX pulsesharp_events_by_name ON pulsesharp_events (event_name, global_position);

            CREATE TABLE pulsesharp_cursors (
                handler_name TEXT    NOT NULL,
                stream       TEXT    NOT NULL,
                position     INTEGER NOT NULL,
                state        INTEGER NOT NULL,
                attempts     INTEGER NOT NULL,
                last_error   TEXT    NULL,
                retry_at     INTEGER NULL,
                PRIMARY KEY (handler_name, stream)
            );

            CREATE TABLE pulsesharp_jobs (
                sequence         INTEGER PRIMARY KEY AUTOINCREMENT,
                job_id           TEXT    NOT NULL UNIQUE,
                job_name         TEXT    NOT NULL,
                data             BLOB    NOT NULL,
                state            INTEGER NOT NULL,
                attempts         INTEGER NOT NULL,
                done             INTEGER NOT NULL,
                total            INTEGER NOT NULL,
                last_error       TEXT    NULL,
                enqueued_at      INTEGER NOT NULL,
                run_at           INTEGER NOT NULL,
                started_at       INTEGER NULL,
                finished_at      INTEGER NULL,
                lease_owner      TEXT    NULL,
                lease_until      INTEGER NULL,
                cancel_requested INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX pulsesharp_jobs_due ON pulsesharp_jobs (state, run_at);
            """)
    ];

    public static async Task Apply(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await Execute(connection, null, "PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await Execute(connection, transaction, "CREATE TABLE IF NOT EXISTS pulsesharp_schema (version INTEGER NOT NULL);", cancellationToken).ConfigureAwait(false);

        var current = await CurrentVersion(connection, transaction, cancellationToken).ConfigureAwait(false);
        foreach (var (version, sql) in Migrations.Where(migration => migration.Version > current))
        {
            await Execute(connection, transaction, sql, cancellationToken).ConfigureAwait(false);
            await Execute(connection, transaction, $"INSERT INTO pulsesharp_schema (version) VALUES ({version});", cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> CurrentVersion(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM pulsesharp_schema;";
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static async Task Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
