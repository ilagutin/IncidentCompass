using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Application.Memory;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using NpgsqlTypes;

namespace IncidentCompass.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class MemorySearchTests(PostgresRepositoryFixture postgres)
{
    private const string MemoryModel = "mock-memory-embedding-v1";

    [DockerAvailableFact]
    public async Task MemoryChunks_RejectEmbeddingDimensionMismatch()
    {
        var connectionString = await CreateSchemaAsync();
        await ClearMemoryAsync(connectionString);
        var itemId = await InsertMemoryItemAsync(connectionString, "local", "dimension-mismatch.md");

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertMemoryChunkAsync(
                connectionString,
                itemId,
                "local",
                "mock",
                MemoryModel,
                embeddingDimensions: 3,
                [1f, 0f],
                TestContext.Current.CancellationToken));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [DockerAvailableFact]
    public async Task SearchAsync_UsesExactTenantProviderModelDimensionFilters()
    {
        using var scope = await CreateScopeAsync();
        var itemId = await InsertMemoryItemAsync(scope.ConnectionString, "local", "exact-filter.md");
        await InsertMemoryChunkAsync(
            scope.ConnectionString,
            itemId,
            "local",
            "mock",
            MemoryModel,
            embeddingDimensions: 2,
            [1f, 0f],
            TestContext.Current.CancellationToken);
        await InsertFilterCandidateAsync(
            scope.ConnectionString, "other-tenant", "foreign-tenant.md", "mock", MemoryModel, 2, [1f, 0f]);
        await InsertFilterCandidateAsync(
            scope.ConnectionString, "local", "foreign-provider.md", "foreign-provider", MemoryModel, 2, [1f, 0f]);
        await InsertFilterCandidateAsync(
            scope.ConnectionString, "local", "foreign-model.md", "mock", "foreign-model", 2, [1f, 0f]);
        await InsertFilterCandidateAsync(
            scope.ConnectionString, "local", "foreign-dimension.md", "mock", MemoryModel, 3, [1f, 0f, 0f]);
        var crossWiredItemId = await InsertMemoryItemAsync(
            scope.ConnectionString,
            "other-tenant",
            "cross-wired-parent.md");
        await InsertMemoryChunkAsync(
            scope.ConnectionString,
            crossWiredItemId,
            "local",
            "mock",
            MemoryModel,
            2,
            [1f, 0f],
            TestContext.Current.CancellationToken);
        var inactiveItemId = await InsertFilterCandidateAsync(
            scope.ConnectionString, "local", "inactive-filter.md", "mock", MemoryModel, 2, [1f, 0f], isActive: false);

        using var serviceScope = scope.Factory.Services.CreateScope();
        var repository = serviceScope.ServiceProvider.GetRequiredService<IMemoryRepository>();

        var match = await repository.SearchAsync(
            new MemorySearchRequest("local", "mock", MemoryModel, 2, [1f, 0f], CandidateCount: 5, MinScore: 0.25),
            TestContext.Current.CancellationToken);
        var dimensionMismatch = await repository.SearchAsync(
            new MemorySearchRequest("local", "mock", MemoryModel, 4, [1f, 0f, 0f, 0f], CandidateCount: 5, MinScore: 0.25),
            TestContext.Current.CancellationToken);
        var modelMismatch = await repository.SearchAsync(
            new MemorySearchRequest("local", "mock", "other-model", 2, [1f, 0f], CandidateCount: 5, MinScore: 0.25),
            TestContext.Current.CancellationToken);
        var providerMismatch = await repository.SearchAsync(
            new MemorySearchRequest("local", "other-provider", MemoryModel, 2, [1f, 0f], CandidateCount: 5, MinScore: 0.25),
            TestContext.Current.CancellationToken);
        var tenantMismatch = await repository.SearchAsync(
            new MemorySearchRequest("other-tenant", "mock", MemoryModel, 2, [1f, 0f], CandidateCount: 5, MinScore: 0.25),
            TestContext.Current.CancellationToken);

        Assert.Single(match);
        Assert.Empty(dimensionMismatch);
        Assert.Empty(modelMismatch);
        Assert.Empty(providerMismatch);
        Assert.Single(tenantMismatch);
        Assert.Equal("foreign-tenant.md", tenantMismatch[0].Source);
        Assert.DoesNotContain(match, candidate => candidate.MemoryItemId == crossWiredItemId);
        Assert.DoesNotContain(match, candidate => candidate.MemoryItemId == inactiveItemId);
    }

