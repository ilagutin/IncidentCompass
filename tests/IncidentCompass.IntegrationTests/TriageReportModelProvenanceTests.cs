using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Application.Investigation.Reports;
using IncidentCompass.Domain.Incidents;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// A published report says which models produced it. These tests pin what that claim means: it is
/// the set of distinct model participants of the publishing job attempt, it is derived from the
/// triage ledger rather than accumulated alongside it, and no part of it comes from model output.
/// </summary>
[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class TriageReportModelProvenanceTests(PostgresRepositoryFixture postgres)
{
    private static readonly JsonSerializerOptions ResponseOptions = new(JsonSerializerDefaults.Web);

    [DockerAvailableFact]
    public async Task PublishedReport_CarriesTheProvenanceTheLedgerRecordedForThatAttempt()
    {
        using var scope = await CreateScopeAsync();
        var ingested = await PostIngestAsync(scope.Client, "provenance-ledger-match");
        var claimed = await RunClaimedJobAsync(scope, "worker-provenance-ledger", ingested.JobId!.Value);

        var reportId = await ScalarAsync<Guid>(
            scope.ConnectionString,
            "SELECT id FROM incidentcompass.triage_reports WHERE fault_id = @fault_id;",
            ("fault_id", ingested.FaultId));

        // Aggregated here from the raw ledger rows rather than from anything the publish path
        // produced, so this asserts agreement between two independent derivations of the same fact.
        var expected = await AggregateLedgerModelCallsAsync(scope.ConnectionString, claimed.Id, claimed.Attempt);
        Assert.NotEmpty(expected);
        var stored = await ReadStoredProvenanceAsync(scope.ConnectionString, reportId);
        Assert.Equal(expected, stored);

        var details = await GetReportAsync(scope.Client, reportId);
        Assert.Equal(expected, details.ModelProvenance);
        Assert.Contains(details.ModelProvenance!, participant => participant.CallKind == "orchestrator");
        Assert.All(details.ModelProvenance!, participant => Assert.True(participant.CallCount > 0));
    }

    [DockerAvailableFact]
    public async Task WorkerRoleOnItsOwnRouteAndModel_IsItsOwnParticipantRatherThanCollapsed()
    {
        using var scope = await CreateScopeAsync(
            MultiModelFixtureConfigPath(),
            services =>
            {
                services.RemoveAll<IAiModelClient>();
                services.AddScoped<IAiModelClient, DelegatingThenPublishingModelClient>();
            });
        var ingested = await PostIngestAsync(scope.Client, "provenance-multi-model");
        var claimed = await RunClaimedJobAsync(scope, "worker-provenance-multi-model", ingested.JobId!.Value);

        var reportId = await ScalarAsync<Guid>(
            scope.ConnectionString,
            "SELECT id FROM incidentcompass.triage_reports WHERE fault_id = @fault_id;",
            ("fault_id", ingested.FaultId));
        var stored = await ReadStoredProvenanceAsync(scope.ConnectionString, reportId);
        Assert.Equal(
            await AggregateLedgerModelCallsAsync(scope.ConnectionString, claimed.Id, claimed.Attempt),
            stored);

        var orchestrator = Assert.Single(stored!, participant => participant.CallKind == "orchestrator");
        var worker = Assert.Single(stored!, participant => participant.CallKind == "worker");
        Assert.Null(orchestrator.Role);
        Assert.Equal("report-chat", orchestrator.RouteId);
        Assert.Equal("local-orchestrator-model", orchestrator.Model);
        Assert.Equal("analysis", worker.Role);
        Assert.Equal("analysis-chat", worker.RouteId);
        Assert.Equal("local-worker-model", worker.Model);

        // The point of the representation: the worker model is visible instead of being hidden
        // behind the route that happened to emit publish_report.
        Assert.NotEqual(orchestrator.Model, worker.Model);
        Assert.Equal(2, stored!.Count);
    }

    [DockerAvailableFact]
    public async Task ProvenanceClaimedByModelOutput_IsIgnoredInFavourOfWhatActuallyAnswered()
    {
        using var scope = await CreateScopeAsync(configureServices: services =>
        {
            services.RemoveAll<IAiModelClient>();
            services.AddScoped<IAiModelClient, ForgedProvenanceModelClient>();
        });
        var ingested = await PostIngestAsync(scope.Client, "provenance-forgery");
        await RunClaimedJobAsync(scope, "worker-provenance-forgery", ingested.JobId!.Value);

        var reportId = await ScalarAsync<Guid>(
            scope.ConnectionString,
            "SELECT id FROM incidentcompass.triage_reports WHERE fault_id = @fault_id;",
            ("fault_id", ingested.FaultId));
        var storedJson = await ScalarAsync<string>(
            scope.ConnectionString,
            "SELECT model_provenance::text FROM incidentcompass.triage_reports WHERE id = @report_id;",
            ("report_id", reportId));

        foreach (var forged in ForgedProvenanceModelClient.ForgedValues)
        {
            Assert.DoesNotContain(forged, storedJson, StringComparison.Ordinal);
        }

        var stored = await ReadStoredProvenanceAsync(scope.ConnectionString, reportId);
        var participant = Assert.Single(stored!);
        Assert.Equal("orchestrator", participant.CallKind);
        Assert.Equal("report-chat", participant.RouteId);
        Assert.Equal(ForgedProvenanceModelClient.AnsweringProvider, participant.Provider);
        Assert.Equal(1, participant.CallCount);

        var details = await GetReportAsync(scope.Client, reportId);
        Assert.Equal(stored, details.ModelProvenance);
    }

    /// <summary>
    /// Rebuilds the expected participant list straight from the attempt's <c>ModelCall</c> ledger
    /// rows, the way a reader with only the ledger in hand would.
    /// </summary>
    private static async Task<IReadOnlyList<TriageReportModelParticipant>> AggregateLedgerModelCallsAsync(
        string connectionString,
        Guid jobId,
        int attempt)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT role, rationale
            FROM incidentcompass.triage_ledger
            WHERE job_id = @job_id AND attempt = @attempt AND event_type = 'ModelCall'
            ORDER BY id;
            """, connection);
        command.Parameters.AddWithValue("job_id", jobId);
        command.Parameters.AddWithValue("attempt", attempt);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<TriageReportModelParticipant>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            var role = reader.IsDBNull(0) ? null : reader.GetString(0);
            using var metadata = JsonDocument.Parse(reader.GetString(1));
            var root = metadata.RootElement;
            if (root.GetProperty("outcome").GetString() != "success")
            {
                continue;
            }

            var participant = new TriageReportModelParticipant(
                root.GetProperty("kind").GetString()!,
                role,
                root.GetProperty("routeId").GetString()!,
                root.GetProperty("provider").GetString()!,
                root.GetProperty("model").GetString()!,
                CallCount: 0);
            var key = JsonSerializer.Serialize(participant);
            if (counts.TryGetValue(key, out var callCount))
            {
                counts[key] = callCount + 1;
                continue;
            }

            counts[key] = 1;
            order.Add(participant);
        }

        return order
            .Select(participant => participant with
            {
                CallCount = counts[JsonSerializer.Serialize(participant with { CallCount = 0 })]
            })
            .ToArray();
    }

    private static async Task<IReadOnlyList<TriageReportModelParticipant>?> ReadStoredProvenanceAsync(
        string connectionString,
        Guid reportId)
    {
        var stored = await ScalarAsync<string>(
            connectionString,
            "SELECT model_provenance::text FROM incidentcompass.triage_reports WHERE id = @report_id;",
            ("report_id", reportId));
        return JsonSerializer.Deserialize<IReadOnlyList<TriageReportModelParticipant>>(stored);
    }

    private async Task<TestScope> CreateScopeAsync(
        string? configPath = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        await PostgresTriageJobTestIsolation.CompleteClaimableJobsAsync(connectionString);
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
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        return new TestScope(factory, client, connectionString);
    }

    private static string MultiModelFixtureConfigPath() =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "multi-model-triage-config", "incidentcompass.config.json");

    private static async Task<IngestSignalResponseDto> PostIngestAsync(HttpClient client, string servicePrefix)
    {
        var unique = Guid.NewGuid().ToString("N");
        var response = await client.PostAsJsonAsync(
            "/api/v1/incidents",
            new TesterEnvelopeDto(
                "tester",
                servicePrefix + "-" + unique,
                "prod",
                DateTimeOffset.UtcNow,
                new TesterAttributesDto("TimeoutException", "provenance probe " + unique, "/provenance")),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<IngestSignalResponseDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.NotNull(body.JobId);
        return body;
    }

    private static async Task<TriageJob> RunClaimedJobAsync(TestScope scope, string workerId, Guid expectedJobId)
    {
        using var serviceScope = scope.Factory.Services.CreateScope();
        var runner = serviceScope.ServiceProvider.GetRequiredService<ITriageJobRunner>();
        var claimed = await runner.ClaimNextAsync(workerId, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        Assert.NotNull(claimed);
        Assert.Equal(expectedJobId, claimed.Id);
        await runner.ProcessClaimedAsync(
            claimed,
            workerId,
            new TriageJobProcessingSettings(MaxAttempts: 1, RetryDelay: TimeSpan.FromSeconds(1)),
            TestContext.Current.CancellationToken);
        return claimed;
    }

    private static async Task<TriageReportProvenanceDetailsDto> GetReportAsync(HttpClient client, Guid reportId)
    {
        var response = await client.GetAsync("/api/v1/triage-reports/" + reportId, TestContext.Current.CancellationToken);
        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"Expected a successful report response, received {(int)response.StatusCode}: {content}");
        return JsonSerializer.Deserialize<TriageReportProvenanceDetailsDto>(content, ResponseOptions)!;
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    /// <summary>
    /// Delegates once to the analysis role and then publishes, so the attempt spans the
    /// orchestrator route and a worker role's own route. The answering model name is the requested
    /// one, which is the route's configured model, so the two routes answer under two model names
    /// exactly as two different backing models would.
    /// </summary>
    private sealed class DelegatingThenPublishingModelClient : IAiModelClient
    {
        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            var isOrchestrator = request.Tools?.Any(static tool => tool.Name == "delegate") == true;
            if (!isOrchestrator)
            {
                return Task.FromResult(Respond(
                    request,
                    """
                    {"keyFacts":["The trigger signal names a timeout."],"candidateClassification":"SimpleKnownError","needsDeeperContext":false,"rationale":"The error shape is already clear."}
                    """,
                    []));
            }

            if (!request.Messages.Any(static message => message.Role == AiMessageRole.Tool))
            {
                using var delegateArguments = JsonDocument.Parse(
                    """{"role":"analysis","task":"Extract the key facts from the trigger signal."}""");
                return Task.FromResult(Respond(request, "Delegate analysis.",
                    [new AiToolCall("provenance-delegate-analysis", "delegate", "v1", delegateArguments.RootElement.Clone())]));
            }

            var referenceId = FindPromptArtifactId(request, "TriggerSignal")
                ?? throw new InvalidOperationException("No citable prompt artifact was found.");
            using var publishArguments = JsonDocument.Parse(
                "{\"report_json\":{\"status\":\"Completed\",\"summary\":\"Multi-model provenance probe.\"," +
                "\"classification\":\"SimpleKnownError\",\"confidence\":\"Medium\",\"documentationFit\":\"Missing\"," +
                "\"evidence\":[{\"referenceId\":\"" + referenceId + "\"}],\"limitations\":[]," +
                "\"recommendedNextAction\":\"Review the analysis facts.\"}}");
            return Task.FromResult(Respond(request, "Publish after analysis.",
                [new AiToolCall("provenance-publish", "publish_report", "v1", publishArguments.RootElement.Clone())]));
        }

        private static AiModelResponse Respond(
            AiModelRequest request,
            string content,
            IReadOnlyList<AiToolCall> toolCalls) =>
            new(content, request.Model, "multi-model-test", new AiModelUsage(20, 10, 30), request.CorrelationId, toolCalls);
    }

    private static string? FindPromptArtifactId(AiModelRequest request, string kind)
    {
        var prompt = request.Messages.First(message => message.Role == AiMessageRole.User).Content;
        var line = prompt.Split('\n').FirstOrDefault(value => value.Contains("kind=" + kind, StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        var start = line.IndexOf("artifact:", StringComparison.Ordinal);
        return line[(start + "artifact:".Length)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
    }

    /// <summary>
    /// Publishes in one turn and asserts, in its own report body, a provenance that never happened.
    /// The values it actually answers with are different again, so the stored provenance can only
    /// match one of the two.
    /// </summary>
    private sealed class ForgedProvenanceModelClient : IAiModelClient
    {
        public const string AnsweringProvider = "provenance-observed-provider";

        public static readonly string[] ForgedValues =
        [
            "forged-route",
            "forged-provider",
            "forged-model"
        ];

        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            var referenceId = FindPromptArtifactId(request, "TriggerSignal")
                ?? throw new InvalidOperationException("No citable prompt artifact was found.");
            using var arguments = JsonDocument.Parse(
                "{\"report_json\":{\"status\":\"Completed\",\"summary\":\"Forged provenance probe.\"," +
                "\"classification\":\"SimpleKnownError\",\"confidence\":\"Medium\",\"documentationFit\":\"Missing\"," +
                "\"evidence\":[{\"referenceId\":\"" + referenceId + "\"}],\"limitations\":[]," +
                "\"recommendedNextAction\":\"Review the forged provenance claim.\"," +
                "\"modelProvenance\":[{\"callKind\":\"orchestrator\",\"role\":null,\"routeId\":\"forged-route\"," +
                "\"provider\":\"forged-provider\",\"model\":\"forged-model\",\"callCount\":99}]}}");
            return Task.FromResult(new AiModelResponse(
                "publish",
                request.Model,
                AnsweringProvider,
                new AiModelUsage(10, 5, 15),
                request.CorrelationId,
                [new AiToolCall("publish-forged-provenance", "publish_report", "v1", arguments.RootElement.Clone())]));
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

    private sealed record TriageReportProvenanceDetailsDto(
        Guid Id,
        IReadOnlyList<TriageReportModelParticipant>? ModelProvenance);

    private sealed record TesterAttributesDto(string ErrorType, string ErrorMessage, string HttpRoute);

    private sealed record TesterEnvelopeDto(
        string SourceKind,
        string ServiceName,
        string Environment,
        DateTimeOffset ObservedAtUtc,
        TesterAttributesDto Attributes);

    private sealed record IngestSignalResponseDto(Guid SignalId, Guid FaultId, Guid? JobId, string? ConfigHash);
}
