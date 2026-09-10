using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Governance.Validation;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Application.Investigation.Jobs.Testing;
using IncidentCompass.Domain.Governance;
using IncidentCompass.Worker;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class GovernedWorkerToolPathTests(PostgresRepositoryFixture postgres)
{
    [DockerAvailableFact]
    public async Task ProcessClaimedAsync_SyntheticPreconditionReadsSuccessfulToolResultAcrossWorkerSessions()
    {
        using var scope = await CreateScopeAsync(GovernanceScenario.Precondition);
        var ingested = await RunOneAsync(scope);

        var decisions = await ReadLedgerRowsAsync(scope.ConnectionString, ingested.JobId!.Value, "PolicyDecision");
        Assert.Contains(decisions, row => row.ToolName == "tool_y" && row.Decision == "Allowed");
        Assert.Contains(decisions, row => row.ToolName == "tool_x" && row.Decision == "Allowed" && row.DecisionReason!.Contains("precondition satisfied", StringComparison.Ordinal));

        var toolResults = await ReadLedgerRowsAsync(scope.ConnectionString, ingested.JobId.Value, "ToolResult");
        Assert.Contains(toolResults, row => row.ToolName == "tool_y" && row.ToolStatus == "Succeeded" && row.Rationale == "Tool completed successfully.");
        Assert.Contains(toolResults, row => row.ToolName == "tool_x" && row.ToolStatus == "Succeeded" && row.Rationale == "Tool completed successfully.");
    }

    [DockerAvailableFact]
    public async Task ProcessClaimedAsync_RateCapDeniesPastMaxInCurrentAttempt()
    {
        using var scope = await CreateScopeAsync(GovernanceScenario.RateCap);
        var ingested = await RunOneAsync(scope);

        var job = await ReadJobStatusAsync(scope.ConnectionString, ingested.JobId!.Value);
        var decisions = await ReadLedgerRowsAsync(scope.ConnectionString, ingested.JobId.Value, "PolicyDecision");
        var toolResults = await ReadLedgerRowsAsync(scope.ConnectionString, ingested.JobId.Value, "ToolResult");

        Assert.Equal("DeadLettered", job.Status);
        Assert.Contains(decisions, row => row.ToolName == "tool_x" && row.Decision == "Allowed");
        Assert.Contains(decisions, row => row.ToolName == "tool_x" && row.Decision == "Denied" && row.DecisionReason!.StartsWith("rate_cap_exceeded:", StringComparison.Ordinal));
        var toolXResult = Assert.Single(toolResults, row => row.ToolName == "tool_x");
        Assert.Equal("Succeeded", toolXResult.ToolStatus);
    }

    [DockerAvailableFact]
    public async Task ConfigurationLoadRejectsApprovalRuleForImmediateTool()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateScopeAsync(GovernanceScenario.RequiresApproval));

        Assert.Contains("Rules.requires_approval.Tool", exception.Message, StringComparison.Ordinal);
        Assert.Contains("external action tool id", exception.Message, StringComparison.Ordinal);
    }

    [DockerAvailableFact]
    public async Task ProcessClaimedAsync_UnknownWorkerToolFailsClosedWithAuditVisibleDecision()
    {
        using var scope = await CreateScopeAsync(GovernanceScenario.UnknownTool);
        var ingested = await RunOneAsync(scope);

        var job = await ReadJobStatusAsync(scope.ConnectionString, ingested.JobId!.Value);
        var decisions = await ReadLedgerRowsAsync(scope.ConnectionString, ingested.JobId.Value, "PolicyDecision");
        var toolResults = await ReadLedgerRowsAsync(scope.ConnectionString, ingested.JobId.Value, "ToolResult");

        Assert.Equal("DeadLettered", job.Status);
        Assert.Contains(decisions, row => row.ToolName == "unknown_tool" && row.Decision == "Denied");
        Assert.DoesNotContain(toolResults, row => row.ToolName == "unknown_tool");
    }

    [DockerAvailableFact]
    public async Task ProcessClaimedAsync_ToolResultCommitRollbackLeavesNoArtifactOrResultEvent()
    {
        using var scope = await CreateScopeAsync(
            GovernanceScenario.AtomicCrash,
            services =>
            {
                services.RemoveAll<ITriageToolResultCommitFaultInjector>();
                services.AddScoped<ITriageToolResultCommitFaultInjector, ThrowAfterArtifactInserted>();
            });
        var ingested = await RunOneAsync(scope);

        var job = await ReadJobStatusAsync(scope.ConnectionString, ingested.JobId!.Value);
        var toolResultArtifacts = await ScalarAsync<long>(
            scope.ConnectionString,
            "SELECT COUNT(*) FROM incidentcompass.triage_artifacts WHERE job_id = @job_id AND kind = 'ToolResult';",
            ("job_id", ingested.JobId.Value));
        var toolResultEvents = await ScalarAsync<long>(
            scope.ConnectionString,
            "SELECT COUNT(*) FROM incidentcompass.triage_ledger WHERE job_id = @job_id AND event_type = 'ToolResult';",
            ("job_id", ingested.JobId.Value));

        Assert.Equal("DeadLettered", job.Status);
        Assert.Equal(0, toolResultArtifacts);
        Assert.Equal(0, toolResultEvents);
    }

    [DockerAvailableFact]
    public async Task WorkerPump_OwnershipLossCancelsBlockingToolBeforePublication()
    {
        var blockingTool = new BlockingSyntheticTool("tool_y");
        using var scope = await CreateScopeAsync(
            GovernanceScenario.Precondition,
            services =>
            {
                services.RemoveAll<IImmediateAgentTool>();
                services.AddScoped<IImmediateAgentTool>(_ => new SyntheticTool("tool_x"));
                services.AddSingleton(blockingTool);
                services.AddScoped<IImmediateAgentTool>(serviceProvider => serviceProvider.GetRequiredService<BlockingSyntheticTool>());
            });
        var ingested = await PostIngestAsync(scope.Client);
        var pump = new WorkerJobPump(
            scope.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            new WorkerJobLeaseRenewer(),
            scope.Factory.Services.GetRequiredService<ILogger<WorkerJobPump>>());
        var options = new WorkerOptions
        {
            MaxConcurrentJobs = 1,
            LeaseSeconds = 3,
            MaxAttempts = 3,
            RetryDelaySeconds = 1
        };

        Assert.Equal(1, await pump.FillAvailableSlotsAsync("worker-tool-stale", options, TestContext.Current.CancellationToken));
        await blockingTool.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await ExecuteAsync(scope.ConnectionString, """
            UPDATE incidentcompass.triage_jobs
            SET attempt = 2,
                locked_by = 'worker-tool-new-owner',
                locked_until_utc = now() + interval '5 minutes'
            WHERE id = @job_id;
            """, ("job_id", ingested.JobId!.Value));

        await blockingTool.Cancelled.Task.WaitAsync(TestContext.Current.CancellationToken);
        await pump.WaitForNextWakeAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        await pump.ObserveCompletedAsync(TestContext.Current.CancellationToken);

        var reportCount = await ScalarAsync<long>(
            scope.ConnectionString,
            "SELECT COUNT(*) FROM incidentcompass.triage_reports WHERE fault_id = @fault_id;",
            ("fault_id", ingested.FaultId));
        var toolResults = await ReadLedgerRowsAsync(scope.ConnectionString, ingested.JobId.Value, "ToolResult");
        Assert.Equal(0, pump.ActiveJobCount);
        Assert.Equal(0, reportCount);
        Assert.DoesNotContain(toolResults, row => row.ToolName == "tool_y");
    }
    private async Task<TestScope> CreateScopeAsync(
        GovernanceScenario scenario,
        Action<IServiceCollection>? configureServices = null)
    {
        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        await PostgresTriageJobTestIsolation.CompleteClaimableJobsAsync(connectionString);
        var configPath = await CreateConfigurationAsync(scenario);

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:IncidentCompass", connectionString);
            builder.UseExplicitMockProviders();
            builder.UseSetting("IncidentCompass:ConfigSource:Path", configPath);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAiModelClient>();
                services.AddScoped<IAiModelClient>(_ => new SyntheticGovernanceModelClient(scenario));
                services.AddScoped<IImmediateAgentTool>(_ => new SyntheticTool("tool_x"));
                services.AddScoped<IImmediateAgentTool>(_ => new SyntheticTool("tool_y"));
                services.AddSingleton(new AgentToolDescriptor("tool_x", AgentToolCapability.ImmediateRead));
                services.AddSingleton(new AgentToolDescriptor("tool_y", AgentToolCapability.ImmediateRead));
                configureServices?.Invoke(services);
            });
        });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        return new TestScope(factory, client, connectionString);
    }

    private static async Task<IngestSignalResponseDto> RunOneAsync(TestScope scope)
    {
        var ingested = await PostIngestAsync(scope.Client);
        using var serviceScope = scope.Factory.Services.CreateScope();
        var runner = serviceScope.ServiceProvider.GetRequiredService<ITriageJobRunner>();
        var claimed = await runner.ClaimNextAsync("worker-governance", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        Assert.NotNull(claimed);
        Assert.Equal(ingested.JobId, claimed.Id);

        await runner.ProcessClaimedAsync(
            claimed,
            "worker-governance",
            new TriageJobProcessingSettings(MaxAttempts: 1, RetryDelay: TimeSpan.FromSeconds(1)),
            TestContext.Current.CancellationToken);
        return ingested;
    }

    private static async Task<string> CreateConfigurationAsync(GovernanceScenario scenario)
    {
        var directory = Path.Combine(Path.GetTempPath(), "incidentcompass-governance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "instructions"));
        Directory.CreateDirectory(Path.Combine(directory, "schemas"));
        await File.WriteAllTextAsync(Path.Combine(directory, "instructions", "orchestrator.md"), "Synthetic governance orchestrator.");
        await File.WriteAllTextAsync(Path.Combine(directory, "instructions", "synthetic_x.md"), "synthetic_x worker.");
        await File.WriteAllTextAsync(Path.Combine(directory, "instructions", "synthetic_y.md"), "synthetic_y worker.");
        await File.WriteAllTextAsync(Path.Combine(directory, "schemas", "analysis.json"), AnalysisSchema());
        var config = BuildConfig(scenario);
        var configPath = Path.Combine(directory, "incidentcompass.config.json");
        await File.WriteAllTextAsync(configPath, config.ToJsonString());
        return configPath;
    }

    private static JsonObject BuildConfig(GovernanceScenario scenario)
    {
        var roles = new JsonObject();
        var tools = new JsonObject();
        var rules = new JsonArray();
        AddRole(roles, "synthetic_x", scenario == GovernanceScenario.UnknownTool ? [] : ["tool_x"]);
        if (scenario == GovernanceScenario.Precondition)
        {
            AddRole(roles, "synthetic_y", ["tool_y"]);
        }

        if (scenario != GovernanceScenario.UnknownTool)
        {
            tools["tool_x"] = InternalTool();
        }

        if (scenario == GovernanceScenario.Precondition)
        {
            tools["tool_y"] = InternalTool();
            rules.Add(new JsonObject
            {
                ["Type"] = "precondition",
                ["Tool"] = "tool_x",
                ["Scope"] = "attempt",
                ["RequiresSuccessfulToolResult"] = "tool_y"
            });
        }
        else if (scenario == GovernanceScenario.RateCap)
        {
            rules.Add(new JsonObject { ["Type"] = "rate_cap", ["Tool"] = "tool_x", ["Scope"] = "attempt", ["Max"] = 1 });
        }
        else if (scenario == GovernanceScenario.RequiresApproval)
        {
            rules.Add(new JsonObject { ["Type"] = "requires_approval", ["Tool"] = "tool_x", ["Scope"] = "attempt" });
        }

        return new JsonObject
        {
            ["Providers"] = new JsonObject { ["local-oai"] = new JsonObject { ["Kind"] = "Mock" } },
            ["Routes"] = new JsonObject
            {
                ["analysis-chat"] = ChatRoute(),
                ["report-chat"] = ChatRoute()
            },
            ["Orchestrator"] = new JsonObject
            {
                ["Instructions"] = "ref:instructions/orchestrator.md",
                ["RouteId"] = "report-chat",
                ["Tools"] = new JsonArray("delegate", "publish_report"),
                ["Budget"] = new JsonObject
                {
                    ["MaxWorkers"] = 4,
                    ["MaxTokens"] = 100000,
                    ["MaxWallClockSeconds"] = 120,
                    ["MaxReprompts"] = 1
                }
            },
            ["Roles"] = roles,
            ["Tools"] = tools,
            ["Rules"] = rules,
            ["Ingestion"] = new JsonObject { ["DefaultTenant"] = "local", ["AllowedSources"] = new JsonArray("otel", "user", "tester", "manual") },
            ["FaultGrouping"] = new JsonObject
            {
                ["LookbackMinutes"] = 15,
                ["SilenceWindowMinutes"] = 30,
                ["FingerprintVersion"] = 1,
                ["MassIssue"] = new JsonObject { ["MinNeighborCount"] = 5, ["MinFingerprintStrength"] = "strong" }
            }
        };
    }

    private static void AddRole(JsonObject roles, string roleName, string[] toolNames)
    {
        var tools = new JsonArray();
        foreach (var toolName in toolNames)
        {
            tools.Add(toolName);
        }

        roles[roleName] = new JsonObject
        {
            ["RouteId"] = "analysis-chat",
            ["Instructions"] = "ref:instructions/" + roleName + ".md",
            ["Tools"] = tools,
            ["OutputSchema"] = "ref:schemas/analysis.json"
        };
    }

    private static JsonObject ChatRoute()
    {
        return new JsonObject
        {
            ["Kind"] = "Chat",
            ["ProviderId"] = "local-oai",
            ["Model"] = "synthetic-model",
            ["Temperature"] = 0.0,
            ["MaxOutputTokens"] = 1000,
            ["ContextWindowTokens"] = 8192
        };
    }

    private static JsonObject InternalTool()
    {
        return new JsonObject { ["Kind"] = "internal" };
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

    private static async Task<IngestSignalResponseDto> PostIngestAsync(HttpClient client)
    {
        var unique = Guid.NewGuid().ToString("N");
        var response = await client.PostAsJsonAsync(
            "/api/v1/incidents",
            new TesterEnvelopeDto(
                "tester",
                "governance-tool-svc-" + unique,
                "prod",
                DateTimeOffset.UtcNow,
                new TesterAttributesDto("TimeoutException", "Governance probe timed out " + unique, "/governance")),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<IngestSignalResponseDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.NotNull(body.JobId);
        return body;
    }

    private static async Task<IReadOnlyList<LedgerRow>> ReadLedgerRowsAsync(string connectionString, Guid jobId, string eventType)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT event_type, tool_name, decision, decision_reason, rationale, tool_status
            FROM incidentcompass.triage_ledger
            WHERE job_id = @job_id AND event_type = @event_type
            ORDER BY id;
            """,
            connection);
        command.Parameters.AddWithValue("job_id", jobId);
        command.Parameters.AddWithValue("event_type", eventType);

        var rows = new List<LedgerRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new LedgerRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return rows;
    }

    private static async Task<JobRow> ReadJobStatusAsync(string connectionString, Guid jobId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT status FROM incidentcompass.triage_jobs WHERE id = @job_id;", connection);
        command.Parameters.AddWithValue("job_id", jobId);
        return new JobRow((string)(await command.ExecuteScalarAsync())!);
    }

    private static async Task ExecuteAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
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

    private sealed class SyntheticGovernanceModelClient(GovernanceScenario scenario) : IAiModelClient
    {
        private static readonly string[] SyntheticKeyFacts = ["Synthetic tool path exercised."];

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
            if (scenario == GovernanceScenario.Precondition && orchestratorCalls == 1)
            {
                return Response(request, "delegate y", [ToolCall("delegate-y", "delegate", "{\"role\":\"synthetic_y\",\"task\":\"run tool_y\"}")]);
            }

            if (orchestratorCalls == 1 || (scenario == GovernanceScenario.Precondition && orchestratorCalls == 2))
            {
                return Response(request, "delegate x", [ToolCall("delegate-x", "delegate", "{\"role\":\"synthetic_x\",\"task\":\"run tool_x\"}")]);
            }

            return Response(request, "publish", [PublishToolCall(request)]);
        }

        private AiModelResponse WorkerResponse(AiModelRequest request)
        {
            var hasToolResult = request.Messages.Any(static message => message.Role == AiMessageRole.Tool);
            if (!hasToolResult)
            {
                var toolName = scenario == GovernanceScenario.UnknownTool
                    ? "unknown_tool"
                    : (request.Tools is { Count: > 0 } tools ? tools[0].Name : null) ?? "tool_x";
                return Response(request, "propose " + toolName, [ToolCall("worker-" + toolName + "-" + Guid.NewGuid().ToString("N"), toolName, "{}")]);
            }

            if (scenario == GovernanceScenario.RateCap && request.Messages.Count(message => message.Role == AiMessageRole.Tool) == 1)
            {
                return Response(request, "propose tool_x again", [ToolCall("worker-tool-x-repeat", "tool_x", "{}")]);
            }

            var rationale = scenario == GovernanceScenario.RequiresApproval
                ? "Tool required approval; approval required limitation recorded."
                : "Synthetic worker completed after tool result.";
            return Response(request, WorkerJson(rationale), []);
        }

        private static bool IsOrchestrator(AiModelRequest request)
        {
            var toolNames = request.Tools?.Select(static tool => tool.Name).ToHashSet(StringComparer.Ordinal) ?? [];
            return toolNames.SetEquals(["delegate", "publish_report"]);
        }

        private static AiModelResponse Response(AiModelRequest request, string content, IReadOnlyList<AiToolCall> toolCalls)
        {
            return new AiModelResponse(
                content,
                request.Model,
                "synthetic-governance",
                new AiModelUsage(10, 5, 15),
                request.CorrelationId,
                toolCalls);
        }

        private static string WorkerJson(string rationale)
        {
            return JsonSerializer.Serialize(new
            {
                keyFacts = SyntheticKeyFacts,
                candidateClassification = "SimpleKnownError",
                needsDeeperContext = false,
                rationale
            });
        }

        private static AiToolCall PublishToolCall(AiModelRequest request)
        {
            var referenceId = FindPromptArtifactId(request, "TriggerSignal");
            return ToolCall("publish", "publish_report", "{\"report_json\":{\"status\":\"Completed\",\"summary\":\"Synthetic governance run completed.\",\"classification\":\"SimpleKnownError\",\"confidence\":\"Medium\",\"documentationFit\":\"Missing\",\"evidence\":[{\"referenceId\":\"" + referenceId + "\"}],\"limitations\":[],\"recommendedNextAction\":\"Review synthetic tool output.\"}}");
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

        private static AiToolCall ToolCall(string id, string name, string argumentsJson)
        {
            using var arguments = JsonDocument.Parse(argumentsJson);
            return new AiToolCall(id, name, "v1", arguments.RootElement.Clone());
        }
    }

    private sealed class SyntheticTool(string name) : IImmediateAgentTool
    {
        public AiToolDefinition Definition { get; } = new(name, "Synthetic test-only tool.", "v1", Element("{\"type\":\"object\"}"));

        public ToolValidationResult Validate(JsonElement arguments)
        {
            return arguments.ValueKind == JsonValueKind.Object
                ? ToolValidationResult.Valid(arguments.Clone())
                : ToolValidationResult.Invalid("invalid_arguments", "Synthetic tool arguments must be an object.");
        }

        public Task<ToolExecutionResult> ExecuteAsync(AgentToolExecutionContext context, JsonElement sanitizedArguments, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ToolExecutionResult(ToolExecutionStatus.Succeeded, Element("{\"tool\":\"" + name + "\",\"status\":\"ok\"}")));
        }

        private static JsonElement Element(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
    }

    private sealed class BlockingSyntheticTool(string name) : IImmediateAgentTool
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AiToolDefinition Definition { get; } = new(name, "Blocking synthetic test-only tool.", "v1", Element("{\"type\":\"object\"}"));

        public ToolValidationResult Validate(JsonElement arguments) => ToolValidationResult.Valid(arguments.Clone());

        public async Task<ToolExecutionResult> ExecuteAsync(AgentToolExecutionContext context, JsonElement sanitizedArguments, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }

            throw new InvalidOperationException("The blocking synthetic tool unexpectedly completed.");
        }

        private static JsonElement Element(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
    }
    private sealed class ThrowAfterArtifactInserted : ITriageToolResultCommitFaultInjector
    {
        public Task AfterArtifactInsertedAsync(CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Injected crash between tool artifact and ToolResult ledger event.");
        }
    }

    private enum GovernanceScenario { Precondition, RateCap, RequiresApproval, UnknownTool, AtomicCrash }

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

    private sealed record LedgerRow(string EventType, string? ToolName, string? Decision, string? DecisionReason, string? Rationale, string? ToolStatus);

    private sealed record JobRow(string Status);
}