    [DockerAvailableFact]
    public async Task SearchAsync_OrdersVectorScoreThenFixedChunkUuid()
    {
        using var scope = await CreateScopeAsync();
        var itemId = await InsertMemoryItemAsync(scope.ConnectionString, "local", "ordering.md");
        var lowerId = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var higherId = Guid.Parse("20000000-0000-0000-0000-000000000002");
        await InsertMemoryChunkAsync(
            scope.ConnectionString,
            itemId,
            "local",
            "mock",
            MemoryModel,
            2,
            [1f, 0f],
            TestContext.Current.CancellationToken,
            higherId,
            chunkPosition: 1);
        await InsertMemoryChunkAsync(
            scope.ConnectionString,
            itemId,
            "local",
            "mock",
            MemoryModel,
            2,
            [1f, 0f],
            TestContext.Current.CancellationToken,
            lowerId);

        using var serviceScope = scope.Factory.Services.CreateScope();
        var repository = serviceScope.ServiceProvider.GetRequiredService<IMemoryRepository>();
        var matches = await repository.SearchAsync(
            new MemorySearchRequest("local", "mock", MemoryModel, 2, [1f, 0f], CandidateCount: 20, MinScore: 0.25),
            TestContext.Current.CancellationToken);

        Assert.Equal([lowerId, higherId], matches.Select(static match => match.ChunkId));
    }

    [DockerAvailableFact]
    public async Task CandidateStrategy_PassesBaselineComparatorsAndMandatoryScenarios()
    {
        await using var harness = await BenchmarkHarness.CreateAsync(postgres);
        var run = await MemoryRetrievalBenchmarkRunner.MeasureAsync(harness.Production, harness.Corpus);
        var candidate = MemoryRetrievalMetrics.Evaluate(harness.Corpus, run.Results);
        var baseline = MemoryRetrievalBaselineRecord.Load(FindRepoRoot()).Deterministic;

        Assert.True(candidate.Metrics.ChunkMacroRecallAt5 >= baseline.Metrics.ChunkMacroRecallAt5);
        Assert.True(candidate.Metrics.ChunkMicroRecallAt5 >= baseline.Metrics.ChunkMicroRecallAt5);
        Assert.True(candidate.Metrics.ItemMacroRecallAt5 >= baseline.Metrics.ItemMacroRecallAt5);
        Assert.True(candidate.Metrics.ItemMicroRecallAt5 >= baseline.Metrics.ItemMicroRecallAt5);
        Assert.True(candidate.Metrics.MeanFirstRelevantChunkRank <= baseline.Metrics.MeanFirstRelevantChunkRank);
        Assert.True(candidate.Metrics.NoMatchPrecision >= baseline.Metrics.NoMatchPrecision);
        Assert.True(candidate.Metrics.NoMatchFalsePositiveCount <= baseline.Metrics.NoMatchFalsePositiveCount);

        var falseEmpty = Assert.Single(candidate.Queries,
            static query => query.QueryId == "false-empty-checkout-timeout");
        Assert.Contains(Guid.Parse("20000000-0000-0000-0000-000000000001"), falseEmpty.ReturnedChunkIds);
        Assert.Contains(Guid.Parse("20000000-0000-0000-0000-000000000002"), falseEmpty.ReturnedChunkIds);
        var currentService = Assert.Single(candidate.Queries,
            static query => query.QueryId == "current-inventory-mitigation");
        Assert.Equal(Guid.Parse("20000000-0000-0000-0000-000000000003"), currentService.ReturnedChunkIds[0]);

        TestContext.Current.TestOutputHelper?.WriteLine(
            "CANDIDATE_METRICS=" + JsonSerializer.Serialize(candidate.Metrics));
        TestContext.Current.TestOutputHelper?.WriteLine(
            "CANDIDATE_LATENCY=" + JsonSerializer.Serialize(run.Latency));
    }

