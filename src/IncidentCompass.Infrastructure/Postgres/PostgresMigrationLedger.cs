using Npgsql;

namespace IncidentCompass.Infrastructure.Postgres;

internal sealed class PostgresMigrationLedger(PostgresDataSourceProvider dataSourceProvider)
{
    public Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken) =>
        dataSourceProvider.OpenConnectionAsync(cancellationToken);

    public async Task EnsureTableAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE SCHEMA IF NOT EXISTS incidentcompass;

            CREATE TABLE IF NOT EXISTS incidentcompass.schema_migrations (
                version integer PRIMARY KEY CHECK (version > 0),
                name text NOT NULL CHECK (length(btrim(name)) > 0),
                checksum text NOT NULL CHECK (length(btrim(checksum)) > 0),
                status text NOT NULL CHECK (status IN ('Applied', 'Failed')),
                applied_at_utc timestamptz NULL,
                failed_at_utc timestamptz NULL,
                error_message text NULL,
                CHECK (
                    (status = 'Applied' AND applied_at_utc IS NOT NULL AND failed_at_utc IS NULL AND error_message IS NULL) OR
                    (status = 'Failed' AND applied_at_utc IS NULL AND failed_at_utc IS NOT NULL AND error_message IS NOT NULL)
                )
            );
            """;
        await PostgresMigrationSql.ExecuteAsync(connection, transaction: null, sql, cancellationToken);
    }

    public async Task<bool> IsAppliedAsync(
        NpgsqlConnection connection,
        PostgresSchemaMigration migration,
        PostgresMigrationChecksumPolicy checksumPolicy,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT name, checksum, status
            FROM incidentcompass.schema_migrations
            WHERE version = $1;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(migration.Version);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return false;
        }

        var name = reader.GetString(0);
        var actualChecksum = reader.GetString(1);
        var status = reader.GetString(2);
        ValidateIdentity(checksumPolicy, migration.Version, name, actualChecksum);

        return string.Equals(status, "Applied", StringComparison.Ordinal);
    }

    public async Task ValidateCatalogAsync(
        NpgsqlConnection connection,
        IReadOnlyDictionary<int, PostgresMigrationChecksumPolicy> checksumPolicies,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT version, name, checksum
            FROM incidentcompass.schema_migrations
            ORDER BY version;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var version = reader.GetInt32(0);
            if (!checksumPolicies.TryGetValue(version, out var checksumPolicy))
            {
                throw new InvalidOperationException(
                    $"PostgreSQL migration ledger contains unexpected durable version {version}, " +
                    "which is not present in this released catalog. Restore the matching application " +
                    "version or migration ledger from a trusted backup; do not renumber durable rows.");
            }

            ValidateIdentity(
                checksumPolicy,
                version,
                reader.GetString(1),
                reader.GetString(2));
        }
    }

    private static void ValidateIdentity(
        PostgresMigrationChecksumPolicy checksumPolicy,
        int version,
        string name,
        string actualChecksum)
    {
        if (!string.Equals(name, checksumPolicy.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"PostgreSQL migration {version} has durable name '{name}', but " +
                $"the released catalog expects '{checksumPolicy.Name}'. Restore the migration ledger " +
                "from a trusted backup; do not edit released SQL or migration records.");
        }

        if (!checksumPolicy.Accepts(version, name, actualChecksum))
        {
            throw new InvalidOperationException(
                $"PostgreSQL migration {version} ({checksumPolicy.Name}) has durable checksum " +
                $"'{actualChecksum}', which is neither canonical nor an allowed released LF/CRLF " +
                $"legacy checksum. Expected canonical checksum " +
                $"'{checksumPolicy.CanonicalChecksum.Value}'. Restore the released migration files " +
                "or migration ledger from a trusted backup; do not edit either in place.");
        }
    }

    public Task MarkAppliedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgresSchemaMigration migration,
        PostgresMigrationChecksum checksum,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO incidentcompass.schema_migrations (
                version, name, checksum, status, applied_at_utc)
            VALUES ($1, $2, $3, 'Applied', clock_timestamp())
            ON CONFLICT (version) DO UPDATE
            SET name = EXCLUDED.name,
                checksum = EXCLUDED.checksum,
                status = 'Applied',
                applied_at_utc = EXCLUDED.applied_at_utc,
                failed_at_utc = NULL,
                error_message = NULL;
            """;
        return PostgresMigrationSql.ExecuteAsync(
            connection,
            transaction,
            sql,
            cancellationToken,
            migration.Version,
            migration.Name,
            checksum.Value);
    }

    public async Task RecordFailureAsync(
        PostgresSchemaMigration migration,
        PostgresMigrationChecksum checksum,
        Exception exception,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSourceProvider.OpenConnectionAsync(cancellationToken);
        const string sql = """
            INSERT INTO incidentcompass.schema_migrations (
                version, name, checksum, status, failed_at_utc, error_message)
            VALUES ($1, $2, $3, 'Failed', clock_timestamp(), $4)
            ON CONFLICT (version) DO UPDATE
            SET name = EXCLUDED.name,
                checksum = EXCLUDED.checksum,
                status = 'Failed',
                applied_at_utc = NULL,
                failed_at_utc = EXCLUDED.failed_at_utc,
                error_message = EXCLUDED.error_message;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(migration.Version);
        command.Parameters.AddWithValue(migration.Name);
        command.Parameters.AddWithValue(checksum.Value);
        command.Parameters.AddWithValue(DescribeFailure(exception));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string DescribeFailure(Exception exception)
    {
        var description = exception.GetType().Name + ": " + exception.Message;
        return description.Length <= 1_000 ? description : description[..1_000];
    }
}
