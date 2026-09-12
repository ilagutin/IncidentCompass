using System.Globalization;
using System.Security.Cryptography;
using IncidentCompass.Infrastructure.Configuration;
using IncidentCompass.Infrastructure.Postgres;
using IncidentCompass.Infrastructure.Postgres.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

internal static class PostgresMigrationTestSupport
{
    public static async Task RunMigrationsAsync(string connectionString)
    {
        using var services = CreateServiceProvider(
            connectionString,
            new NoPostgresMigrationFailureInjector());
        await services.GetRequiredService<PostgresMigrationRunner>()
            .MigrateAsync(TestContext.Current.CancellationToken);
    }

    public static IHost CreateMigrationHost(
        string connectionString,
        IPostgresMigrationFailureInjector failureInjector) =>
        new HostBuilder()
            .ConfigureServices(services =>
            {
                ConfigureMigrationServices(services, connectionString, failureInjector);
                services.AddSingleton<IHostedService, PostgresMigrationHostedService>();
            })
            .Build();

    public static ServiceProvider CreateServiceProvider(
        string connectionString,
        IPostgresMigrationFailureInjector failureInjector)
    {
        var services = new ServiceCollection();
        ConfigureMigrationServices(services, connectionString, failureInjector);
        return services.BuildServiceProvider();
    }

    public static void ConfigureMigrationServices(
        IServiceCollection services,
        string connectionString,
        IPostgresMigrationFailureInjector failureInjector)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:MigrationTests"] = connectionString,
                ["IncidentCompass:Postgres:ConnectionStringName"] = "MigrationTests"
            })
            .Build();

        services.AddPostgresConnectionOptions(configuration);
        services.AddSingleton<PostgresDataSourceProvider>();
        services.AddSingleton<PostgresMigrationReadiness>();
        services.AddSingleton<IPostgresMigrationReadiness>(
            serviceProvider => serviceProvider.GetRequiredService<PostgresMigrationReadiness>());
        services.AddSingleton<IPostgresMigrationFailureInjector>(failureInjector);
        services.AddSingleton<PostgresMigrationRunner>();
    }

    public static async Task<string> Sha256WithCrlfNormalizedToLfAsync(string scriptName)
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "infra", "postgres", "init", scriptName);
            if (File.Exists(path))
            {
                var source = await File.ReadAllBytesAsync(
                    path,
                    TestContext.Current.CancellationToken);
                var normalized = new byte[source.Length];
                var writeIndex = 0;

                for (var readIndex = 0; readIndex < source.Length; readIndex++)
                {
                    if (source[readIndex] == 0x0D && readIndex + 1 < source.Length && source[readIndex + 1] == 0x0A)
                    {
                        normalized[writeIndex++] = 0x0A;
                        readIndex++;
                        continue;
                    }

                    normalized[writeIndex++] = source[readIndex];
                }

                return Convert.ToHexString(SHA256.HashData(normalized.AsSpan(0, writeIndex)));
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Migration script '{scriptName}' was not found.");
    }

    public static async Task<Guid> ReadGuidAsync(string connectionString, string sql)
    {
        var value = await ExecuteScalarAsync(connectionString, sql);
        return (Guid)value!;
    }

    public static async Task<IReadOnlyList<int>> ReadAppliedVersionsAsync(string connectionString)
    {
        await using var connection = await OpenAsync(connectionString);
        await using var command = new NpgsqlCommand("""
            SELECT version
            FROM incidentcompass.schema_migrations
            WHERE status = 'Applied'
            ORDER BY version;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var versions = new List<int>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            versions.Add(reader.GetInt32(0));
        }

        return versions;
    }

    public static async Task<IReadOnlyList<MigrationRecord>> ReadMigrationRecordsAsync(string connectionString)
    {
        await using var connection = await OpenAsync(connectionString);
        await using var command = new NpgsqlCommand("""
            SELECT version, name, checksum, status, applied_at_utc, failed_at_utc, error_message
            FROM incidentcompass.schema_migrations
            ORDER BY version;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var records = new List<MigrationRecord>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            records.Add(ReadMigrationRecord(reader));
        }

        return records;
    }

    public static async Task<MigrationRecord?> ReadMigrationRecordAsync(string connectionString, int version)
    {
        await using var connection = await OpenAsync(connectionString);
        await using var command = new NpgsqlCommand("""
            SELECT version, name, checksum, status, applied_at_utc, failed_at_utc, error_message
            FROM incidentcompass.schema_migrations
            WHERE version = $1;
            """, connection);
        command.Parameters.AddWithValue(version);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        return await reader.ReadAsync(TestContext.Current.CancellationToken)
            ? ReadMigrationRecord(reader)
            : null;
    }

    public static MigrationRecord ReadMigrationRecord(NpgsqlDataReader reader) =>
        new(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
            reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
            reader.IsDBNull(6) ? null : reader.GetString(6));

    public static async Task SetMigrationChecksumAsync(
        string connectionString,
        int version,
        string checksum)
    {
        await using var connection = await OpenAsync(connectionString);
        await using var command = new NpgsqlCommand("""
            UPDATE incidentcompass.schema_migrations
            SET checksum = $2
            WHERE version = $1;
            """, connection);
        command.Parameters.AddWithValue(version);
        command.Parameters.AddWithValue(checksum);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }

    public static async Task<int> CountAsync(string connectionString, string tableName) =>
        Convert.ToInt32(await ExecuteScalarAsync(
            connectionString,
            $"SELECT count(*) FROM incidentcompass.{tableName};"), CultureInfo.InvariantCulture);

    public static async Task<int> CountSqlAsync(
        string connectionString,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = await OpenAsync(connectionString);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        return Convert.ToInt32(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), CultureInfo.InvariantCulture);
    }

    public static async Task<IReadOnlyList<string>> ReadStringsAsync(string connectionString, string sql)
    {
        await using var connection = await OpenAsync(connectionString);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var values = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    public static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = await OpenAsync(connectionString);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    public static async Task<object?> ExecuteScalarAsync(string connectionString, string sql)
    {
        await using var connection = await OpenAsync(connectionString);
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    public static async Task<NpgsqlConnection> OpenAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }
}
