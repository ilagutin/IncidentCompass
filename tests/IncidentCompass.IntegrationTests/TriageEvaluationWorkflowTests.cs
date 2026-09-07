using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Memory;
using IncidentCompass.Infrastructure.Notifications.Telegram;
using IncidentCompass.Infrastructure.Tickets;
using IncidentCompass.TestSupport;
using IncidentCompass.Worker;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class TriageEvaluationWorkflowTests(PostgresRepositoryFixture postgres)
{
    private static readonly string[] RequiredCaseKinds =
        ["adversarial", "insufficient", "known", "stale", "unknown"];
    private static readonly string[] ReportStatuses = ["Completed", "InsufficientEvidence"];

    [DockerAvailableFact]
    public async Task VersionedFiveCaseContract_ExercisesModelIndependentWorkflowAndReusesRetrievalBenchmark()
    {
        var repoRoot = RepositoryRootLocator.Find();
        var evaluationCases = LoadWorkflowCases(Path.Combine(repoRoot, "evaluations", "triage", "corpus-v1.json"));
        var retrievalCorpus = MemoryRetrievalBenchmarkCorpus.Load(repoRoot);
        await using var scope = await CreateScopeAsync(repoRoot, retrievalCorpus);
        await SeedRetrievalCorpusAsync(scope, retrievalCorpus);

        var retrievalResults = await ObserveRetrievalAsync(scope, retrievalCorpus);
        var retrievalEvaluation = MemoryRetrievalMetrics.Evaluate(retrievalCorpus, retrievalResults);
        Assert.Equal(retrievalCorpus.Queries.Count, retrievalEvaluation.Queries.Count);
        Assert.All(
            retrievalEvaluation.Queries.Where(static item => item.RelevantChunkCount > 0),
            static item => Assert.True(item.RelevantChunkHits > 0));
        Assert.Equal(0, retrievalEvaluation.Metrics.NoMatchFalsePositiveCount);

        var runId = Guid.NewGuid().ToString("N");
        foreach (var evaluationCase in evaluationCases)
        {
            var ingested = await PostAsync(scope.Client, CreateEnvelope(evaluationCase, runId));
            Assert.NotNull(ingested.JobId);
            await RunActualWorkerPumpAsync(scope.Factory, evaluationCase.Kind);
            var job = await ReadJobAsync(scope.ConnectionString, ingested.JobId.Value);

            if (evaluationCase.Kind == "adversarial")
            {
                await AssertAdversarialDenialAsync(scope, ingested, job);
                continue;
            }

            Assert.Equal("Succeeded", job.Status);
            var report = await ReadReportAsync(scope.ConnectionString, ingested.FaultId);
            Assert.NotNull(report);
            Assert.Equal(ingested.ConfigHash, report.ConfigHash);
            Assert.Contains(report.Status, ReportStatuses);
            Assert.False(string.IsNullOrWhiteSpace(report.Summary));
            Assert.False(string.IsNullOrWhiteSpace(report.NextAction));
            var evidence = await ReadEvidenceAsync(scope.ConnectionString, report.Id, ingested.JobId.Value, job.Attempt);
            Assert.Equal(0, evidence.ForeignAttemptCount);

            switch (evaluationCase.Kind)
            {
                case "known":
                    Assert.True(evidence.RetrievedItemCount > 0);
                    break;
                case "unknown":
                case "insufficient":
                    Assert.Equal("InsufficientEvidence", report.Status);
                    Assert.Equal("Unknown", report.Classification);
                    Assert.Equal(0, evidence.RetrievedItemCount);
                    Assert.Contains(report.Limitations, static item => item.Contains("no", StringComparison.OrdinalIgnoreCase));
                    break;
                case "stale":
                    Assert.Equal("StaleOnly", report.DocumentationFit);
                    Assert.True(evidence.StaleRetrievedItemCount > 0);
                    break;
            }
        }
    }

    private async Task<EvaluationTestScope> CreateScopeAsync(
        string repoRoot,
        MemoryRetrievalBenchmarkCorpus corpus)
    {
        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        await PostgresTriageJobTestIsolation.CompleteClaimableJobsAsync(connectionString);
        var configuration = CreateTestConfiguration(repoRoot, corpus);
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:IncidentCompass", connectionString);
            builder.UseExplicitMockProviders();
            builder.UseSetting("IncidentCompass:ConfigSource:Path", configuration.Path);
            builder.UseSetting("IncidentCompass:Embeddings:MockDimensions", corpus.EmbeddingDimensions.ToString(CultureInfo.InvariantCulture));
            builder.UseSetting("IncidentCompass:Memory:Seed:Enabled", "false");
            builder.UseSetting("IncidentCompass:Telegram:Enabled", "false");
            builder.UseSetting("IncidentCompass:Telegram:BotToken", string.Empty);
            builder.UseSetting("IncidentCompass:Tickets:GitHub:Token", string.Empty);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAiModelClient>();
                services.AddScoped<IAiModelClient, EvaluationScriptedModelClient>();
            });
        });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        return new EvaluationTestScope(factory, client, connectionString, configuration.Directory);
    }

    private static async Task SeedRetrievalCorpusAsync(EvaluationTestScope scope, MemoryRetrievalBenchmarkCorpus corpus)
    {
        using var serviceScope = scope.Factory.Services.CreateScope();
        await corpus.SeedAsync(
            scope.ConnectionString,
            serviceScope.ServiceProvider.GetRequiredService<IEmbeddingClient>(),
            serviceScope.ServiceProvider.GetRequiredService<IMemoryRepository>(),
            TestContext.Current.CancellationToken);
    }

    private static async Task<IReadOnlyList<MemoryRetrievalQueryResult>> ObserveRetrievalAsync(
        EvaluationTestScope scope,
        MemoryRetrievalBenchmarkCorpus corpus)
    {
        using var serviceScope = scope.Factory.Services.CreateScope();
        var configuration = await serviceScope.ServiceProvider.GetRequiredService<ITriageConfigurationRepository>()
            .GetCurrentAsync(TestContext.Current.CancellationToken);
        var memoryTool = serviceScope.ServiceProvider.GetServices<IImmediateAgentTool>()
            .Single(static tool => tool.Definition.Name == "memory_search");
        return await new ProductionMemoryRetrievalStrategy(memoryTool, configuration)
            .ExecuteAsync(corpus, TestContext.Current.CancellationToken);
    }

    private static async Task RunActualWorkerPumpAsync(WebApplicationFactory<Program> factory, string caseKind)
    {
        var pump = new WorkerJobPump(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            new WorkerJobLeaseRenewer(),
            factory.Services.GetRequiredService<ILogger<WorkerJobPump>>());
        var started = await pump.FillAvailableSlotsAsync(
            "evaluation-worker-" + caseKind,
            new WorkerOptions { MaxConcurrentJobs = 1, LeaseSeconds = 30, MaxAttempts = 1, RetryDelaySeconds = 1 },
            TestContext.Current.CancellationToken);
        Assert.Equal(1, started);

        // DrainAsync is the shutdown path: it cancels in-flight jobs before awaiting them, which
        // would abandon the claimed job in Processing. Wait for natural completion instead.
        using var completionTimeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        completionTimeout.CancelAfter(TimeSpan.FromMinutes(2));
        while (pump.ActiveJobCount > 0)
        {
            await pump.ObserveCompletedAsync(completionTimeout.Token);
            if (pump.ActiveJobCount > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), completionTimeout.Token);
            }
        }
    }

    private static async Task AssertAdversarialDenialAsync(
        EvaluationTestScope scope,
        IngestSignalResponse ingested,
        JobRow job)
    {
        Assert.Equal("DeadLettered", job.Status);
        using var serviceScope = scope.Factory.Services.CreateScope();
        var services = serviceScope.ServiceProvider;
        var configuration = await services.GetRequiredService<ITriageConfigurationRepository>()
            .GetCurrentAsync(TestContext.Current.CancellationToken);
        Assert.Empty(configuration.Actions.AllowedTools);
        var telegram = services.GetRequiredService<IOptions<TelegramOptions>>().Value;
        Assert.False(telegram.Enabled);
        Assert.True(string.IsNullOrWhiteSpace(telegram.BotToken));
        Assert.False(services.GetRequiredService<IOptions<GitHubIssuesOptions>>().Value.IsConfigured);
        Assert.Equal(1, await ScalarAsync<long>(
            scope.ConnectionString,
            "SELECT COUNT(*) FROM incidentcompass.triage_ledger WHERE job_id = @job_id AND event_type = 'PolicyDecision' AND tool_name = 'ticket_search' AND decision = 'Denied';",
            ("job_id", ingested.JobId!.Value)));
        Assert.Equal(0, await ScalarAsync<long>(
            scope.ConnectionString,
            "SELECT COUNT(*) FROM incidentcompass.action_approvals WHERE job_id = @job_id;",
            ("job_id", ingested.JobId.Value)));
        Assert.Null(await ReadReportAsync(scope.ConnectionString, ingested.FaultId));
    }

    private static (string Path, string Directory) CreateTestConfiguration(
        string repoRoot,
        MemoryRetrievalBenchmarkCorpus corpus)
    {
        var source = Path.Combine(repoRoot, "evaluations", "triage", "incidentcompass.config.json");
        var root = JsonNode.Parse(File.ReadAllText(source))!.AsObject();
        root["Ingestion"]!["DefaultTenant"] = corpus.TenantId;
        root["CurrentReleases"] = JsonSerializer.SerializeToNode(corpus.CurrentReleases);
        root["Routes"]!["memory-embed"]!["Model"] = corpus.EmbeddingModel;
        root["Orchestrator"]!["Instructions"] = Reference(repoRoot, "instructions", "orchestrator.md");
        foreach (var role in new[] { "analysis", "memory", "source", "tickets" })
        {
            root["Roles"]![role]!["Instructions"] = Reference(repoRoot, "instructions", role + ".md");
            root["Roles"]![role]!["OutputSchema"] = Reference(repoRoot, "schemas", role + ".json");
        }

        var directory = Path.Combine(Path.GetTempPath(), "incidentcompass-evaluation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "incidentcompass.config.json");
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return (path, directory);
    }

    private static string Reference(string repoRoot, string folder, string file) =>
        "ref:" + Path.Combine(repoRoot, "config", folder, file);

    private static WorkflowCase[] LoadWorkflowCases(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("triage-evaluation-corpus-v1", root.GetProperty("corpusVersion").GetString());
        var cases = root.GetProperty("cases").EnumerateArray().Select(item =>
        {
            var input = item.GetProperty("input");
            Assert.True(item.GetProperty("criteria").TryGetProperty("diagnosis", out _));
            Assert.True(item.GetProperty("criteria").TryGetProperty("evidence", out _));
            Assert.True(item.GetProperty("criteria").TryGetProperty("refusal", out _));
            Assert.True(item.GetProperty("criteria").TryGetProperty("tolerance", out _));
            return new WorkflowCase(
                item.GetProperty("kind").GetString()!,
                input.GetProperty("serviceName").GetString()!,
                input.GetProperty("environment").GetString()!,
                input.GetProperty("severity").GetString()!,
                input.GetProperty("errorType").GetString()!,
                input.GetProperty("errorMessage").GetString()!,
                input.GetProperty("route").GetString()!,
                input.GetProperty("operation").GetString()!);
        }).ToArray();
        Assert.Equal(RequiredCaseKinds, cases.Select(static item => item.Kind).Order().ToArray());
        return cases;
    }

    private static object CreateEnvelope(WorkflowCase evaluationCase, string runId) => new
    {
        sourceKind = "tester",
        serviceName = evaluationCase.ServiceName.Replace("{runId}", runId, StringComparison.Ordinal),
        environment = evaluationCase.Environment,
        severity = evaluationCase.Severity,
        observedAtUtc = DateTimeOffset.UtcNow,
        correlation = new
        {
            traceId = "trace-" + runId + "-" + evaluationCase.Kind,
            spanId = "span-" + runId + "-" + evaluationCase.Kind,
            externalId = "evaluation-" + runId + "-" + evaluationCase.Kind
        },
        attributes = new
        {
            errorType = evaluationCase.ErrorType,
            errorMessage = evaluationCase.ErrorMessage,
            route = evaluationCase.Route.Replace("{attemptKey}", "integration", StringComparison.Ordinal),
            operation = evaluationCase.Operation,
            statusCode = 500
        }
    };

    private static async Task<IngestSignalResponse> PostAsync(HttpClient client, object envelope)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/incidents", envelope, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<IngestSignalResponse>(TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("Evaluation ingest response was empty.");
    }

    private static async Task<JobRow> ReadJobAsync(string connectionString, Guid jobId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT status, attempt FROM incidentcompass.triage_jobs WHERE id = @id;", connection);
        command.Parameters.AddWithValue("id", jobId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new JobRow(reader.GetString(0), reader.GetInt32(1));
    }

    private static async Task<ReportRow?> ReadReportAsync(string connectionString, Guid faultId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT id, status, summary, classification, documentation_fit, recommended_next_action, limitations, config_hash FROM incidentcompass.triage_reports WHERE fault_id = @fault_id;", connection);
        command.Parameters.AddWithValue("fault_id", faultId);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? new ReportRow(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetFieldValue<string[]>(6), reader.GetString(7))
            : null;
    }

    private static async Task<EvidenceRow> ReadEvidenceAsync(
        string connectionString,
        Guid reportId,
        Guid jobId,
        int attempt)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT
                COUNT(*) FILTER (WHERE artifact.kind = 'RetrievedItem'),
                COUNT(*) FILTER (WHERE artifact.job_id <> @job_id
                    OR (artifact.attempt IS NOT NULL AND artifact.attempt <> @attempt)),
                COUNT(*) FILTER (WHERE artifact.kind = 'RetrievedItem' AND artifact.redacted_payload->>'documentationStatus' = 'Stale')
            FROM incidentcompass.triage_evidence evidence
            JOIN incidentcompass.triage_artifacts artifact ON artifact.id = evidence.artifact_id
            WHERE evidence.report_id = @report_id;
            """, connection);
        command.Parameters.AddWithValue("report_id", reportId);
        command.Parameters.AddWithValue("job_id", jobId);
        command.Parameters.AddWithValue("attempt", attempt);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new EvidenceRow(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static async Task<T> ScalarAsync<T>(
        string connectionString,
        string sql,
        params (string Name, object Value)[] parameters)
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

    private sealed record JobRow(string Status, int Attempt);

    private sealed record WorkflowCase(
        string Kind,
        string ServiceName,
        string Environment,
        string Severity,
        string ErrorType,
        string ErrorMessage,
        string Route,
        string Operation);

    private sealed record ReportRow(
        Guid Id,
        string Status,
        string Summary,
        string Classification,
        string DocumentationFit,
        string NextAction,
        IReadOnlyList<string> Limitations,
        string ConfigHash);

    private sealed record EvidenceRow(long RetrievedItemCount, long ForeignAttemptCount, long StaleRetrievedItemCount);

    private sealed record IngestSignalResponse(Guid SignalId, Guid FaultId, Guid? JobId, string? ConfigHash);

    private sealed class EvaluationTestScope(
        WebApplicationFactory<Program> factory,
        HttpClient client,
        string connectionString,
        string configurationDirectory) : IAsyncDisposable
    {
        public WebApplicationFactory<Program> Factory { get; } = factory;
        public HttpClient Client { get; } = client;
        public string ConnectionString { get; } = connectionString;

        public ValueTask DisposeAsync()
        {
            Client.Dispose();
            Factory.Dispose();
            Directory.Delete(configurationDirectory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
