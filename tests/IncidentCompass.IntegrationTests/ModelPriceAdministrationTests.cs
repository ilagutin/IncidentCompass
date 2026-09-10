using Npgsql;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// The rails around the one table in this schema whose only writer is an operator at a prompt.
/// </summary>
/// <remarks>
/// Every case here is written the way the runbook tells an operator to write it, so a change that
/// breaks the documented procedure breaks a test rather than a production database.
/// </remarks>
[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class ModelPriceAdministrationTests(PostgresRepositoryFixture postgres)
{
    private const string Operator = "ops:price-runbook";

    [DockerAvailableFact]
    public async Task AttributedInsertIsAcceptedAndTheChangeTimeIsStampedByTheDatabase()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var priceId = Guid.NewGuid();

        await InsertPriceAsync(
            database.ConnectionString,
            priceId,
            "attributed-provider",
            "attributed-model",
            "2026-08-01T00:00:00Z",
            null,
            administeredBy: Operator,
            // A backdated claim in the statement is discarded: the database stamps the moment.
            administeredAtUtc: "2000-01-01T00:00:00Z");

        Assert.Equal(1L, await CountAsync(database.ConnectionString, """
            SELECT count(*) FROM incidentcompass.ai_model_pricing
            WHERE id = @id
              AND administered_by = @operator
              AND administered_at_utc > '2020-01-01T00:00:00Z'
              AND administered_at_utc >= created_at_utc;
            """, ("id", priceId), ("operator", Operator)));
    }

    [DockerAvailableFact]
    public async Task PriceWrittenOrChangedWithoutANameIsRefused()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var priceId = Guid.NewGuid();

        var insertFailure = await ExpectPostgresFailureAsync(() => InsertPriceAsync(
            database.ConnectionString,
            priceId,
            "unattributed-provider",
            "unattributed-model",
            "2026-08-01T00:00:00Z",
            null,
            administeredBy: null));
        Assert.Contains("must name who changed it", insertFailure.MessageText, StringComparison.Ordinal);

        var blankFailure = await ExpectPostgresFailureAsync(() => InsertPriceAsync(
            database.ConnectionString,
            priceId,
            "unattributed-provider",
            "unattributed-model",
            "2026-08-01T00:00:00Z",
            null,
            administeredBy: "   "));
        Assert.Contains("must name who changed it", blankFailure.MessageText, StringComparison.Ordinal);

        await InsertPriceAsync(
            database.ConnectionString,
            priceId,
            "unattributed-provider",
            "unattributed-model",
            "2026-08-01T00:00:00Z",
            null,
            administeredBy: Operator);

        var updateFailure = await ExpectPostgresFailureAsync(() => ExecuteAsync(
            database.ConnectionString,
            """
            UPDATE incidentcompass.ai_model_pricing
            SET input_token_price_per_million = 9, administered_by = NULL
            WHERE id = @id;
            """,
            ("id", priceId)));
        Assert.Contains("must name who changed it", updateFailure.MessageText, StringComparison.Ordinal);
    }

    [DockerAvailableFact]
    public async Task CorrectingAPriceReStampsTheChangeTimeAndLeavesTheCreationTimeAlone()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var priceId = Guid.NewGuid();
        await InsertPriceAsync(
            database.ConnectionString,
            priceId,
            "corrected-provider",
            "corrected-model",
            "2026-08-01T00:00:00Z",
            null,
            administeredBy: Operator);
        var created = await ReadTimestampAsync(
            database.ConnectionString,
            "SELECT created_at_utc FROM incidentcompass.ai_model_pricing WHERE id = @id;",
            priceId);
        var administeredBeforeCorrection = await ReadTimestampAsync(
            database.ConnectionString,
            "SELECT administered_at_utc FROM incidentcompass.ai_model_pricing WHERE id = @id;",
            priceId);

        await ExecuteAsync(
            database.ConnectionString,
            """
            UPDATE incidentcompass.ai_model_pricing
            SET input_token_price_per_million = 3,
                administered_by = 'ops:correction',
                administration_note = 'Rate was transcribed from the wrong column of the price list.',
                created_at_utc = '2000-01-01T00:00:00Z'
            WHERE id = @id;
            """,
            ("id", priceId));

        Assert.Equal(
            created,
            await ReadTimestampAsync(
                database.ConnectionString,
                "SELECT created_at_utc FROM incidentcompass.ai_model_pricing WHERE id = @id;",
                priceId));
        Assert.True(await ReadTimestampAsync(
            database.ConnectionString,
            "SELECT administered_at_utc FROM incidentcompass.ai_model_pricing WHERE id = @id;",
            priceId) >= administeredBeforeCorrection);
        Assert.Equal(1L, await CountAsync(database.ConnectionString, """
            SELECT count(*) FROM incidentcompass.ai_model_pricing
            WHERE id = @id AND administered_by = 'ops:correction'
              AND administration_note IS NOT NULL;
            """, ("id", priceId)));
    }

    [DockerAvailableFact]
    public async Task OverlappingIntervalForOneProviderAndModelIsRefusedWhileAdjacentAndDistinctOnesAreNot()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        await InsertPriceAsync(
            database.ConnectionString,
            Guid.NewGuid(),
            "overlap-provider",
            "overlap-model",
            "2026-08-01T00:00:00Z",
            "2026-09-01T00:00:00Z",
            administeredBy: Operator);

        foreach (var (fromUtc, toUtc) in new[]
                 {
                     ("2026-08-15T00:00:00Z", "2026-09-15T00:00:00Z"),
                     ("2026-08-01T00:00:00Z", "2026-09-01T00:00:00Z"),
                     ("2026-07-01T00:00:00Z", (string?)null),
                     ("2026-08-10T00:00:00Z", "2026-08-11T00:00:00Z")
                 })
        {
            var failure = await ExpectPostgresFailureAsync(() => InsertPriceAsync(
                database.ConnectionString,
                Guid.NewGuid(),
                "overlap-provider",
                "overlap-model",
                fromUtc,
                toUtc,
                administeredBy: Operator));
            Assert.Equal("23P01", failure.SqlState);
            Assert.Equal("ex_ai_model_pricing_no_overlap", failure.ConstraintName);
        }

        // A price change is expressed as one interval ending exactly where the next begins. The
        // bound is half-open, so this is the shape the constraint has to keep allowing.
        await InsertPriceAsync(
            database.ConnectionString,
            Guid.NewGuid(),
            "overlap-provider",
            "overlap-model",
            "2026-09-01T00:00:00Z",
            null,
            administeredBy: Operator);

        // Neither the model nor the provider is shared, and case is not folded, matching the
        // case-sensitive lookup the rollup performs.
        await InsertPriceAsync(
            database.ConnectionString,
            Guid.NewGuid(),
            "overlap-provider",
            "Overlap-Model",
            "2026-08-01T00:00:00Z",
            null,
            administeredBy: Operator);
        await InsertPriceAsync(
            database.ConnectionString,
            Guid.NewGuid(),
            "Overlap-Provider",
            "overlap-model",
            "2026-08-01T00:00:00Z",
            null,
            administeredBy: Operator);

        var moved = await ExpectPostgresFailureAsync(() => ExecuteAsync(
            database.ConnectionString,
            """
            UPDATE incidentcompass.ai_model_pricing
            SET effective_from_utc = '2026-08-02T00:00:00Z', administered_by = 'ops:typo'
            WHERE provider = 'overlap-provider' AND model = 'overlap-model'
              AND effective_from_utc = '2026-09-01T00:00:00Z';
            """));
        Assert.Equal("23P01", moved.SqlState);
    }

    [DockerAvailableFact]
    public async Task DeletingAPriceIsRefusedAndRetirementIsTheSupportedAlternative()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var priceId = Guid.NewGuid();
        await InsertPriceAsync(
            database.ConnectionString,
            priceId,
            "retired-provider",
            "retired-model",
            "2026-08-01T00:00:00Z",
            null,
            administeredBy: Operator);

        var failure = await ExpectPostgresFailureAsync(() => ExecuteAsync(
            database.ConnectionString,
            "DELETE FROM incidentcompass.ai_model_pricing WHERE id = @id;",
            ("id", priceId)));
        Assert.Contains("retired by setting effective_to_utc", failure.MessageText, StringComparison.Ordinal);

        var seedFailure = await ExpectPostgresFailureAsync(() => ExecuteAsync(
            database.ConnectionString,
            "DELETE FROM incidentcompass.ai_model_pricing WHERE provider = 'mock';"));
        Assert.Contains("retired by setting effective_to_utc", seedFailure.MessageText, StringComparison.Ordinal);

        await ExecuteAsync(
            database.ConnectionString,
            """
            UPDATE incidentcompass.ai_model_pricing
            SET effective_to_utc = '2026-09-01T00:00:00Z',
                administered_by = 'ops:retirement',
                administration_note = 'Model withdrawn by the provider.'
            WHERE id = @id;
            """,
            ("id", priceId));
        await InsertPriceAsync(
            database.ConnectionString,
            Guid.NewGuid(),
            "retired-provider",
            "retired-model",
            "2026-09-01T00:00:00Z",
            null,
            administeredBy: Operator);

        // The retired row is still there, still priced for every hour it covered.
        Assert.Equal(1L, await CountAsync(database.ConnectionString, """
            SELECT count(*) FROM incidentcompass.ai_model_pricing
            WHERE id = @id AND effective_to_utc = '2026-09-01T00:00:00Z';
            """, ("id", priceId)));
    }

    [DockerAvailableFact]
    public async Task PricesSeededWithTheSchemaNameTheSchemaAsTheirAuthor()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);

        Assert.Equal(5L, await CountAsync(database.ConnectionString, """
            SELECT count(*) FROM incidentcompass.ai_model_pricing
            WHERE provider = 'mock'
              AND administered_by = 'schema:004-observability-cost.sql'
              AND administered_at_utc IS NOT NULL;
            """));
    }

    /// <summary>
    /// Every script under <c>infra/postgres/init</c> is written to be a no-op when re-applied, and
    /// the seed insert in <c>004-observability-cost.sql</c> is one of them. The attribution rule
    /// must not turn that into a script that can only be run once.
    /// </summary>
    [DockerAvailableFact]
    public async Task ReApplyingTheSeedAndAdministrationScriptsChangesNothing()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var before = await ReadTimestampAsync(
            database.ConnectionString,
            "SELECT administered_at_utc FROM incidentcompass.ai_model_pricing WHERE id = @id;",
            Guid.Parse("00000000-0000-0000-0000-000000000501"));

        await PostgresSchemaTestHelper.ApplyInitScriptsAsync(
            database.ConnectionString,
            "004-observability-cost.sql",
            "030-model-price-administration.sql");

        Assert.Equal(5L, await CountAsync(database.ConnectionString, """
            SELECT count(*) FROM incidentcompass.ai_model_pricing WHERE provider = 'mock';
            """));
        Assert.Equal(
            before,
            await ReadTimestampAsync(
                database.ConnectionString,
                "SELECT administered_at_utc FROM incidentcompass.ai_model_pricing WHERE id = @id;",
                Guid.Parse("00000000-0000-0000-0000-000000000501")));
    }

    internal static Task InsertPriceAsync(
        string connectionString,
        Guid id,
        string provider,
        string model,
        string effectiveFromUtc,
        string? effectiveToUtc,
        string? administeredBy,
        string? administeredAtUtc = null) =>
        ExecuteAsync(
            connectionString,
            """
            INSERT INTO incidentcompass.ai_model_pricing (
                id, provider, model, currency, input_token_price_per_million,
                output_token_price_per_million, effective_from_utc, effective_to_utc,
                administered_by, administered_at_utc)
            VALUES (@id, @provider, @model, 'USD', 1, 2,
                CAST(@from_utc AS timestamptz), CAST(@to_utc AS timestamptz),
                @administered_by, CAST(@administered_at AS timestamptz));
            """,
            ("id", id),
            ("provider", provider),
            ("model", model),
            ("from_utc", effectiveFromUtc),
            ("to_utc", (object?)effectiveToUtc ?? DBNull.Value),
            ("administered_by", (object?)administeredBy ?? DBNull.Value),
            ("administered_at", (object?)administeredAtUtc ?? DBNull.Value));

    private static async Task<PostgresException> ExpectPostgresFailureAsync(Func<Task> action) =>
        Assert.IsType<PostgresException>(await Record.ExceptionAsync(action));

    private static async Task<DateTime> ReadTimestampAsync(
        string connectionString,
        string sql,
        Guid id) =>
        (DateTime)(await ActionApprovalTestSupport.ScalarAsync(connectionString, sql, ("id", id)))!;

    private static Task<long> CountAsync(
        string connectionString,
        string sql,
        params (string Name, object Value)[] parameters) =>
        ActionApprovalTestSupport.CountAsync(connectionString, sql, parameters);

    private static Task ExecuteAsync(
        string connectionString,
        string sql,
        params (string Name, object Value)[] parameters) =>
        ActionApprovalTestSupport.ExecuteAsync(connectionString, sql, parameters);
}
