using System.Globalization;
using System.Text.Json;
using IncidentCompass.Application;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Application.Observability.CostRollup;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class ModelCostRollupTests(PostgresRepositoryFixture postgres)
{
    /// <summary>
    /// The one adapter identifier every OpenAI-compatible provider answers under, which is why it
    /// cannot be the pricing key.
    /// </summary>
    private const string SharedAdapter = "openai-compatible";

    private static readonly DateTimeOffset WindowStart = DateTimeOffset.Parse("2026-08-01T00:15:00Z", CultureInfo.InvariantCulture);
    private static readonly DateTimeOffset WindowEnd = DateTimeOffset.Parse("2026-08-01T03:00:00Z", CultureInfo.InvariantCulture);

    [DockerAvailableFact]
    public async Task ValidMalformedUnpricedAndAmbiguousHistoryRollsUpFailClosed()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var tenantA = await SeedJobAsync(database.ConnectionString, "tenant-a");
        var tenantB = await SeedJobAsync(database.ConnectionString, "tenant-b");
        await SeedPricingMatrixAsync(database.ConnectionString);

        await InsertCallAsync(database.ConnectionString, tenantA, WindowStart, Usage("alpha", "Model-A", 100_000, 200_000, 300_000));
        await InsertCallAsync(database.ConnectionString, tenantA, DateTimeOffset.Parse("2026-08-01T00:59:59Z", CultureInfo.InvariantCulture), Usage("alpha", "Model-A", 10, 20, 30));
        await InsertCallAsync(database.ConnectionString, tenantA, DateTimeOffset.Parse("2026-08-01T00:30:00Z", CultureInfo.InvariantCulture), FailedUsage("alpha", "Model-A", 100, 200, 300));
        await InsertCallAsync(database.ConnectionString, tenantA, DateTimeOffset.Parse("2026-08-01T01:00:00Z", CultureInfo.InvariantCulture), Usage("alpha", "Model-A", 1_000_000, 1_000_000, 2_000_000));
        await InsertCallAsync(database.ConnectionString, tenantA, DateTimeOffset.Parse("2026-08-01T01:05:00Z", CultureInfo.InvariantCulture), Usage("beta", "Model-B", 1_000_000, 0, 1_000_000));
        await InsertCallAsync(database.ConnectionString, tenantA, DateTimeOffset.Parse("2026-08-01T01:10:00Z", CultureInfo.InvariantCulture), Usage("missing", "Model-C", 7, 8, 15));
        await InsertCallAsync(database.ConnectionString, tenantA, DateTimeOffset.Parse("2026-08-01T01:15:00Z", CultureInfo.InvariantCulture), Usage("caseprovider", "CaseModel", 9, 10, 19));
        await InsertCallAsync(database.ConnectionString, tenantA, DateTimeOffset.Parse("2026-08-01T01:20:00Z", CultureInfo.InvariantCulture), Usage("overlap", "Model-O", 11, 12, 23));
        await InsertCallAsync(database.ConnectionString, tenantA, DateTimeOffset.Parse("2026-08-01T01:25:00Z", CultureInfo.InvariantCulture), Usage("tie", "Model-T", 13, 14, 27));
        await InsertCallAsync(database.ConnectionString, tenantA, DateTimeOffset.Parse("2026-08-01T01:30:00Z", CultureInfo.InvariantCulture), Usage("mock", "mock-chat", 21, 22, 43));

        var invalidRows = new[]
        {
            "{",
            "{\"provider\":\"alpha\",\"model\":\"Model-A\",\"inputTokens\":\"1\",\"outputTokens\":2,\"totalTokens\":3}",
            Usage(" ", "Model-A", 1, 2, 3),
            Usage("alpha", "Model-A", -1, 2, 1),
            "{\"provider\":\"alpha\",\"model\":\"Model-A\",\"inputTokens\":2147483648,\"outputTokens\":2,\"totalTokens\":3}",
            "{\"provider\":\"alpha\",\"provider\":\"beta\",\"model\":\"Model-A\",\"inputTokens\":1,\"outputTokens\":2,\"totalTokens\":3}"
        };
        for (var index = 0; index < invalidRows.Length; index++)
        {
            await InsertCallAsync(
                database.ConnectionString,
                tenantA,
                DateTimeOffset.Parse("2026-08-01T02:00:00Z", CultureInfo.InvariantCulture).AddMinutes(index),
                invalidRows[index]);
        }

        await InsertCallAsync(database.ConnectionString, tenantA, WindowStart.AddTicks(-1), Usage("alpha", "Model-A", 99, 99, 198));
        await InsertCallAsync(database.ConnectionString, tenantA, WindowEnd, Usage("alpha", "Model-A", 99, 99, 198));
        await InsertCallAsync(database.ConnectionString, tenantB, WindowStart, Usage("alpha", "Model-A", 999, 999, 1_998));

        using var services = CreateServices(database.ConnectionString);
        var hours = await services.GetRequiredService<IModelCostRollupRepository>().ReadAsync(
            "tenant-a", WindowStart, WindowEnd, TestContext.Current.CancellationToken);

        Assert.Collection(
            hours,
            hour => AssertHour(hour, "2026-08-01T00:00:00Z", 3, 100_110, 200_220, 300_330, 3, 0, ("USD", 0.650715m)),
            hour => AssertHour(hour, "2026-08-01T01:00:00Z", 7, 2_000_061, 1_000_066, 3_000_127, 3, 4, ("EUR", 4m), ("USD", 5m)),
            hour => AssertHour(hour, "2026-08-01T02:00:00Z", 6, 0, 0, 0, 0, 6));
    }

    [DockerAvailableFact]
    public async Task FailedCallWithUnknownUsageCountsAsUnpricedWithoutTokensOrSpend()
    {
        var now = DateTimeOffset.Parse("2026-08-03T14:20:00Z", CultureInfo.InvariantCulture);
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var seeded = await SeedJobAsync(database.ConnectionString, "tenant-a", now);
        await InsertPriceAsync(
            database.ConnectionString,
            "failed-provider",
            "failed-model",
            "USD",
            1m,
            1m,
            now.AddHours(-1),
            now.AddHours(1));
        await InsertCallAsync(
            database.ConnectionString,
            seeded,
            now,
            FailedUnknownUsage());
        using var services = CreateServices(database.ConnectionString);

        var hours = await services.GetRequiredService<IModelCostRollupRepository>().ReadAsync(
            "tenant-a",
            now.AddMinutes(-1),
            now.AddMinutes(1),
            TestContext.Current.CancellationToken);

        AssertHour(
            Assert.Single(hours),
            "2026-08-03T14:00:00Z",
            calls: 1,
            input: 0,
            output: 0,
            total: 0,
            priced: 0,
            unpriced: 1);
    }

    [DockerAvailableFact]
    public async Task ExistingWriterKeepsLogicalRouteAndExcludesSensitiveCallMaterial()
    {
        const string promptSentinel = "prompt-secret-sentinel";
        const string responseSentinel = "provider-body-sentinel";
        const string endpointSentinel = "https://provider-endpoint-sentinel.invalid";
        const string credentialSentinel = "credential-secret-sentinel";
        const string vectorSentinel = "embedding-vector-sentinel";
        var now = DateTimeOffset.Parse("2026-08-02T12:30:00Z", CultureInfo.InvariantCulture);
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var seeded = await SeedJobAsync(database.ConnectionString, "tenant-a", now);
        await InsertPriceAsync(
            database.ConnectionString,
            "writer-provider",
            "writer-model",
            "USD",
            1m,
            1m,
            now.AddHours(-1),
            now.AddHours(1));
        var timeProvider = new ManualTimeProvider(now);
        using var services = CreateServices(database.ConnectionString, timeProvider);
        var configuration = CreateTriageConfiguration(seeded.ConfigHash, endpointSentinel, credentialSentinel);
        var caller = new InvestigationModelCaller(
            new StaticModelClient(responseSentinel),
            services.GetRequiredService<ITriageLedgerReader>(),
            new TriageLedgerAppender(services.GetRequiredService<ITriageLedgerWriter>()),
            timeProvider);

        await caller.CompleteAsync(
            new TriageJobCallContext(seeded.Job, configuration, now.AddSeconds(-30), "safe-logical-route", "orchestrator"),
            configuration.Routes["safe-logical-route"],
            [new AiChatMessage(AiMessageRole.User, $"{promptSentinel} {vectorSentinel}")],
            tools: null,
            TestContext.Current.CancellationToken);

        var rationale = Convert.ToString(await ActionApprovalTestSupport.ScalarAsync(database.ConnectionString, """
            SELECT rationale FROM incidentcompass.triage_ledger
            WHERE job_id = @job AND event_type = 'ModelCall';
            """, ("job", seeded.Job.Id)), CultureInfo.InvariantCulture)!;
        using var metadata = JsonDocument.Parse(rationale);
        Assert.Equal("safe-logical-route", metadata.RootElement.GetProperty("routeId").GetString());

        // The adapter answered under its own shared name; the row still names the payer, which is
        // the only reason the priced assertion below can hold.
        Assert.Equal(SharedAdapter, metadata.RootElement.GetProperty("provider").GetString());
        Assert.Equal("writer-provider", metadata.RootElement.GetProperty("providerId").GetString());
        Assert.Equal(1, await ActionApprovalTestSupport.CountAsync(database.ConnectionString, """
            SELECT count(*) FROM incidentcompass.triage_ledger
            WHERE job_id = @job AND event_type = 'BudgetEvent' AND tokens_delta = 300;
            """, ("job", seeded.Job.Id)));
        var durableText = string.Join(' ', await ReadLedgerTextAsync(database.ConnectionString, seeded.Job.Id));
        foreach (var sentinel in new[] { promptSentinel, responseSentinel, endpointSentinel, credentialSentinel, vectorSentinel })
        {
            Assert.DoesNotContain(sentinel, durableText, StringComparison.Ordinal);
        }

        var hours = await services.GetRequiredService<IModelCostRollupRepository>().ReadAsync(
            "tenant-a", now.AddMinutes(-1), now.AddMinutes(1), TestContext.Current.CancellationToken);
        AssertHour(Assert.Single(hours), "2026-08-02T12:00:00Z", 1, 100, 200, 300, 1, 0, ("USD", 0.0003m));
    }

    /// <summary>
    /// Two providers a host declared separately, with separate endpoints, separate credentials and
    /// separate prices, answered by one adapter under one model name. They are two payers, and the
    /// rollup has to bill them apart.
    /// </summary>
    [DockerAvailableFact]
    public async Task TwoConfiguredProvidersOnOneAdapterAndModelNameAreBilledSeparately()
    {
        var now = DateTimeOffset.Parse("2026-08-05T09:20:00Z", CultureInfo.InvariantCulture);
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var seeded = await SeedJobAsync(database.ConnectionString, "tenant-a", now);
        await InsertPriceAsync(
            database.ConnectionString, "provider-cheap", "shared-model", "USD", 1m, 2m,
            now.AddHours(-1), now.AddHours(1));
        await InsertPriceAsync(
            database.ConnectionString, "provider-dear", "shared-model", "EUR", 10m, 20m,
            now.AddHours(-1), now.AddHours(1));
        await InsertCallAsync(
            database.ConnectionString, seeded, now,
            Usage("provider-cheap", "shared-model", 1_000_000, 1_000_000, 2_000_000));
        await InsertCallAsync(
            database.ConnectionString, seeded, now.AddMinutes(5),
            Usage("provider-dear", "shared-model", 1_000_000, 1_000_000, 2_000_000));
        using var services = CreateServices(database.ConnectionString);

        var hours = await services.GetRequiredService<IModelCostRollupRepository>().ReadAsync(
            "tenant-a", now.AddHours(-1), now.AddHours(1), TestContext.Current.CancellationToken);

        // 3 and 30, not 6 or 60: each call was priced by the provider that answered it. A rollup
        // keyed on the shared adapter name could produce neither of these two totals at once.
        AssertHour(
            Assert.Single(hours), "2026-08-05T09:00:00Z",
            calls: 2, input: 2_000_000, output: 2_000_000, total: 4_000_000,
            priced: 2, unpriced: 0, ("EUR", 30m), ("USD", 3m));
    }

    /// <summary>
    /// A row written before the configured provider was recorded still parses and is still
    /// accounted for. What it cannot do is buy a price: it names an adapter, and an adapter is not
    /// a payer, so the call and its tokens are counted and its spend is not invented.
    /// </summary>
    [DockerAvailableFact]
    public async Task RowWrittenBeforeTheConfiguredProviderWasRecordedCountsButIsNeverPriced()
    {
        var now = DateTimeOffset.Parse("2026-08-06T08:40:00Z", CultureInfo.InvariantCulture);
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var seeded = await SeedJobAsync(database.ConnectionString, "tenant-a", now);

        // Priced under both names the row could plausibly be matched by, so the assertion below is
        // about the rollup refusing to guess rather than about a missing price row.
        await InsertPriceAsync(
            database.ConnectionString, "legacy-provider", "legacy-model", "USD", 1m, 2m,
            now.AddHours(-1), now.AddHours(1));
        await InsertPriceAsync(
            database.ConnectionString, SharedAdapter, "legacy-model", "USD", 1m, 2m,
            now.AddHours(-1), now.AddHours(1));
        await InsertCallAsync(
            database.ConnectionString, seeded, now,
            LegacyUsage("legacy-provider", "legacy-model", 100, 200, 300));
        using var services = CreateServices(database.ConnectionString);

        var hours = await services.GetRequiredService<IModelCostRollupRepository>().ReadAsync(
            "tenant-a", now.AddHours(-1), now.AddHours(1), TestContext.Current.CancellationToken);

        AssertHour(
            Assert.Single(hours), "2026-08-06T08:00:00Z",
            calls: 1, input: 100, output: 200, total: 300, priced: 0, unpriced: 1);
    }

    /// <summary>
    /// A locally estimated token count is not a measurement, so it never becomes money, and the
    /// response says how much of the hour that covered. Otherwise a spend figure would be part
    /// invoice and part guess with nothing to tell the reader which.
    /// </summary>
    [DockerAvailableFact]
    public async Task EstimatedUsageIsCountedAndVisibleButNeverPricedBesideAMeasuredCall()
    {
        var now = DateTimeOffset.Parse("2026-08-07T07:10:00Z", CultureInfo.InvariantCulture);
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var seeded = await SeedJobAsync(database.ConnectionString, "tenant-a", now);
        await InsertPriceAsync(
            database.ConnectionString, "estimate-provider", "estimate-model", "USD", 1m, 2m,
            now.AddHours(-1), now.AddHours(1));
        await InsertCallAsync(
            database.ConnectionString, seeded, now,
            Usage("estimate-provider", "estimate-model", 100, 200, 300));
        await InsertCallAsync(
            database.ConnectionString, seeded, now.AddMinutes(5),
            Usage("estimate-provider", "estimate-model", 400, 500, 900, usageSource: "estimate"));
        using var services = CreateServices(database.ConnectionString);

        var hours = await services.GetRequiredService<IModelCostRollupRepository>().ReadAsync(
            "tenant-a", now.AddHours(-1), now.AddHours(1), TestContext.Current.CancellationToken);

        // Both calls counted, both sets of tokens counted, and the 0.0005 is the measured call
        // alone: the estimated call had a price waiting for it and was still not charged.
        AssertHour(
            Assert.Single(hours), "2026-08-07T07:00:00Z",
            calls: 2, input: 500, output: 700, total: 1_200,
            priced: 1, unpriced: 1, estimatedCalls: 1, estimatedTokens: 900, ("USD", 0.0005m));
    }

    private static ServiceProvider CreateServices(
        string connectionString,
        TimeProvider? timeProvider = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:CostRollupTests"] = connectionString,
            ["IncidentCompass:Postgres:ConnectionStringName"] = "CostRollupTests"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        if (timeProvider is not null)
        {
            services.AddSingleton(timeProvider);
        }
        services.AddApplication(configuration);
        services.AddInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    private static async Task<SeededJob> SeedJobAsync(
        string connectionString,
        string tenantId,
        DateTimeOffset? createdAtUtc = null)
    {
        var atUtc = createdAtUtc ?? WindowStart;
        var signalId = Guid.NewGuid();
        var faultId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var configHash = "cost-rollup-" + Guid.NewGuid().ToString("N");
        await ActionApprovalTestSupport.ExecuteAsync(connectionString, """
            INSERT INTO incidentcompass.triage_config_snapshots (config_hash, serialized_config, instructions, created_at_utc)
            VALUES (@config, '{}'::jsonb, '{}'::jsonb, @at);
            INSERT INTO incidentcompass.signals (
                id, tenant_id, source, fingerprint, fingerprint_version, fingerprint_strength,
                service_name, environment, error_type, summary, body, observed_at_utc, received_at_utc)
            VALUES (@signal, @tenant, 'cost-test', @fingerprint, 1, 'strong',
                'cost-service', 'test', 'CostProbe', 'cost probe', '{}'::jsonb, @at, @at);
            INSERT INTO incidentcompass.faults (
                id, trigger_signal_id, tenant_id, status, fingerprint, fingerprint_version,
                fingerprint_strength, service_name, environment, created_at_utc)
            VALUES (@fault, @signal, @tenant, 'Analyzing', @fingerprint, 1,
                'strong', 'cost-service', 'test', @at);
            UPDATE incidentcompass.signals SET fault_id = @fault WHERE id = @signal;
            INSERT INTO incidentcompass.triage_jobs (
                id, fault_id, status, attempt, config_hash, created_at_utc, updated_at_utc)
            VALUES (@job, @fault, 'Processing', 1, @config, @at, @at);
            """,
            ("config", configHash), ("at", atUtc), ("signal", signalId), ("tenant", tenantId),
            ("fingerprint", "cost-" + Guid.NewGuid().ToString("N")), ("fault", faultId), ("job", jobId));
        return new SeededJob(
            new TriageJob(jobId, faultId, TriageJobStatus.Processing, 1, null, null, null, null, null, configHash, atUtc, atUtc),
            configHash);
    }

    private static Task InsertCallAsync(string connectionString, SeededJob job, DateTimeOffset atUtc, string rationale) =>
        ActionApprovalTestSupport.ExecuteAsync(connectionString, """
            INSERT INTO incidentcompass.triage_ledger (
                fault_id, job_id, attempt, event_type, rationale, config_hash, created_at_utc)
            VALUES (@fault, @job, 1, 'ModelCall', @rationale, @config, @at);
            """, ("fault", job.Job.FaultId), ("job", job.Job.Id), ("rationale", rationale),
            ("config", job.ConfigHash), ("at", atUtc));

    private static async Task SeedPricingMatrixAsync(string connectionString)
    {
        await InsertPriceAsync(connectionString, "alpha", "Model-A", "USD", 1.5m, 2.5m, DateTimeOffset.Parse("2026-08-01T00:00:00Z", CultureInfo.InvariantCulture), DateTimeOffset.Parse("2026-08-01T01:00:00Z", CultureInfo.InvariantCulture));
        await InsertPriceAsync(connectionString, "alpha", "Model-A", "USD", 2m, 3m, DateTimeOffset.Parse("2026-08-01T01:00:00Z", CultureInfo.InvariantCulture), WindowEnd);
        await InsertPriceAsync(connectionString, "beta", "Model-B", "EUR", 4m, 6m, WindowStart, WindowEnd);
        await InsertPriceAsync(connectionString, "CaseProvider", "CaseModel", "USD", 1m, 1m, WindowStart, WindowEnd);
        await InsertPriceAsync(connectionString, "overlap", "Model-O", "USD", 1m, 1m, WindowStart, WindowEnd);
        await InsertPriceAsync(connectionString, "overlap", "Model-O", "USD", 2m, 2m, DateTimeOffset.Parse("2026-08-01T01:00:00Z", CultureInfo.InvariantCulture), DateTimeOffset.Parse("2026-08-01T02:00:00Z", CultureInfo.InvariantCulture));
        await InsertPriceAsync(connectionString, "tie", "Model-T", "USD", 1m, 1m, WindowStart, WindowEnd);
        await InsertPriceAsync(connectionString, "tie", "Model-T", "USD", 2m, 2m, WindowStart, WindowEnd);
    }

    private static Task InsertPriceAsync(
        string connectionString,
        string provider,
        string model,
        string currency,
        decimal inputPrice,
        decimal outputPrice,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc) =>
        ActionApprovalTestSupport.ExecuteAsync(connectionString, """
            INSERT INTO incidentcompass.ai_model_pricing (
                id, provider, model, currency, input_token_price_per_million,
                output_token_price_per_million, effective_from_utc, effective_to_utc)
            VALUES (@id, @provider, @model, @currency, @input, @output, @from, @to);
            """, ("id", Guid.NewGuid()), ("provider", provider), ("model", model), ("currency", currency),
            ("input", inputPrice), ("output", outputPrice), ("from", fromUtc), ("to", toUtc));

    /// <summary>
    /// A row as the current writer writes it: the adapter under <c>provider</c>, which every
    /// declared provider on that adapter shares, and the configured provider under
    /// <c>providerId</c>, which is the identity that has a price.
    /// </summary>
    private static string Usage(
        string providerId,
        string model,
        int input,
        int output,
        int total,
        string usageSource = "provider") =>
        JsonSerializer.Serialize(new
        {
            kind = "orchestrator",
            routeId = "safe-logical-route",
            provider = SharedAdapter,
            model,
            usageSource,
            inputTokens = input,
            outputTokens = output,
            totalTokens = total,
            providerId
        });

    /// <summary>
    /// A row exactly as it was written before the configured provider was recorded: it names the
    /// adapter and nothing else about who answered.
    /// </summary>
    private static string LegacyUsage(string provider, string model, int input, int output, int total) =>
        JsonSerializer.Serialize(new
        {
            kind = "orchestrator",
            routeId = "safe-logical-route",
            provider,
            model,
            usageSource = "provider",
            inputTokens = input,
            outputTokens = output,
            totalTokens = total
        });

    private static string FailedUsage(string providerId, string model, int input, int output, int total) =>
        JsonSerializer.Serialize(new
        {
            kind = "worker",
            routeId = "analysis-chat",
            provider = SharedAdapter,
            model,
            usageSource = "provider",
            inputTokens = input,
            outputTokens = output,
            totalTokens = total,
            durationMs = 100,
            proposedToolCallCount = 0,
            callId = Guid.NewGuid(),
            outcome = "failed",
            errorCode = "provider_generation_timeout",
            providerId
        });

    private static string FailedUnknownUsage() =>
        JsonSerializer.Serialize(new
        {
            kind = "worker",
            routeId = "analysis-chat",
            model = "failed-model",
            provider = SharedAdapter,
            usageSource = "unknown",
            inputTokens = (int?)null,
            outputTokens = (int?)null,
            totalTokens = (int?)null,
            durationMs = 125,
            proposedToolCallCount = 0,
            callId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            outcome = "failed",
            errorCode = "provider_generation_timeout",
            providerId = "failed-provider"
        });

    private static void AssertHour(
        CostRollupHour actual,
        string hourUtc,
        long calls,
        long input,
        long output,
        long total,
        long priced,
        long unpriced,
        params (string Currency, decimal Amount)[] spend)
    {
        AssertHour(actual, hourUtc, calls, input, output, total, priced, unpriced, 0, 0, spend);
    }

    private static void AssertHour(
        CostRollupHour actual,
        string hourUtc,
        long calls,
        long input,
        long output,
        long total,
        long priced,
        long unpriced,
        long estimatedCalls,
        long estimatedTokens,
        params (string Currency, decimal Amount)[] spend)
    {
        Assert.Equal(DateTimeOffset.Parse(hourUtc, CultureInfo.InvariantCulture), actual.HourUtc);
        Assert.Equal((calls, input, output, total, priced, unpriced),
            (actual.CallCount, actual.InputTokens, actual.OutputTokens, actual.TotalTokens,
             actual.PricedCallCount, actual.UnpricedCallCount));
        Assert.Equal(
            (estimatedCalls, estimatedTokens),
            (actual.EstimatedUsageCallCount, actual.EstimatedUsageTotalTokens));
        Assert.Equal(spend, actual.SpendTotals.Select(item => (item.Currency, item.Amount)));
    }

    private static async Task<IReadOnlyList<string>> ReadLedgerTextAsync(string connectionString, Guid jobId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT coalesce(rationale, '') || ' ' || coalesce(payload_ref, '')
            FROM incidentcompass.triage_ledger WHERE job_id = @job;
            """, connection);
        command.Parameters.AddWithValue("job", jobId);
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken)) values.Add(reader.GetString(0));
        return values;
    }

    private static TriageConfiguration CreateTriageConfiguration(
        string configHash,
        string endpointSentinel,
        string credentialSentinel) =>
        new(
            configHash,
            new Dictionary<string, TriageProviderSettings>
            {
                ["writer-provider"] = new("Mock", endpointSentinel, credentialSentinel)
            },
            new Dictionary<string, TriageRouteSettings>
            {
                ["safe-logical-route"] = new("Chat", "writer-provider", "writer-model", 0, 100, 8192)
            },
            new OrchestratorSettings("test", "safe-logical-route", [], new OrchestratorBudgetSettings(1, 10_000, 60, 1)),
            new Dictionary<string, TriageRoleSettings>(),
            new Dictionary<string, TriageToolSettings>(),
            [],
            new IngestionSettings("tenant-a", ["cost-test"]),
            new FaultGroupingSettings(15, 30, 1, new MassIssueSettings(5, "strong")),
            RedactionSettings.Default);

    private sealed record SeededJob(TriageJob Job, string ConfigHash);

    private sealed class StaticModelClient(string responseSentinel) : IAiModelClient
    {
        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new AiModelResponse(
                responseSentinel,
                "writer-model",
                SharedAdapter,
                new AiModelUsage(100, 200, 300),
                request.CorrelationId,
                []));
    }
}
