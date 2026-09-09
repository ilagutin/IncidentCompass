using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Domain.Incidents;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class BoundedRepromptAndBudgetTests(PostgresRepositoryFixture postgres)
{
    private const string WorkerOutputSentinel = "INJECTED_MODEL_OUTPUT_MUST_NOT_BE_DURABLE";

    [DockerAvailableFact]
    public async Task ProcessClaimedAsync_InvalidWorkerOutputRepromptsAtMostConfiguredLimitThenFailsClosed()
    {
        using var scope = await CreateScopeAsync(RepromptScenario.InvalidWorkerOutput, maxReprompts: 2, maxTokens: 100000, contextWindowTokens: 8192);
        var ingested = await IngestOneAsync(scope);
        await ProcessNextAsync(scope, "worker-invalid-output", maxAttempts: 3, retryDelay: TimeSpan.Zero);

        var job = await ReadJobAsync(scope.ConnectionString, ingested.JobId!.Value);
        var attempt = await ScalarAsync<int>(
            scope.ConnectionString,
            "SELECT attempt FROM incidentcompass.triage_jobs WHERE id = @job_id;",
            ("job_id", ingested.JobId.Value));
        var workerModelCalls = await ScalarAsync<long>(
            scope.ConnectionString,
            "SELECT COUNT(*) FROM incidentcompass.triage_ledger WHERE job_id = @job_id AND event_type = 'ModelCall' AND role = 'analysis';",
            ("job_id", ingested.JobId.Value));

        var workerDeltas = await ScalarAsync<long>(
            scope.ConnectionString,
            "SELECT COALESCE(SUM(workers_delta), 0) FROM incidentcompass.triage_ledger WHERE job_id = @job_id AND event_type = 'BudgetEvent';",
            ("job_id", ingested.JobId.Value));
        var leakedLedgerFields = await ScalarAsync<long>(
            scope.ConnectionString,
            """
            SELECT COUNT(*)
            FROM incidentcompass.triage_ledger
            WHERE job_id = @job_id
              AND concat_ws(' ', role, tool_name, rationale, decision_reason, payload_ref) LIKE @sentinel_pattern;
            """,
            ("job_id", ingested.JobId.Value),
            ("sentinel_pattern", "%" + WorkerOutputSentinel + "%"));
        var repromptEvents = await ReadRepromptBudgetEventsAsync(
            scope.ConnectionString,
            ingested.JobId.Value,
            "worker_output_reprompt:");

        Assert.Equal("DeadLettered", job.Status);
        Assert.Equal(3, workerModelCalls);
        Assert.Equal(1, workerDeltas);
        Assert.Equal(2, repromptEvents.Count);
        foreach (var repromptEvent in repromptEvents)
        {
            AssertRepromptEvent(
                repromptEvent,
                "analysis",
                "worker_output_reprompt:",
                "not valid JSON");
            Assert.DoesNotContain(WorkerOutputSentinel, repromptEvent.Rationale, StringComparison.Ordinal);
        }

        Assert.Equal(0, leakedLedgerFields);
        Assert.Equal("worker_output_invalid", job.LastErrorCode);
        Assert.Equal(
            "worker_output_invalid: worker output remained invalid after bounded reprompts.",
            job.LastErrorMessage);
        Assert.DoesNotContain(WorkerOutputSentinel, job.LastErrorMessage, StringComparison.Ordinal);
        Assert.Equal(1, attempt);

        var reclaimed = await TryClaimNextAsync(scope, "worker-invalid-output-reclaim");
        Assert.True(
            reclaimed?.Id != ingested.JobId.Value,
            "A dead-lettered invalid-worker-output job must not be claimable for another attempt.");
    }

    [DockerAvailableFact]
    public async Task ProcessClaimedAsync_NoToolOrchestratorTurnRepromptsAtMostConfiguredLimitThenFailsClosed()
    {
        using var scope = await CreateScopeAsync(RepromptScenario.NoOrchestratorTool, maxReprompts: 1, maxTokens: 100000, contextWindowTokens: 8192);
        var ingested = await IngestOneAsync(scope);
        // Attempts are deliberately left on the job, so the dead-letter below is attributable to the
        // spent reprompt allowance rather than to the attempt guard.
        await ProcessNextAsync(scope, "worker-reprompt", maxAttempts: 3, retryDelay: TimeSpan.Zero);

        var job = await ReadJobAsync(scope.ConnectionString, ingested.JobId!.Value);
        var attempt = await ScalarAsync<int>(
            scope.ConnectionString,
            "SELECT attempt FROM incidentcompass.triage_jobs WHERE id = @job_id;",
            ("job_id", ingested.JobId.Value));
        var orchestratorModelCalls = await ScalarAsync<long>(
            scope.ConnectionString,
            "SELECT COUNT(*) FROM incidentcompass.triage_ledger WHERE job_id = @job_id AND event_type = 'ModelCall' AND role IS NULL;",
            ("job_id", ingested.JobId.Value));
        var repromptEvent = Assert.Single(await ReadRepromptBudgetEventsAsync(
            scope.ConnectionString,
            ingested.JobId.Value,
            "orchestrator_reprompt:"));

        Assert.Equal("DeadLettered", job.Status);
        Assert.Equal(2, orchestratorModelCalls);
        AssertRepromptEvent(repromptEvent, "orchestrator", "orchestrator_reprompt:", "no_tool_call");
        Assert.Equal("triage_budget_orchestrator_reprompt_limit_reached", job.LastErrorCode);
        Assert.Equal(
            "triage_budget_orchestrator_reprompt_limit_reached: TriageBudgetExhaustedException.",
            job.LastErrorMessage);
        Assert.Equal(1, attempt);
    }

    [DockerAvailableFact]
    public async Task ProcessClaimedAsync_NonObjectDelegateArgumentsRepromptAndRecovers()
    {
        using var scope = await CreateScopeAsync(RepromptScenario.NonObjectDelegateArgumentsThenValid, maxReprompts: 1, maxTokens: 100000, contextWindowTokens: 8192);
        var ingested = await RunOneAsync(scope);

        var job = await ReadJobAsync(scope.ConnectionString, ingested.JobId!.Value);
        var orchestratorModelCalls = await ScalarAsync<long>(
            scope.ConnectionString,
            "SELECT COUNT(*) FROM incidentcompass.triage_ledger WHERE job_id = @job_id AND event_type = 'ModelCall' AND role IS NULL;",
            ("job_id", ingested.JobId.Value));
        var repromptEvent = Assert.Single(await ReadRepromptBudgetEventsAsync(
            scope.ConnectionString,
            ingested.JobId.Value,
            "orchestrator_reprompt:"));

        Assert.Equal("Succeeded", job.Status);
        Assert.Equal(3, orchestratorModelCalls);
        AssertRepromptEvent(repromptEvent, "orchestrator", "orchestrator_reprompt:", "delegate_validation_failed");
    }

    [DockerAvailableFact]
    public async Task ProcessClaimedAsync_NonObjectPublishArgumentsRepromptAndRecovers()
    {
        using var scope = await CreateScopeAsync(RepromptScenario.NonObjectPublishArgumentsThenValid, maxReprompts: 1, maxTokens: 100000, contextWindowTokens: 8192);
        var ingested = await RunOneAsync(scope);

        var job = await ReadJobAsync(scope.ConnectionString, ingested.JobId!.Value);
        var orchestratorModelCalls = await ScalarAsync<long>(
            scope.ConnectionString,
            "SELECT COUNT(*) FROM incidentcompass.triage_ledger WHERE job_id = @job_id AND event_type = 'ModelCall' AND role IS NULL;",
            ("job_id", ingested.JobId.Value));
        var repromptEvent = Assert.Single(await ReadRepromptBudgetEventsAsync(
            scope.ConnectionString,
            ingested.JobId.Value,
            "orchestrator_reprompt:"));

        Assert.Equal("Succeeded", job.Status);
        Assert.Equal(3, orchestratorModelCalls);
        AssertRepromptEvent(repromptEvent, "orchestrator", "orchestrator_reprompt:", "publish_report_validation_failed");
    }

    [DockerAvailableFact]
    public async Task ProcessClaimedAsync_UnknownOrchestratorToolConsumesRepromptBudgetThenFailsClosed()
    {
        using var scope = await CreateScopeAsync(RepromptScenario.UnknownOrchestratorTool, maxReprompts: 1, maxTokens: 100000, contextWindowTokens: 8192);
        var ingested = await RunOneAsync(scope);

        var job = await ReadJobAsync(scope.ConnectionString, ingested.JobId!.Value);
        var orchestratorModelCalls = await ScalarAsync<long>(
            scope.ConnectionString,
            "SELECT COUNT(*) FROM incidentcompass.triage_ledger WHERE job_id = @job_id AND event_type = 'ModelCall' AND role IS NULL;",
            ("job_id", ingested.JobId.Value));
        var chargedTokens = await ScalarAsync<long>(
            scope.ConnectionString,
            "SELECT COALESCE(SUM(tokens_delta), 0) FROM incidentcompass.triage_ledger WHERE job_id = @job_id AND event_type = 'BudgetEvent';",
            ("job_id", ingested.JobId.Value));
        var repromptEvent = Assert.Single(await ReadRepromptBudgetEventsAsync(
            scope.ConnectionString,
            ingested.JobId.Value,
            "orchestrator_reprompt:"));

        Assert.Equal("DeadLettered", job.Status);
        Assert.Equal(2, orchestratorModelCalls);
        Assert.Equal(30, chargedTokens);
        AssertRepromptEvent(repromptEvent, "orchestrator", "orchestrator_reprompt:", "unknown_tool");
        Assert.Equal("triage_budget_orchestrator_reprompt_limit_reached", job.LastErrorCode);
        Assert.Equal(
            "triage_budget_orchestrator_reprompt_limit_reached: TriageBudgetExhaustedException.",
            job.LastErrorMessage);
    }

    [DockerAvailableFact]
    public async Task ProcessClaimedAsync_MaxWorkersReachedFailsClosedWithBudgetEvent()
    {
        using var scope = await CreateScopeAsync(RepromptScenario.MaxWorkersExceeded, maxReprompts: 1, maxTokens: 100000, contextWindowTokens: 8192, maxWorkers: 1);
        var ingested = await RunOneAsync(scope);

        var job = await ReadJobAsync(scope.ConnectionString, ingested.JobId!.Value);
        var budgetEvents = await ReadBudgetRationalesAsync(scope.ConnectionString, ingested.JobId.Value);
        var workerDeltas = await ScalarAsync<long>(
            scope.ConnectionString,
            "SELECT COALESCE(SUM(workers_delta), 0) FROM incidentcompass.triage_ledger WHERE job_id = @job_id AND event_type = 'BudgetEvent';",
            ("job_id", ingested.JobId.Value));

        Assert.Equal("triage_budget_max_workers_reached", job.LastErrorCode);
        Assert.Equal("DeadLettered", job.Status);
        Assert.Equal(1, workerDeltas);
        Assert.Contains(budgetEvents, value => value.Contains("max_workers_reached", StringComparison.Ordinal));
    }

    [DockerAvailableFact]
    public async Task ProcessClaimedAsync_MissingConfigSnapshotRetriesThenDeadLetters()
    {
        using var scope = await CreateScopeAsync(RepromptScenario.Valid, maxReprompts: 1, maxTokens: 100000, contextWindowTokens: 8192);
        var ingested = await IngestOneAsync(scope);
        Assert.NotNull(ingested.JobId);

        await ProcessNextWithMissingConfigAsync(scope, "worker-config-missing", maxAttempts: 2, retryDelay: TimeSpan.Zero);
        var retry = await ReadJobAsync(scope.ConnectionString, ingested.JobId.Value);
        Assert.Equal("RetryPending", retry.Status);
        Assert.Equal("config_snapshot_unavailable", retry.LastErrorCode);

        await ProcessNextWithMissingConfigAsync(scope, "worker-config-missing", maxAttempts: 2, retryDelay: TimeSpan.Zero);
        var deadLettered = await ReadJobAsync(scope.ConnectionString, ingested.JobId.Value);
        Assert.Equal("DeadLettered", deadLettered.Status);
        Assert.Equal("config_snapshot_unavailable", deadLettered.LastErrorCode);
    }
    [DockerAvailableFact]
    public async Task ProcessClaimedAsync_TokenBudgetStopsBeforeNextCallAfterOneCallOvershoot()
    {
        using var scope = await CreateScopeAsync(RepromptScenario.NoOrchestratorTool, maxReprompts: 2, maxTokens: 10, contextWindowTokens: 8192);
        var ingested = await RunOneAsync(scope);

        var budgetEvents = await ReadBudgetRationalesAsync(scope.ConnectionString, ingested.JobId!.Value);
        var tokens = await ScalarAsync<long>(
            scope.ConnectionString,
            "SELECT COALESCE(SUM(tokens_delta), 0) FROM incidentcompass.triage_ledger WHERE job_id = @job_id AND event_type = 'BudgetEvent';",
            ("job_id", ingested.JobId.Value));

        Assert.True(tokens > 0);
        Assert.Contains(budgetEvents, value => value.Contains("max_tokens_overshot_after_call", StringComparison.Ordinal));
        Assert.Contains(budgetEvents, value => value.Contains("max_tokens_reached_before_call", StringComparison.Ordinal));
    }

    /// <summary>
    /// Budget exhaustion is permanent for the job: a replay would spend the same tokens and stop at
    /// the same guard. The over-budget attempt must therefore dead-letter under its own error code
    /// after exactly one attempt, even when the retry budget still has attempts left.
    /// </summary>
    [DockerAvailableFact]
    public async Task ProcessClaimedAsync_TokenBudgetExhaustionDeadLettersAfterExactlyOneAttempt()
    {
        using var scope = await CreateScopeAsync(RepromptScenario.NoOrchestratorTool, maxReprompts: 2, maxTokens: 10, contextWindowTokens: 8192);
        var ingested = await IngestOneAsync(scope);
        Assert.NotNull(ingested.JobId);

        await ProcessNextAsync(scope, "worker-budget-exhaustion", maxAttempts: 3, retryDelay: TimeSpan.Zero);

        var job = await ReadJobAsync(scope.ConnectionString, ingested.JobId.Value);
        var attempt = await ScalarAsync<int>(
            scope.ConnectionString,
            "SELECT attempt FROM incidentcompass.triage_jobs WHERE id = @job_id;",
            ("job_id", ingested.JobId.Value));

        Assert.Equal("DeadLettered", job.Status);
        Assert.Equal("triage_budget_max_tokens_reached", job.LastErrorCode);
        // last_error_message is a bounded classification built from the same code as
        // last_error_code, not the raw TriageBudgetExhaustedException text, so the row is
        // self-explanatory when read directly from the database without a raw provider/exception
        // string ever being persisted.
        Assert.Equal("triage_budget_max_tokens_reached: TriageBudgetExhaustedException.", job.LastErrorMessage);
        Assert.Equal(1, attempt);

        var reclaimed = await TryClaimNextAsync(scope, "worker-budget-exhaustion");
        Assert.True(
            reclaimed?.Id != ingested.JobId.Value,
            "A dead-lettered over-budget job must not be claimable for a second attempt.");
    }

    [DockerAvailableFact]
    public async Task ProcessClaimedAsync_ContextWindowGuardDeniesOversizedPromptWithBudgetEvent()
    {
        using var scope = await CreateScopeAsync(RepromptScenario.Valid, maxReprompts: 1, maxTokens: 100000, contextWindowTokens: 2);
        var ingested = await RunOneAsync(scope);

        var job = await ReadJobAsync(scope.ConnectionString, ingested.JobId!.Value);
        var budgetEvents = await ReadBudgetRationalesAsync(scope.ConnectionString, ingested.JobId.Value);

        Assert.Equal("DeadLettered", job.Status);
        Assert.Contains(budgetEvents, value => value.Contains("context_window_exceeded", StringComparison.Ordinal));
    }

    private async Task<TestScope> CreateScopeAsync(
        RepromptScenario scenario,
        int maxReprompts,
        int maxTokens,
        int contextWindowTokens,
        int maxWorkers = 2)
    {
        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        await PostgresTriageJobTestIsolation.CompleteClaimableJobsAsync(connectionString);
        var configPath = await CreateConfigurationAsync(maxReprompts, maxTokens, contextWindowTokens, maxWorkers);

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:IncidentCompass", connectionString);
            builder.UseExplicitMockProviders();
            builder.UseSetting("IncidentCompass:ConfigSource:Path", configPath);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAiModelClient>();
                services.AddScoped<IAiModelClient>(_ => new RepromptModelClient(scenario));
            });
        });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        return new TestScope(factory, client, connectionString);
    }

    private static async Task<IngestSignalResponseDto> RunOneAsync(TestScope scope)
    {
        var ingested = await IngestOneAsync(scope);
        await ProcessNextAsync(scope, "worker-reprompt", maxAttempts: 1, retryDelay: TimeSpan.FromSeconds(1));
        return ingested;
    }

    private static async Task<IngestSignalResponseDto> IngestOneAsync(TestScope scope)
    {
        var unique = Guid.NewGuid().ToString("N");
        var response = await scope.Client.PostAsJsonAsync(
            "/api/v1/incidents",
            new TesterEnvelopeDto(
                "tester",
                "reprompt-budget-svc-" + unique,
                "prod",
                DateTimeOffset.UtcNow,
                new TesterAttributesDto("TimeoutException", "Reprompt probe timed out " + unique, "/reprompt")),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var ingested = await response.Content.ReadFromJsonAsync<IngestSignalResponseDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(ingested);
        Assert.NotNull(ingested.JobId);
        return ingested;
    }

    private static async Task ProcessNextAsync(
        TestScope scope,
        string workerId,
        int maxAttempts,
        TimeSpan retryDelay)
    {
        using var serviceScope = scope.Factory.Services.CreateScope();
        var runner = serviceScope.ServiceProvider.GetRequiredService<ITriageJobRunner>();
        var claimed = await runner.ClaimNextAsync(workerId, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        Assert.NotNull(claimed);
        await runner.ProcessClaimedAsync(
            claimed,
            workerId,
            new TriageJobProcessingSettings(maxAttempts, retryDelay),
            TestContext.Current.CancellationToken);
    }

    private static async Task<TriageJob?> TryClaimNextAsync(TestScope scope, string workerId)
    {
        using var serviceScope = scope.Factory.Services.CreateScope();
        var runner = serviceScope.ServiceProvider.GetRequiredService<ITriageJobRunner>();
        return await runner.ClaimNextAsync(workerId, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
    }

    private static async Task ProcessNextWithMissingConfigAsync(
        TestScope scope,
        string workerId,
        int maxAttempts,
        TimeSpan retryDelay)
    {
        using var serviceScope = scope.Factory.Services.CreateScope();
        var runtimeRepository = serviceScope.ServiceProvider.GetRequiredService<ITriageJobRuntimeRepository>();
        var processor = serviceScope.ServiceProvider.GetRequiredService<IClaimedTriageJobProcessor>();
        var timeProvider = serviceScope.ServiceProvider.GetRequiredService<TimeProvider>();
        var runner = new TriageJobRunner(
            runtimeRepository,
            new MissingSnapshotConfigurationRepository(),
            processor,
            timeProvider);
        var claimed = await runner.ClaimNextAsync(workerId, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        Assert.NotNull(claimed);
        await runner.ProcessClaimedAsync(
            claimed,
            workerId,
            new TriageJobProcessingSettings(maxAttempts, retryDelay),
            TestContext.Current.CancellationToken);
    }
    private static async Task<string> CreateConfigurationAsync(int maxReprompts, int maxTokens, int contextWindowTokens, int maxWorkers)
    {
        var directory = Path.Combine(Path.GetTempPath(), "incidentcompass-reprompt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "instructions"));
        Directory.CreateDirectory(Path.Combine(directory, "schemas"));
        await File.WriteAllTextAsync(Path.Combine(directory, "instructions", "orchestrator.md"), "Delegate analysis then publish a report.");
        await File.WriteAllTextAsync(Path.Combine(directory, "instructions", "analysis.md"), "Return JSON only. keyFacts must be an array of strings.");
        await File.WriteAllTextAsync(Path.Combine(directory, "schemas", "analysis.json"), AnalysisSchema());

        var config = new JsonObject
        {
            ["Providers"] = new JsonObject { ["local-oai"] = new JsonObject { ["Kind"] = "Mock" } },
            ["Routes"] = new JsonObject
            {
                ["analysis-chat"] = ChatRoute(contextWindowTokens),
                ["report-chat"] = ChatRoute(contextWindowTokens)
            },
            ["Orchestrator"] = new JsonObject
            {
                ["Instructions"] = "ref:instructions/orchestrator.md",
                ["RouteId"] = "report-chat",
                ["Tools"] = new JsonArray("delegate", "publish_report"),
                ["Budget"] = new JsonObject
                {
                    ["MaxWorkers"] = maxWorkers,
                    ["MaxTokens"] = maxTokens,
                    ["MaxWallClockSeconds"] = 120,
                    ["MaxReprompts"] = maxReprompts
                }
            },
            ["Roles"] = new JsonObject
            {
                ["analysis"] = new JsonObject
                {
                    ["RouteId"] = "analysis-chat",
                    ["Instructions"] = "ref:instructions/analysis.md",
                    ["Tools"] = new JsonArray(),
                    ["OutputSchema"] = "ref:schemas/analysis.json"
                }
            },
            ["Tools"] = new JsonObject(),
            ["Rules"] = new JsonArray(),
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

    private static JsonObject ChatRoute(int contextWindowTokens)
    {
        return new JsonObject
        {
            ["Kind"] = "Chat",
            ["ProviderId"] = "local-oai",
            ["Model"] = "reprompt-model",
            ["Temperature"] = 0.0,
            ["MaxOutputTokens"] = 1000,
            ["ContextWindowTokens"] = contextWindowTokens
        };
    }

    private static string AnalysisSchema()
    {
        return """
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "keyFacts": { "type": "array", "items": { "type": "string" } },
                "candidateClassification": { "type": "string", "enum": ["KnownIncident", "LikelyRegression", "SimpleKnownError", "Unknown", "Noise"] },
                "needsDeeperContext": { "type": "boolean" },
                "rationale": { "type": "string" }
              },
              "required": ["keyFacts", "candidateClassification", "needsDeeperContext"]
            }
            """;
    }

    private static async Task<JobRow> ReadJobAsync(string connectionString, Guid jobId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT status, last_error_code, last_error_message FROM incidentcompass.triage_jobs WHERE id = @job_id;", connection);
        command.Parameters.AddWithValue("job_id", jobId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new JobRow(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? string.Empty : reader.GetString(2));
    }

    private static async Task<IReadOnlyList<string>> ReadBudgetRationalesAsync(string connectionString, Guid jobId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT rationale FROM incidentcompass.triage_ledger WHERE job_id = @job_id AND event_type = 'BudgetEvent' ORDER BY id;", connection);
        command.Parameters.AddWithValue("job_id", jobId);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<RepromptBudgetEvent>> ReadRepromptBudgetEventsAsync(
        string connectionString,
        Guid jobId,
        string rationalePrefix)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT role, rationale, tokens_delta, workers_delta
            FROM incidentcompass.triage_ledger
            WHERE job_id = @job_id
              AND event_type = 'BudgetEvent'
              AND rationale LIKE @rationale_pattern
            ORDER BY id;
            """, connection);
        command.Parameters.AddWithValue("job_id", jobId);
        command.Parameters.AddWithValue("rationale_pattern", rationalePrefix + "%");
        var rows = new List<RepromptBudgetEvent>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new RepromptBudgetEvent(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3)));
        }

        return rows;
    }

    private static void AssertRepromptEvent(
        RepromptBudgetEvent repromptEvent,
        string expectedRole,
        string expectedPrefix,
        string expectedDiagnostic)
    {
        Assert.Equal(expectedRole, repromptEvent.Role);
        Assert.StartsWith(expectedPrefix, repromptEvent.Rationale, StringComparison.Ordinal);
        Assert.Contains(expectedDiagnostic, repromptEvent.Rationale, StringComparison.Ordinal);
        Assert.InRange(repromptEvent.Rationale.Length, 1, 1000);
        Assert.Null(repromptEvent.TokensDelta);
        Assert.Null(repromptEvent.WorkersDelta);
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync())!;
    }

    private sealed class RepromptModelClient(RepromptScenario scenario) : IAiModelClient
    {
        private static readonly string[] ValidWorkerKeyFacts = ["Valid worker output."];

        private int orchestratorCalls;

        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            if (IsOrchestrator(request))
            {
                return Task.FromResult(OrchestratorResponse(request));
            }

            return Task.FromResult(WorkerResponse(request));
        }

        private AiModelResponse OrchestratorResponse(AiModelRequest request)
        {
            orchestratorCalls++;
            if (scenario == RepromptScenario.NoOrchestratorTool)
            {
                EnsureValidationErrorAfterFirstTurn(request, "No-tool");
                return Response(request, "I will answer without a tool.", []);
            }

            if (scenario == RepromptScenario.UnknownOrchestratorTool)
            {
                EnsureValidationErrorAfterFirstTurn(request, "Unknown-tool");
                return Response(request, "unknown", [ToolCall("unknown-" + orchestratorCalls, "unknown_tool", "{}")]);
            }

            if (scenario == RepromptScenario.NonObjectDelegateArgumentsThenValid)
            {
                if (orchestratorCalls == 1)
                {
                    return Response(request, "delegate malformed", [StringArgumentToolCall("delegate-malformed", "delegate", "{\"role\"")]);
                }

                if (orchestratorCalls == 2)
                {
                    EnsureValidationError(request, "Malformed delegate reprompt did not include validation error.");
                    return Response(request, "delegate", [DelegateToolCall()]);
                }

                return Response(request, "publish", [PublishToolCall(request)]);
            }

            if (scenario == RepromptScenario.NonObjectPublishArgumentsThenValid)
            {
                if (orchestratorCalls == 1)
                {
                    return Response(request, "delegate", [DelegateToolCall()]);
                }

                if (orchestratorCalls == 2)
                {
                    return Response(request, "publish malformed", [StringArgumentToolCall("publish-malformed", "publish_report", "{\"report_json\":")]);
                }

                EnsureValidationError(request, "Malformed publish_report reprompt did not include validation error.");
                return Response(request, "publish", [PublishToolCall(request)]);
            }

            if (scenario == RepromptScenario.MaxWorkersExceeded)
            {
                return Response(request, "delegate", [ToolCall("delegate-analysis-" + orchestratorCalls, "delegate", "{\"role\":\"analysis\",\"task\":\"Analyze the signal again.\"}")]);
            }

            if (orchestratorCalls == 1)
            {
                return Response(request, "delegate", [DelegateToolCall()]);
            }

            return Response(request, "publish", [PublishToolCall(request)]);
        }

        private AiModelResponse WorkerResponse(AiModelRequest request)
        {
            if (scenario == RepromptScenario.InvalidWorkerOutput)
            {
                if (request.Messages.Count > 2 && !request.Messages.Any(chatMessage => chatMessage.Content.Contains("Validation error", StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException("Worker reprompt did not include validation error.");
                }

                return Response(request, WorkerOutputSentinel + " {", []);
            }

            return Response(request, JsonSerializer.Serialize(new
            {
                keyFacts = ValidWorkerKeyFacts,
                candidateClassification = "SimpleKnownError",
                needsDeeperContext = false,
                rationale = "Valid worker output."
            }), []);
        }

        private void EnsureValidationErrorAfterFirstTurn(AiModelRequest request, string label)
        {
            if (orchestratorCalls > 1)
            {
                EnsureValidationError(request, label + " reprompt did not include validation error.");
            }
        }

        private static void EnsureValidationError(AiModelRequest request, string message)
        {
            if (!request.Messages.Any(chatMessage => chatMessage.Content.Contains("Validation error", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(message);
            }
        }
        private static bool IsOrchestrator(AiModelRequest request)
        {
            var toolNames = request.Tools?.Select(static tool => tool.Name).ToHashSet(StringComparer.Ordinal) ?? [];
            return toolNames.SetEquals(["delegate", "publish_report"]);
        }

        private static AiToolCall DelegateToolCall()
        {
            return ToolCall("delegate-analysis", "delegate", "{\"role\":\"analysis\",\"task\":\"Analyze the signal.\"}");
        }
        private static AiModelResponse Response(AiModelRequest request, string content, IReadOnlyList<AiToolCall> toolCalls)
        {
            return new AiModelResponse(content, request.Model, "reprompt-test", new AiModelUsage(10, 5, 15), request.CorrelationId, toolCalls);
        }

        private static AiToolCall PublishToolCall(AiModelRequest request)
        {
            var referenceId = FindPromptArtifactId(request, "TriggerSignal");
            return ToolCall("publish", "publish_report", "{\"report_json\":{\"status\":\"Completed\",\"summary\":\"Reprompt run completed.\",\"classification\":\"SimpleKnownError\",\"confidence\":\"Medium\",\"documentationFit\":\"Missing\",\"evidence\":[{\"referenceId\":\"" + referenceId + "\"}],\"limitations\":[],\"recommendedNextAction\":\"Review logs.\"}}");
        }

        private static string FindPromptArtifactId(AiModelRequest request, string kind)
        {
            var prompt = request.Messages.FirstOrDefault(static message => message.Role == AiMessageRole.User)?.Content ?? string.Empty;
            foreach (var line in prompt.Split('\n'))
            {
                if (!line.Contains("kind=" + kind, StringComparison.Ordinal))
                {
                    continue;
                }

                var marker = "artifact:";
                var start = line.IndexOf(marker, StringComparison.Ordinal);
                if (start >= 0)
                {
                    return line[(start + marker.Length)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                }
            }

            return Guid.Empty.ToString();
        }

        private static AiToolCall StringArgumentToolCall(string id, string name, string arguments)
        {
            var element = JsonSerializer.SerializeToElement(arguments);
            return new AiToolCall(id, name, "v1", element);
        }
        private static AiToolCall ToolCall(string id, string name, string argumentsJson)
        {
            using var arguments = JsonDocument.Parse(argumentsJson);
            return new AiToolCall(id, name, "v1", arguments.RootElement.Clone());
        }
    }

    private sealed class MissingSnapshotConfigurationRepository : ITriageConfigurationRepository
    {
        public Task<TriageConfiguration> GetCurrentAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Current configuration is not used by this test.");

        public Task<TriageConfiguration> GetByHashAsync(string configHash, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Injected config snapshot load failure for " + configHash + ".");
    }
    private enum RepromptScenario { Valid, InvalidWorkerOutput, NoOrchestratorTool, NonObjectDelegateArgumentsThenValid, NonObjectPublishArgumentsThenValid, UnknownOrchestratorTool, MaxWorkersExceeded }

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

    private sealed record JobRow(string Status, string? LastErrorCode, string LastErrorMessage);

    private sealed record RepromptBudgetEvent(
        string? Role,
        string Rationale,
        int? TokensDelta,
        int? WorkersDelta);
}
