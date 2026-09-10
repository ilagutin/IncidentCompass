using System.Globalization;
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
            [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23],
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
            [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23],
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
            [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23],
            await ReadAppliedVersionsAsync(database.ConnectionString));
        Assert.True(await HasRequiredV02IndexesAndColumnsAsync(database.ConnectionString));
        await AssertCostRollupHistoryPreservedAsync(database.ConnectionString, costHistory);
    }

    /// <summary>
    /// Version 21 will not close an ambiguity it did not create. A database that already holds two
    /// prices covering one instant is told which pair is wrong and left alone until an operator
    /// decides which interval to close; picking one here would be exactly the arbitration the read
    /// path refuses to perform.
    /// </summary>
    [DockerAvailableFact]
    public async Task Version21RefusesToUpgradeOverAmbiguousPricesAndNamesThem()
    {
        await using var database = await MigrationDatabase.CreateAsync(fixture);
        using (var failing = CreateServiceProvider(
                   database.ConnectionString, new FailingMigrationInjector(21)))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => failing
                .GetRequiredService<PostgresMigrationRunner>()
                .MigrateAsync(TestContext.Current.CancellationToken));
        }

        await ExecuteAsync(database.ConnectionString, """
            INSERT INTO incidentcompass.ai_model_pricing (
                id, provider, model, currency, input_token_price_per_million,
                output_token_price_per_million, effective_from_utc, effective_to_utc)
            VALUES
                (gen_random_uuid(), 'stale-provider', 'stale-model', 'USD', 1, 2,
                 '2026-01-01T00:00:00Z', '2026-03-01T00:00:00Z'),
                (gen_random_uuid(), 'stale-provider', 'stale-model', 'USD', 3, 4,
                 '2026-02-01T00:00:00Z', NULL);
            """);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunMigrationsAsync(database.ConnectionString));

        Assert.Contains("migration 21", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "overlapping effective intervals",
            exception.InnerException!.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "stale-provider/stale-model",
            exception.InnerException.Message,
            StringComparison.Ordinal);
        Assert.Equal(
            [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20],
            await ReadAppliedVersionsAsync(database.ConnectionString));
        Assert.Equal(2, await CountSqlAsync(database.ConnectionString, """
            SELECT count(*) FROM incidentcompass.ai_model_pricing WHERE provider = 'stale-provider';
            """));

        // Closing one interval by hand is the whole remedy, and it changes no reported figure:
        // both rows were already ambiguous, so both were already unpriced.
        await ExecuteAsync(database.ConnectionString, """
            UPDATE incidentcompass.ai_model_pricing
            SET effective_to_utc = '2026-02-01T00:00:00Z'
            WHERE provider = 'stale-provider' AND effective_from_utc = '2026-01-01T00:00:00Z';
            """);
        await RunMigrationsAsync(database.ConnectionString);

        Assert.Equal(
            [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23],
            await ReadAppliedVersionsAsync(database.ConnectionString));
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

        // Renumbering has to move a durable row onto a version the catalog does not know, so the
        // target is derived as one past the catalog's highest version. A literal would either
        // collide with the newest migration's primary key or stop being unknown once it ships.
        var highestCatalogVersion = PostgresMigrationCatalog.All.Max(migration => migration.Version);
        var unknownVersion = highestCatalogVersion + 1;
        Assert.DoesNotContain(PostgresMigrationCatalog.All, migration => migration.Version == unknownVersion);

        await RenumberMigrationAsync(
            database.ConnectionString,
            fromVersion: highestCatalogVersion,
            toVersion: unknownVersion);
        var beforeRestart = await ReadMigrationRecordsAsync(database.ConnectionString);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunMigrationsAsync(database.ConnectionString));

        Assert.Contains(
            "unexpected durable version " + unknownVersion.ToString(CultureInfo.InvariantCulture),
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not present in this released catalog", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not renumber", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(beforeRestart, await ReadMigrationRecordsAsync(database.ConnectionString));
        Assert.DoesNotContain(beforeRestart, record => record.Version == highestCatalogVersion);
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
