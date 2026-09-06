using IncidentCompass.Infrastructure.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static IncidentCompass.IntegrationTests.PostgresMigrationDurableDataAssertions;
using static IncidentCompass.IntegrationTests.PostgresMigrationTestSupport;

namespace IncidentCompass.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class PostgresMigrationFailureTests(PostgresRepositoryFixture fixture)
{
    [DockerAvailableFact]
    public async Task FailedVersion15LeavesVersion14DurableAndThenUpgradesCleanly()
    {
        await using var database = await MigrationDatabase.CreateAsync(fixture);
        using (var failing = CreateServiceProvider(
                   database.ConnectionString, new FailingMigrationInjector(15)))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => failing
                .GetRequiredService<PostgresMigrationRunner>()
                .MigrateAsync(TestContext.Current.CancellationToken));
        }

        Assert.Equal(
            [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14],
            await ReadAppliedVersionsAsync(database.ConnectionString));
        Assert.Equal("Failed", (await ReadMigrationRecordAsync(database.ConnectionString, 15))!.Status);
        var actionId = await SeedPreProjectionActionAsync(database.ConnectionString, "upgrade-from-v14");
        var costHistory = await SeedCostRollupHistoryAsync(database.ConnectionString, "v14");

        await RunMigrationsAsync(database.ConnectionString);

        Assert.Equal(
            [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17],
            await ReadAppliedVersionsAsync(database.ConnectionString));
        Assert.True(await HasRequiredV02IndexesAndColumnsAsync(database.ConnectionString));
        await AssertPreProjectionActionPreservedAsync(database.ConnectionString, actionId);
        await AssertCostRollupHistoryPreservedAsync(database.ConnectionString, costHistory);
    }

    [DockerAvailableFact]
    public async Task FailedVersion16LeavesVersion15DurableAndThenUpgradesCleanly()
    {
        await using var database = await MigrationDatabase.CreateAsync(fixture);
        using (var failing = CreateServiceProvider(
                   database.ConnectionString, new FailingMigrationInjector(16)))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => failing
                .GetRequiredService<PostgresMigrationRunner>()
                .MigrateAsync(TestContext.Current.CancellationToken));
        }

        Assert.Equal(
            [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15],
            await ReadAppliedVersionsAsync(database.ConnectionString));
        Assert.Equal("Failed", (await ReadMigrationRecordAsync(database.ConnectionString, 16))!.Status);
        var actionId = await SeedPreProjectionActionAsync(database.ConnectionString, "upgrade-from-v15");
        var costHistory = await SeedCostRollupHistoryAsync(database.ConnectionString, "v15");

        await RunMigrationsAsync(database.ConnectionString);

        Assert.Equal(
            [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17],
            await ReadAppliedVersionsAsync(database.ConnectionString));
        Assert.True(await HasRequiredV02IndexesAndColumnsAsync(database.ConnectionString));
        await AssertPreProjectionActionPreservedAsync(database.ConnectionString, actionId);
        await AssertCostRollupHistoryPreservedAsync(database.ConnectionString, costHistory);
    }

    [DockerAvailableFact]
    public async Task FailedVersion17LeavesVersion16DurableAndThenUpgradesCleanly()
    {
        await using var database = await MigrationDatabase.CreateAsync(fixture);
        using (var failing = CreateServiceProvider(
                   database.ConnectionString, new FailingMigrationInjector(17)))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => failing
                .GetRequiredService<PostgresMigrationRunner>()
                .MigrateAsync(TestContext.Current.CancellationToken));
        }

        Assert.Equal(
            [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16],
            await ReadAppliedVersionsAsync(database.ConnectionString));
        Assert.Equal("Failed", (await ReadMigrationRecordAsync(database.ConnectionString, 17))!.Status);
        var costHistory = await SeedCostRollupHistoryAsync(database.ConnectionString, "v16");

        await RunMigrationsAsync(database.ConnectionString);

        Assert.Equal(
            [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17],
            await ReadAppliedVersionsAsync(database.ConnectionString));
        Assert.True(await HasRequiredV02IndexesAndColumnsAsync(database.ConnectionString));
        await AssertCostRollupHistoryPreservedAsync(database.ConnectionString, costHistory);
    }

    [DockerAvailableFact]
    public async Task MeaningfullyChangedSqlChecksumFailsClosedAndPreservesLedger()
    {
        await using var database = await MigrationDatabase.CreateAsync(fixture);
        await RunMigrationsAsync(database.ConnectionString);
        var migration = Assert.Single(
            PostgresMigrationCatalog.All,
            candidate => candidate.Version == 17);
        var expectedPolicy = await PostgresMigrationChecksumPolicy.CreateAsync(
            migration,
            TestContext.Current.CancellationToken);
        var changedSqlChecksum = await ComputeChangedSqlChecksumAsync(migration);
        Assert.NotEqual(expectedPolicy.CanonicalChecksum.Value, changedSqlChecksum);
        await SetMigrationChecksumAsync(database.ConnectionString, 17, changedSqlChecksum);
        var beforeRestart = await ReadMigrationRecordsAsync(database.ConnectionString);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunMigrationsAsync(database.ConnectionString));

        Assert.Contains("migration 17", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("neither canonical nor an allowed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("trusted backup", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(beforeRestart, await ReadMigrationRecordsAsync(database.ConnectionString));
    }

    [DockerAvailableFact]
    public async Task ChangedMigrationNameFailsBeforeApplyAndPreservesLedger()
    {
        await using var database = await MigrationDatabase.CreateAsync(fixture);
        await RunMigrationsAsync(database.ConnectionString);
        await SetMigrationNameAsync(database.ConnectionString, 17, "renamed-v0.3-migration");
        var beforeRestart = await ReadMigrationRecordsAsync(database.ConnectionString);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunMigrationsAsync(database.ConnectionString));

        Assert.Contains("migration 17", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("durable name", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("released catalog expects", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(beforeRestart, await ReadMigrationRecordsAsync(database.ConnectionString));
    }

    [DockerAvailableFact]
    public async Task RenumberedMigrationFailsBeforeRerunAndPreservesLedger()
    {
        await using var database = await MigrationDatabase.CreateAsync(fixture);
        await RunMigrationsAsync(database.ConnectionString);
        await RenumberMigrationAsync(database.ConnectionString, fromVersion: 17, toVersion: 18);
        var beforeRestart = await ReadMigrationRecordsAsync(database.ConnectionString);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunMigrationsAsync(database.ConnectionString));

        Assert.Contains("unexpected durable version 18", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not present in this released catalog", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not renumber", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(beforeRestart, await ReadMigrationRecordsAsync(database.ConnectionString));
        Assert.DoesNotContain(beforeRestart, record => record.Version == 17);
    }

    [DockerAvailableFact]
    public async Task InjectedFailureMakesMigrationHostedServiceFailStartupAndRecordsDiagnostic()
    {
        await using var database = await MigrationDatabase.CreateAsync(fixture);
        using var host = CreateMigrationHost(database.ConnectionString, new FailingMigrationInjector(2));

        var exception = await Record.ExceptionAsync(() => host.StartAsync());

        Assert.NotNull(exception);
        Assert.Contains("migration 2", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(host.Services.GetRequiredService<IPostgresMigrationReadiness>().IsReady);

        var record = await ReadMigrationRecordAsync(database.ConnectionString, 2);
        Assert.NotNull(record);
        Assert.Equal("v0.2-memory-file-sync", record.Name);
        Assert.Equal("Failed", record.Status);
        Assert.NotNull(record.FailedAtUtc);
        Assert.Contains("injected", record.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        var migration = Assert.Single(
            PostgresMigrationCatalog.All,
            candidate => candidate.Version == 2);
        var policy = await PostgresMigrationChecksumPolicy.CreateAsync(
            migration,
            TestContext.Current.CancellationToken);
        Assert.Equal(policy.CanonicalChecksum.Value, record.Checksum);
    }

    private static async Task<string> ComputeChangedSqlChecksumAsync(
        PostgresSchemaMigration migration)
    {
        var scripts = new List<KeyValuePair<string, string>>(migration.ScriptNames.Count);
        foreach (var scriptName in migration.ScriptNames)
        {
            var sql = await PostgresMigrationScriptExecutor.ReadAsync(
                scriptName,
                TestContext.Current.CancellationToken);
            scripts.Add(new(scriptName, sql + "\nSELECT 1;"));
        }

        return PostgresMigrationChecksumPolicy.Create(
            migration.Version,
            migration.Name,
            scripts,
            acceptsReleasedLegacyChecksums: false).CanonicalChecksum.Value;
    }

    private static async Task SetMigrationNameAsync(
        string connectionString,
        int version,
        string name)
    {
        await using var connection = await OpenAsync(connectionString);
        await using var command = new NpgsqlCommand("""
            UPDATE incidentcompass.schema_migrations
            SET name = $2
            WHERE version = $1;
            """, connection);
        command.Parameters.AddWithValue(version);
        command.Parameters.AddWithValue(name);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }

    private static async Task RenumberMigrationAsync(
        string connectionString,
        int fromVersion,
        int toVersion)
    {
        await using var connection = await OpenAsync(connectionString);
        await using var command = new NpgsqlCommand("""
            UPDATE incidentcompass.schema_migrations
            SET version = $2
            WHERE version = $1;
            """, connection);
        command.Parameters.AddWithValue(fromVersion);
        command.Parameters.AddWithValue(toVersion);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }
}
