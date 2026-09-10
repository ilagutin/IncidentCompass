using IncidentCompass.Infrastructure.Postgres;
using static IncidentCompass.IntegrationTests.PostgresMigrationDurableDataAssertions;
using static IncidentCompass.IntegrationTests.PostgresMigrationTestSupport;

namespace IncidentCompass.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class PostgresMigrationTests(PostgresRepositoryFixture fixture)
{
    private static readonly string[] ReleasedV011Scripts =
    [
        "001-enable-pgvector.sql",
        "004-observability-cost.sql",
        "006-tool-audit.sql",
        "007-intake.sql",
        "008-triage-ledger.sql",
        "009-triage-reports-minimal.sql",
        "010-memory.sql"
    ];

    [DockerAvailableFact]
    public async Task FreshAndReleasedUpgradeReachTheSameSchemaAndKeepDurableRows()
    {
        await using var fresh = await MigrationDatabase.CreateAsync(fixture);
        await using var upgraded = await MigrationDatabase.CreateAsync(fixture);

        await RunMigrationsAsync(fresh.ConnectionString);
        await PostgresSchemaTestHelper.ApplyReleasedV011ScriptsAsync(
            upgraded.ConnectionString,
            ReleasedV011Scripts);
        await InsertReleasedDurableRowsAsync(upgraded.ConnectionString);
        await RunMigrationsAsync(upgraded.ConnectionString);

        Assert.Equal(
            await ReadSchemaSignatureAsync(fresh.ConnectionString),
            await ReadSchemaSignatureAsync(upgraded.ConnectionString));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20], await ReadAppliedVersionsAsync(fresh.ConnectionString));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20], await ReadAppliedVersionsAsync(upgraded.ConnectionString));
        Assert.True(await HasRequiredV02IndexesAndColumnsAsync(fresh.ConnectionString));
        Assert.True(await HasRequiredV02IndexesAndColumnsAsync(upgraded.ConnectionString));
        Assert.Equal(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            await ReadGuidAsync(
                upgraded.ConnectionString,
                "SELECT job_id FROM incidentcompass.triage_reports WHERE id = '55555555-5555-5555-5555-555555555555';"));
        await AssertReportRowsAreImmutableAsync(upgraded.ConnectionString);
        Assert.Equal(1, await CountSqlAsync(upgraded.ConnectionString, """
            SELECT count(*) FROM incidentcompass.ai_model_pricing
            WHERE id = '99999999-9999-9999-9999-999999999999';
            """));
        Assert.Equal(1, await CountSqlAsync(upgraded.ConnectionString, """
            SELECT count(*) FROM incidentcompass.triage_ledger
            WHERE event_type = 'ModelCall' AND rationale LIKE '%released-safe-route%';
            """));

        foreach (var tableName in new[]
                 {
                     "signals", "faults", "triage_jobs", "triage_artifacts",
                     "triage_ledger", "triage_reports", "triage_evidence", "memory_items", "memory_chunks"
                 })
        {
            Assert.Equal(1, await CountAsync(upgraded.ConnectionString, tableName));
        }
    }

    [DockerAvailableFact]
    public async Task RerunningTheMigratorIsIdempotent()
    {
        await using var database = await MigrationDatabase.CreateAsync(fixture);

        await RunMigrationsAsync(database.ConnectionString);
        var firstRun = await ReadMigrationRecordsAsync(database.ConnectionString);

        await RunMigrationsAsync(database.ConnectionString);
        var secondRun = await ReadMigrationRecordsAsync(database.ConnectionString);

        Assert.Equal(firstRun, secondRun);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20], secondRun.Select(record => record.Version));
        Assert.All(secondRun, record => Assert.Equal("Applied", record.Status));
    }

    [DockerAvailableFact]
    public async Task FreshInstallStoresCanonicalChecksumsForTheReleasedCatalog()
    {
        await using var database = await MigrationDatabase.CreateAsync(fixture);

        await RunMigrationsAsync(database.ConnectionString);

        var records = await ReadMigrationRecordsAsync(database.ConnectionString);
        foreach (var migration in PostgresMigrationCatalog.All)
        {
            var policy = await PostgresMigrationChecksumPolicy.CreateAsync(
                migration,
                TestContext.Current.CancellationToken);
            var record = Assert.Single(records, candidate => candidate.Version == migration.Version);
            Assert.Equal(policy.CanonicalChecksum.Value, record.Checksum);
        }
    }

    [DockerAvailableFact]
    public async Task ReleasedCrlfAndMixedPlatformLedgersRestartWithoutRewritingRows()
    {
        await using var database = await MigrationDatabase.CreateAsync(fixture);
        await RunMigrationsAsync(database.ConnectionString);

        // Only a migration that already shipped can have a durable row written by an older
        // release, so the released flag on the catalog entry - not a version number - decides
        // which rows a legacy CRLF ledger contains. Deriving the set keeps the HasValue
        // assertion strict for every released migration as the catalog grows.
        var releasedMigrations = PostgresMigrationCatalog.All
            .Where(migration => migration.AcceptsReleasedLegacyChecksums)
            .ToArray();
        Assert.NotEmpty(releasedMigrations);

        foreach (var migration in releasedMigrations)
        {
            var policy = await PostgresMigrationChecksumPolicy.CreateAsync(
                migration,
                TestContext.Current.CancellationToken);
            await SetMigrationChecksumAsync(
                database.ConnectionString,
                migration.Version,
                AssertLegacyCrlfChecksum(policy).Value);
        }

        var legacyBeforeRestart = await ReadMigrationRecordsAsync(database.ConnectionString);
        await RunMigrationsAsync(database.ConnectionString);
        Assert.Equal(
            legacyBeforeRestart,
            await ReadMigrationRecordsAsync(database.ConnectionString));

        foreach (var migration in PostgresMigrationCatalog.All.Where(
                     migration => migration.Version % 2 == 0))
        {
            var policy = await PostgresMigrationChecksumPolicy.CreateAsync(
                migration,
                TestContext.Current.CancellationToken);
            await SetMigrationChecksumAsync(
                database.ConnectionString,
                migration.Version,
                policy.CanonicalChecksum.Value);
        }

        var mixedBeforeRestart = await ReadMigrationRecordsAsync(database.ConnectionString);
        await RunMigrationsAsync(database.ConnectionString);
        Assert.Equal(
            mixedBeforeRestart,
            await ReadMigrationRecordsAsync(database.ConnectionString));
    }

    [Fact]
    public async Task FrozenPricingAndLedgerMigrationsMatchRecordedHashes()
    {
        Assert.Equal(
            "541980B5714E9FF5328BE12460C4ED1F3BFB6D242BEAE92773EA361564D03311",
            await Sha256WithCrlfNormalizedToLfAsync("004-observability-cost.sql"));
        Assert.Equal(
            "7120B3CEB408AFABB65625EC000E22C1F62F14D4770C7955C5C82C8AFE10FC3E",
            await Sha256WithCrlfNormalizedToLfAsync("008-triage-ledger.sql"));
    }

    private static PostgresMigrationChecksum AssertLegacyCrlfChecksum(
        PostgresMigrationChecksumPolicy policy)
    {
        Assert.True(policy.ReleasedLegacyCrlfChecksum.HasValue);
        return policy.ReleasedLegacyCrlfChecksum.Value;
    }
}
