using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.ModelGateway;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Application.Investigation.Reports;
using IncidentCompass.Application.Investigation.Reports.Fallback;
using IncidentCompass.Domain.Incidents;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// A route declares a fallback, its provider answers the first call with an outage, and the same
/// call is retried once on the fallback route. These tests follow that through the real publish
/// path: what the ledger records for both calls, what the published report says about the models
/// that answered, and what it says about having run degraded.
/// </summary>
[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class TriageRouteFallbackTests(PostgresRepositoryFixture postgres)
{
    private static readonly JsonSerializerOptions ResponseOptions = new(JsonSerializerDefaults.Web);

    [DockerAvailableFact]
    public async Task UnavailablePrimaryProvider_PublishesOnTheFallbackRouteAndSaysSo()
    {
        using var scope = await CreateScopeAsync();
        var ingested = await PostIngestAsync(scope.Client, "route-fallback");
        var claimed = await RunClaimedJobAsync(scope, "worker-route-fallback", ingested.JobId!.Value);

        var modelCalls = await ReadModelCallsAsync(scope.ConnectionString, claimed.Id, claimed.Attempt);
        Assert.Equal(2, modelCalls.Count);

        // Both calls are in the ledger, and the failed one is charged rather than refunded, exempted
        // or hidden: what the attempt is charged is what the provider would invoice for both.
        var failed = modelCalls[0];
        Assert.Equal("failed", failed.Outcome);
        Assert.Equal("report-chat", failed.RouteId);
        Assert.Equal("provider_unavailable", failed.ErrorCode);
        Assert.Null(failed.FallbackForRouteId);
        Assert.Equal(FailingThenFallbackModelClient.FailedTotalTokens, failed.TotalTokens);

        var succeeded = modelCalls[1];
        Assert.Equal("success", succeeded.Outcome);
        Assert.Equal("report-chat-fallback", succeeded.RouteId);
        Assert.Equal("local-fallback-model", succeeded.Model);
        Assert.Equal("report-chat", succeeded.FallbackForRouteId);

        var charged = await ScalarAsync<long>(
            scope.ConnectionString,
            """
            SELECT COALESCE(SUM(tokens_delta), 0)
            FROM incidentcompass.triage_ledger
            WHERE job_id = @job_id AND attempt = @attempt AND event_type = 'BudgetEvent';
            """,
            ("job_id", claimed.Id),
            ("attempt", claimed.Attempt));
        Assert.Equal(
            FailingThenFallbackModelClient.FailedTotalTokens + FailingThenFallbackModelClient.AnsweredTotalTokens,
            charged);

        var reportId = await ScalarAsync<Guid>(
            scope.ConnectionString,
            "SELECT id FROM incidentcompass.triage_reports WHERE fault_id = @fault_id;",
            ("fault_id", ingested.FaultId));
        var details = await GetReportAsync(scope.Client, reportId);

        // Provenance names what answered, which is the fallback route and its model. The failed call
        // produced nothing the report is built on, so it is not a participant.
        var participant = Assert.Single(details.ModelProvenance!);
        Assert.Equal("report-chat-fallback", participant.RouteId);
        Assert.Equal("local-fallback-model", participant.Model);

        // And the part provenance cannot say on its own: that something failed first. An attempt
        // legitimately spans several routes, so a second route in that list is not by itself a
        // degraded run.
        Assert.Contains(ModelFallbackReportPolicy.DegradedRoutingLimitation, details.Limitations);
    }

    [DockerAvailableFact]
    public async Task RouteWithoutAFallback_PublishesWithNoDegradedRoutingClaim()
    {
        using var scope = await CreateScopeAsync(configureServices: services =>
        {
            services.RemoveAll<IAiModelClient>();
            services.AddScoped<IAiModelClient, PublishingModelClient>();
        });
        var ingested = await PostIngestAsync(scope.Client, "route-no-fallback");
        var claimed = await RunClaimedJobAsync(scope, "worker-route-no-fallback", ingested.JobId!.Value);

        var modelCall = Assert.Single(await ReadModelCallsAsync(scope.ConnectionString, claimed.Id, claimed.Attempt));
        Assert.Equal("success", modelCall.Outcome);
        Assert.Null(modelCall.FallbackForRouteId);

        var reportId = await ScalarAsync<Guid>(
            scope.ConnectionString,
            "SELECT id FROM incidentcompass.triage_reports WHERE fault_id = @fault_id;",
            ("fault_id", ingested.FaultId));
        var details = await GetReportAsync(scope.Client, reportId);

        Assert.DoesNotContain(ModelFallbackReportPolicy.DegradedRoutingLimitation, details.Limitations);
    }

    private static async Task<IReadOnlyList<ModelCallLedgerMetadata>> ReadModelCallsAsync(
        string connectionString,
        Guid jobId,
        int attempt)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT rationale
            FROM incidentcompass.triage_ledger
            WHERE job_id = @job_id AND attempt = @attempt AND event_type = 'ModelCall'
            ORDER BY id;
            """, connection);
        command.Parameters.AddWithValue("job_id", jobId);
        command.Parameters.AddWithValue("attempt", attempt);

        var metadata = new List<ModelCallLedgerMetadata>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            metadata.Add(JsonSerializer.Deserialize<ModelCallLedgerMetadata>(reader.GetString(0))!);
        }

        return metadata;
    }

    private async Task<TestScope> CreateScopeAsync(Action<IServiceCollection>? configureServices = null)
    {
        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        await PostgresTriageJobTestIsolation.CompleteClaimableJobsAsync(connectionString);
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:IncidentCompass", connectionString);
            builder.UseExplicitMockProviders();
            builder.UseSetting("IncidentCompass:ConfigSource:Path", FallbackFixtureConfigPath());
            builder.ConfigureTestServices(configureServices ?? (services =>
            {
                services.RemoveAll<IAiModelClient>();
                services.AddScoped<IAiModelClient, FailingThenFallbackModelClient>();
            }));
        });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        return new TestScope(factory, client, connectionString);
    }

    private static string FallbackFixtureConfigPath() =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "fallback-route-triage-config", "incidentcompass.config.json");

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
                new TesterAttributesDto("TimeoutException", "fallback probe " + unique, "/fallback")),
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

    private static async Task<TriageReportFallbackDetailsDto> GetReportAsync(HttpClient client, Guid reportId)
    {
        var response = await client.GetAsync("/api/v1/triage-reports/" + reportId, TestContext.Current.CancellationToken);
        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"Expected a successful report response, received {(int)response.StatusCode}: {content}");
        return JsonSerializer.Deserialize<TriageReportFallbackDetailsDto>(content, ResponseOptions)!;
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

    private static AiModelResponse PublishResponse(AiModelRequest request, string summary)
    {
        var referenceId = FindPromptArtifactId(request, "TriggerSignal")
            ?? throw new InvalidOperationException("No citable prompt artifact was found.");
        using var arguments = JsonDocument.Parse(
            "{\"report_json\":{\"status\":\"Completed\",\"summary\":\"" + summary + "\"," +
            "\"classification\":\"SimpleKnownError\",\"confidence\":\"Medium\",\"documentationFit\":\"Missing\"," +
            "\"evidence\":[{\"referenceId\":\"" + referenceId + "\"}],\"limitations\":[]," +
            "\"recommendedNextAction\":\"Review the fallback answer.\"}}");
        return new AiModelResponse(
            "publish",
            request.Model,
            "fallback-test-provider",
            new AiModelUsage(20, 10, FailingThenFallbackModelClient.AnsweredTotalTokens),
            request.CorrelationId,
            [new AiToolCall("publish-after-fallback", "publish_report", "v1", arguments.RootElement.Clone())]);
    }

    /// <summary>
    /// Answers the first call of the attempt with a provider outage carrying billable usage, and the
    /// next call - which is the same logical call reaching the fallback route - with a publishable
    /// report.
    /// </summary>
    private sealed class FailingThenFallbackModelClient : IAiModelClient
    {
        public const int FailedTotalTokens = 9;
        public const int AnsweredTotalTokens = 30;

        private int callCount;

        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (++callCount == 1)
            {
                throw new AiModelException(
                    "fallback-test-provider",
                    "Model provider is unavailable.",
                    errorCode: "provider_unavailable",
                    failureKind: ProviderFailureKind.Unavailable,
                    usage: new AiModelUsage(9, 0, FailedTotalTokens));
            }

            return Task.FromResult(PublishResponse(request, "Fallback route probe."));
        }
    }

    private sealed class PublishingModelClient : IAiModelClient
    {
        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            return Task.FromResult(PublishResponse(request, "Primary route probe."));
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

    private sealed record TriageReportFallbackDetailsDto(
        Guid Id,
        IReadOnlyList<string> Limitations,
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