    [DockerAvailableFact]
    public async Task ProcessClaimedAsync_KnownTimeoutDelegatesMemoryAndThreadsRetrievedArtifactIds()
    {
        using var scope = await CreateScopeAsync();
        await SeedCheckoutRunbookAsync(scope.Factory);
        var ingested = await PostIngestAsync(scope.Client, TesterEnvelope("payments-api", "TimeoutException", "Checkout timed out while calling inventory", "/checkout"));

        await RunClaimedJobAsync(scope, ingested.JobId!.Value, "worker-memory-known");

        var report = await ReadReportAsync(scope.ConnectionString, ingested.FaultId);
        var retrievedIds = await ReadArtifactIdsAsync(scope.ConnectionString, ingested.JobId.Value, "RetrievedItem");
        var memoryWorkerOutput = await ReadMemoryWorkerOutputAsync(scope.ConnectionString, ingested.JobId.Value);
        var ledgerRows = await ReadToolLedgerRowsAsync(scope.ConnectionString, ingested.JobId.Value);

        Assert.Equal("Completed", report.Status);
        Assert.Equal("KnownIncident", report.Classification);
        var retrievedId = Assert.Single(retrievedIds);
        Assert.True(memoryWorkerOutput.GetProperty("matched").GetBoolean());
        Assert.Equal(retrievedId.ToString(), memoryWorkerOutput.GetProperty("items")[0].GetProperty("artifactId").GetString());
        Assert.Contains(ledgerRows, row => row.EventType == "ToolProposed" && row.ToolName == "memory_search");
        Assert.Contains(ledgerRows, row => row.EventType == "PolicyDecision" && row.ToolName == "memory_search" && row.Decision == "Allowed");
        Assert.Contains(ledgerRows, row => row.EventType == "ToolResult" && row.ToolName == "memory_search" && row.ToolStatus == "Succeeded");
        Assert.Single(ledgerRows, row => row.EventType == "ToolProposed");
        Assert.Single(ledgerRows, row => row.EventType == "PolicyDecision");
        Assert.Single(ledgerRows, row => row.EventType == "ToolResult");
    }

    [DockerAvailableFact]
    public async Task ProcessClaimedAsync_UnknownErrorReturnsHonestMemoryNoMatch()
    {
        using var scope = await CreateScopeAsync();
        var ingested = await PostIngestAsync(scope.Client, TesterEnvelope("orders-api", "NullReferenceException", "NullReference while rendering order details", "/orders/{id}"));

        await RunClaimedJobAsync(scope, ingested.JobId!.Value, "worker-memory-empty");

        var report = await ReadReportAsync(scope.ConnectionString, ingested.FaultId);
        var retrievedIds = await ReadArtifactIdsAsync(scope.ConnectionString, ingested.JobId.Value, "RetrievedItem");
        var memoryWorkerOutput = await ReadMemoryWorkerOutputAsync(scope.ConnectionString, ingested.JobId.Value);

        Assert.Equal("InsufficientEvidence", report.Status);
        Assert.Equal("Unknown", report.Classification);
        Assert.Empty(retrievedIds);
        Assert.False(memoryWorkerOutput.GetProperty("matched").GetBoolean());
        Assert.Equal("no matches", memoryWorkerOutput.GetProperty("noMatchReason").GetString());
    }

    [DockerAvailableFact]
    public async Task ProcessClaimedAsync_RateCapDeniesRealMemorySearchPastCurrentAttemptCap()
    {
        var configPath = await CreateRateCapConfigurationAsync();
        using var scope = await CreateScopeAsync(configPath, services =>
        {
            services.RemoveAll<IAiModelClient>();
            services.AddScoped<IAiModelClient, RepeatMemorySearchModelClient>();
        });
        var ingested = await PostIngestAsync(scope.Client, TesterEnvelope("payments-api", "TimeoutException", "Checkout timed out while calling inventory", "/checkout"));

        await RunClaimedJobAsync(scope, ingested.JobId!.Value, "worker-memory-rate-cap", maxAttempts: 1);

        var decisions = await ReadToolLedgerRowsAsync(scope.ConnectionString, ingested.JobId.Value);
        Assert.Contains(decisions, row => row.EventType == "PolicyDecision" && row.ToolName == "memory_search" && row.Decision == "Allowed");
        Assert.Contains(decisions, row => row.EventType == "PolicyDecision" && row.ToolName == "memory_search" && row.Decision == "Denied" && row.DecisionReason!.StartsWith("rate_cap_exceeded:", StringComparison.Ordinal));
    }

    private async Task<TestScope> CreateScopeAsync(
        string? configPath = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var connectionString = await CreateSchemaAsync();
        await PostgresTriageJobTestIsolation.CompleteClaimableJobsAsync(connectionString);
        await ClearMemoryAsync(connectionString);

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:IncidentCompass", connectionString);
            builder.UseExplicitMockProviders();
            if (configPath is not null)
            {
                builder.UseSetting("IncidentCompass:ConfigSource:Path", configPath);
            }

            if (configureServices is not null)
            {
                builder.ConfigureTestServices(configureServices);
            }
        });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        return new TestScope(factory, client, connectionString);
    }

    private async Task<string> CreateSchemaAsync()
    {
        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        return connectionString;
    }

    private static async Task SeedCheckoutRunbookAsync(WebApplicationFactory<Program> factory)
    {
        using var serviceScope = factory.Services.CreateScope();
        var embeddingClient = serviceScope.ServiceProvider.GetRequiredService<IEmbeddingClient>();
        var repository = serviceScope.ServiceProvider.GetRequiredService<IMemoryRepository>();
        var configuration = await serviceScope.ServiceProvider
            .GetRequiredService<ITriageConfigurationRepository>()
            .GetCurrentAsync(TestContext.Current.CancellationToken);
        var embeddingRoute = configuration.Routes["memory-embed"];
        var content = await File.ReadAllTextAsync(Path.Combine(FindRepoRoot(), "samples", "runbooks", "checkout-timeout.md"), TestContext.Current.CancellationToken);
        var embedding = await embeddingClient.CreateEmbeddingAsync(
            new EmbeddingRequest(content, embeddingRoute.Model, "memory-test-seed"),
            TestContext.Current.CancellationToken);
        var item = new MemorySeedItem(
            Guid.NewGuid(),
            "local",
            "runbook",
            "samples/runbooks/checkout-timeout.md",
            "Checkout Timeout Runbook",
            content,
            Hash(content),
            Version: 1,
            ["checkout", "timeout"],
            ServiceName: "checkout",
            Component: null,
            ReleaseName: null);
        var chunk = new MemorySeedChunk(
            Guid.NewGuid(),
            Position: 0,
            content,
            Hash(content),
            embedding.Provider,
            embedding.Model,
            embedding.Vector.Count,
            embedding.Vector);

        await repository.ReconcileSeedCorpusAsync(
            new MemorySeedCorpus(
                "local",
                "test",
                Guid.NewGuid(),
                new HashSet<string>(StringComparer.Ordinal) { "samples" },
                [new MemorySeedEntry(item, [chunk])]),
            TestContext.Current.CancellationToken);
    }

    private static async Task RunClaimedJobAsync(
        TestScope scope,
        Guid expectedJobId,
        string workerId,
        int maxAttempts = 3)
    {
        using var serviceScope = scope.Factory.Services.CreateScope();
        var runner = serviceScope.ServiceProvider.GetRequiredService<ITriageJobRunner>();
        var claimed = await runner.ClaimNextAsync(workerId, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        Assert.NotNull(claimed);
        Assert.Equal(expectedJobId, claimed.Id);

        await runner.ProcessClaimedAsync(
            claimed,
            workerId,
            new TriageJobProcessingSettings(maxAttempts, RetryDelay: TimeSpan.FromSeconds(1)),
            TestContext.Current.CancellationToken);
    }

    private static async Task<IngestSignalResponseDto> PostIngestAsync(HttpClient client, TesterEnvelopeDto envelope)
    {
        var response = await client.PostAsJsonAsync("/api/v1/incidents", envelope, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<IngestSignalResponseDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.NotNull(body.JobId);
        return body;
    }

    private static TesterEnvelopeDto TesterEnvelope(string serviceName, string errorType, string errorMessage, string route)
    {
        return new TesterEnvelopeDto(
            "tester",
            serviceName + "-" + Guid.NewGuid().ToString("N"),
            "prod",
            DateTimeOffset.UtcNow,
            new TesterAttributesDto(errorType, errorMessage, route));
    }

    private static async Task<Guid> InsertMemoryItemAsync(string connectionString, string tenantId, string source)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var id = Guid.NewGuid();
        await using var command = new NpgsqlCommand("""
            INSERT INTO incidentcompass.memory_items (
                id, tenant_id, kind, source, title, content, content_hash, version, tags, created_at_utc)
            VALUES (
                @id, @tenant_id, 'runbook', @source, 'Exact filter test', 'checkout timeout inventory',
                @content_hash, 1, ARRAY['test']::text[], @created_at_utc);
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("source", source);
        command.Parameters.AddWithValue("content_hash", Hash(source));
        command.Parameters.AddWithValue("created_at_utc", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return id;
    }

    private static async Task InsertMemoryChunkAsync(
        string connectionString,
        Guid itemId,
        string tenantId,
        string provider,
        string model,
        int embeddingDimensions,
        float[] values,
        CancellationToken cancellationToken,
        Guid? chunkId = null,
        int chunkPosition = 0)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO incidentcompass.memory_chunks (
                id, memory_item_id, tenant_id, chunk_position, text, text_hash,
                embedding_provider, embedding_model, embedding_dimensions,
                embedding_values, embedding_vector, created_at_utc)
            VALUES (
                @id, @memory_item_id, @tenant_id, @chunk_position, 'checkout timeout inventory', @text_hash,
                @embedding_provider, @embedding_model, @embedding_dimensions,
                @embedding_values, @embedding_vector::vector, @created_at_utc);
            """, connection);
        command.Parameters.AddWithValue("id", chunkId ?? Guid.NewGuid());
        command.Parameters.AddWithValue("memory_item_id", itemId);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("chunk_position", chunkPosition);
        command.Parameters.AddWithValue("text_hash", Hash(model + embeddingDimensions));
        command.Parameters.AddWithValue("embedding_provider", provider);
        command.Parameters.AddWithValue("embedding_model", model);
        command.Parameters.AddWithValue("embedding_dimensions", embeddingDimensions);
        command.Parameters.Add("embedding_values", NpgsqlDbType.Array | NpgsqlDbType.Real).Value = values;
        command.Parameters.AddWithValue("embedding_vector", "[" + string.Join(",", values.Select(static value => value.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]");
        command.Parameters.AddWithValue("created_at_utc", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SetMemoryItemActiveAsync(
        string connectionString,
        Guid itemId,
        bool isActive)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "UPDATE incidentcompass.memory_items SET is_active = @is_active WHERE id = @id;",
            connection);
        command.Parameters.AddWithValue("id", itemId);
        command.Parameters.AddWithValue("is_active", isActive);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<Guid> InsertFilterCandidateAsync(
        string connectionString,
        string tenantId,
        string source,
        string provider,
        string model,
        int embeddingDimensions,
        float[] values,
        bool isActive = true)
    {
        var itemId = await InsertMemoryItemAsync(connectionString, tenantId, source);
        await InsertMemoryChunkAsync(
            connectionString,
            itemId,
            tenantId,
            provider,
            model,
            embeddingDimensions,
            values,
            TestContext.Current.CancellationToken);
        if (!isActive)
        {
            await SetMemoryItemActiveAsync(connectionString, itemId, false);
        }

        return itemId;
    }

    private static async Task ClearMemoryAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("DELETE FROM incidentcompass.memory_items;", connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<ReportRow> ReadReportAsync(string connectionString, Guid faultId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT status, classification FROM incidentcompass.triage_reports WHERE fault_id = @fault_id;",
            connection);
        command.Parameters.AddWithValue("fault_id", faultId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new ReportRow(reader.GetString(0), reader.GetString(1));
    }

    private static async Task<IReadOnlyList<Guid>> ReadArtifactIdsAsync(string connectionString, Guid jobId, string kind)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT id FROM incidentcompass.triage_artifacts WHERE job_id = @job_id AND kind = @kind ORDER BY created_at_utc, id;",
            connection);
        command.Parameters.AddWithValue("job_id", jobId);
        command.Parameters.AddWithValue("kind", kind);
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private static async Task<JsonElement> ReadMemoryWorkerOutputAsync(string connectionString, Guid jobId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT redacted_payload::text
            FROM incidentcompass.triage_artifacts
            WHERE job_id = @job_id AND kind = 'WorkerOutput' AND domain_ref = 'worker:memory';
            """, connection);
        command.Parameters.AddWithValue("job_id", jobId);
        var json = (string)(await command.ExecuteScalarAsync())!;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task<IReadOnlyList<ToolLedgerRow>> ReadToolLedgerRowsAsync(string connectionString, Guid jobId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT event_type, tool_name, decision, decision_reason, tool_status
            FROM incidentcompass.triage_ledger
            WHERE job_id = @job_id AND tool_name = 'memory_search'
            ORDER BY id;
            """, connection);
        command.Parameters.AddWithValue("job_id", jobId);
        var rows = new List<ToolLedgerRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new ToolLedgerRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return rows;
    }

    private static async Task<string> CreateRateCapConfigurationAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "incidentcompass-memory-rate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "instructions"));
        Directory.CreateDirectory(Path.Combine(directory, "schemas"));
        await File.WriteAllTextAsync(Path.Combine(directory, "instructions", "orchestrator.md"), "Delegate memory.");
        await File.WriteAllTextAsync(Path.Combine(directory, "instructions", "memory.md"), "Use memory_search.");
        await File.WriteAllTextAsync(Path.Combine(directory, "schemas", "memory.json"), await File.ReadAllTextAsync(Path.Combine(FindRepoRoot(), "config", "schemas", "memory.json")));
        var config = new JsonObject
        {
            ["Providers"] = new JsonObject { ["local-oai"] = new JsonObject { ["Kind"] = "Mock" } },
            ["Routes"] = new JsonObject
            {
                ["analysis-chat"] = ChatRoute(),
                ["report-chat"] = ChatRoute(),
                ["memory-embed"] = new JsonObject { ["Kind"] = "Embedding", ["ProviderId"] = "local-oai", ["Model"] = MemoryModel }
            },
            ["Orchestrator"] = new JsonObject
            {
                ["Instructions"] = "ref:instructions/orchestrator.md",
                ["RouteId"] = "report-chat",
                ["Tools"] = new JsonArray("delegate", "publish_report"),
                ["Budget"] = new JsonObject { ["MaxWorkers"] = 2, ["MaxTokens"] = 100000, ["MaxWallClockSeconds"] = 120, ["MaxReprompts"] = 1 }
            },
            ["Roles"] = new JsonObject
            {
                ["memory"] = new JsonObject { ["RouteId"] = "analysis-chat", ["Instructions"] = "ref:instructions/memory.md", ["Tools"] = new JsonArray("memory_search"), ["OutputSchema"] = "ref:schemas/memory.json" }
            },
            ["Tools"] = new JsonObject { ["memory_search"] = new JsonObject { ["Kind"] = "internal", ["EmbeddingRouteId"] = "memory-embed", ["TopK"] = 5, ["MinScore"] = 0.25 } },
            ["Rules"] = new JsonArray(new JsonObject { ["Type"] = "rate_cap", ["Tool"] = "*", ["Scope"] = "attempt", ["Max"] = 1 }),
            ["Ingestion"] = new JsonObject { ["DefaultTenant"] = "local", ["AllowedSources"] = new JsonArray("otel", "user", "tester", "manual") },
            ["FaultGrouping"] = new JsonObject
            {
                ["LookbackMinutes"] = 15,
                ["SilenceWindowMinutes"] = 30,
                ["FingerprintVersion"] = 1,
                ["MassIssue"] = new JsonObject { ["MinNeighborCount"] = 5, ["MinFingerprintStrength"] = "strong" }
            }
        };
        var path = Path.Combine(directory, "incidentcompass.config.json");
        await File.WriteAllTextAsync(path, config.ToJsonString());
        return path;
    }

    private static JsonObject ChatRoute()
    {
        return new JsonObject
        {
            ["Kind"] = "Chat",
            ["ProviderId"] = "local-oai",
            ["Model"] = "memory-rate-model",
            ["Temperature"] = 0.0,
            ["MaxOutputTokens"] = 1000,
            ["ContextWindowTokens"] = 8192
        };
    }

    private static string Hash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "IncidentCompass.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }

    private sealed class RepeatMemorySearchModelClient : IAiModelClient
    {
        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            var toolNames = request.Tools?.Select(static tool => tool.Name).ToHashSet(StringComparer.Ordinal) ?? [];
            if (toolNames.SetEquals(["delegate", "publish_report"]))
            {
                return Task.FromResult(Response(request, "delegate memory", [ToolCall("delegate-memory", "delegate", "{\"role\":\"memory\",\"task\":\"repeat memory_search\"}")]));
            }

            var toolResultCount = request.Messages.Count(static message => message.Role == AiMessageRole.Tool);
            return Task.FromResult(Response(request, "search", [ToolCall("memory-search-" + toolResultCount, "memory_search", "{\"query\":\"checkout timeout inventory\"}")]));
        }

        private static AiModelResponse Response(AiModelRequest request, string content, IReadOnlyList<AiToolCall> toolCalls)
        {
            return new AiModelResponse(content, request.Model, "repeat-memory-search-test", new AiModelUsage(10, 5, 15), request.CorrelationId, toolCalls);
        }

        private static AiToolCall ToolCall(string id, string name, string argumentsJson)
        {
            using var arguments = JsonDocument.Parse(argumentsJson);
            return new AiToolCall(id, name, "v1", arguments.RootElement.Clone());
        }
    }

    private sealed record TestScope(WebApplicationFactory<Program> Factory, HttpClient Client, string ConnectionString) : IDisposable
    {
        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
        }
    }

    private sealed record TesterAttributesDto(string ErrorType, string ErrorMessage, string HttpRoute);

    private sealed record TesterEnvelopeDto(
        string SourceKind,
        string ServiceName,
        string Environment,
        DateTimeOffset ObservedAtUtc,
        TesterAttributesDto Attributes);

    private sealed record IngestSignalResponseDto(
        Guid SignalId,
        Guid FaultId,
        bool IsNewFault,
        bool IsNewJob,
        bool IsSuppressed,
        Guid? JobId,
        string? ConfigHash);

    private sealed record ReportRow(string Status, string Classification);

    private sealed record ToolLedgerRow(
        string EventType,
        string? ToolName,
        string? Decision,
        string? DecisionReason,
        string? ToolStatus);
}
