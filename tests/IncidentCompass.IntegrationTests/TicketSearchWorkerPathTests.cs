using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Application.Tickets;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class TicketSearchWorkerPathTests(PostgresRepositoryFixture postgres)
{
    [DockerAvailableFact]
    public async Task GovernedSearch_CommitsToolResultAndPublishesClosedExistingTicketCitation()
    {
        var match = new TicketSearchMatch(
            "github", "owner/repo", "42", "Checkout timeout", "open", "octocat",
            DateTimeOffset.Parse("2026-01-15T00:00:00Z", CultureInfo.InvariantCulture), "https://github.com/owner/repo/issues/42", 0.85);
        using var scope = await CreateScopeAsync(new StubTicketSearch(new TicketSearchResult(
            TicketSearchOutcome.Matched, "ticket_search_matches", [match])));
        var ingested = await PostSignalAsync(scope.Client, "ticket-match");
        var (job, services, configuration, investigation) = await ClaimAsync(scope, ingested.JobId!.Value, "worker-ticket-match");

        var outputJson = await services.GetRequiredService<WorkerToolCallExecutor>().ExecuteAsync(
            job, configuration, investigation, "tickets",
            new AiToolCall("ticket-call", "ticket_search", "v1", Json("{}")),
            TestContext.Current.CancellationToken);
        using var output = JsonDocument.Parse(outputJson);
        var artifactId = Assert.Single(output.RootElement.GetProperty("items").EnumerateArray())
            .GetProperty("artifactId").GetString()!;
        await services.GetRequiredService<TriageReportPublisher>().PublishAsync(
            job, "worker-ticket-match",
            new AiToolCall("publish-ticket", "publish_report", "v1", PublishArguments(artifactId, [])),
            TestContext.Current.CancellationToken);

        var evidence = await ReadEvidenceAsync(scope.ConnectionString, ingested.FaultId);
        Assert.Equal("RetrievedItem", evidence.Kind);
        Assert.Equal("ExistingTicket", evidence.EvidenceKind);
        Assert.Equal("owner/repo", evidence.Repository);
        Assert.Equal(42, evidence.IssueNumber);
        Assert.Equal(0.85, evidence.Score);
        Assert.Equal(1, await CountToolResultsAsync(scope.ConnectionString, job.Id));
        var reportId = await ReadReportIdAsync(scope.ConnectionString, ingested.FaultId);
        var response = await scope.Client.GetAsync(
            "/api/v1/triage-reports/" + reportId,
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var responseJson = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var publicEvidence = Assert.Single(responseJson.RootElement.GetProperty("evidence").EnumerateArray());
        Assert.Equal("RetrievedItem", publicEvidence.GetProperty("artifactKind").GetString());
        var publicPayload = publicEvidence.GetProperty("artifactPayload");
        Assert.Equal("ExistingTicket", publicPayload.GetProperty("evidenceKind").GetString());
        Assert.Equal("https://github.com/owner/repo/issues/42", publicPayload.GetProperty("url").GetString());
        Assert.False(publicPayload.TryGetProperty("body", out _));
    }

    [DockerAvailableFact]
    public async Task NoMatchAndUnavailableOutcomesAreDurableAndBecomeCanonicalLimitations()
    {
        foreach (var item in new[]
        {
            (Result: TicketSearchResult.NoMatch("github", "owner/repo"), Expected: "Read-only context ticket_search returned no matches (ticket_search_no_matches)."),
            (Result: TicketSearchResult.Unavailable("ticket_search_rate_limited"), Expected: "Read-only context ticket_search was unavailable (ticket_search_rate_limited).")
        })
        {
            using var scope = await CreateScopeAsync(new StubTicketSearch(item.Result));
            var ingested = await PostSignalAsync(scope.Client, "ticket-outcome");
            var workerId = "worker-ticket-outcome";
            var (job, services, configuration, investigation) = await ClaimAsync(scope, ingested.JobId!.Value, workerId);
            var outputJson = await services.GetRequiredService<WorkerToolCallExecutor>().ExecuteAsync(
                job, configuration, investigation, "tickets",
                new AiToolCall("ticket-call", "ticket_search", "v1", Json("{}")),
                TestContext.Current.CancellationToken);
            using var output = JsonDocument.Parse(outputJson);
            Assert.False(output.RootElement.GetProperty("matched").GetBoolean());
            Assert.Empty(output.RootElement.GetProperty("items").EnumerateArray());
            var triggerArtifactId = await ReadTriggerArtifactIdAsync(scope.ConnectionString, job.Id);
            await services.GetRequiredService<TriageReportPublisher>().PublishAsync(
                job, workerId,
                new AiToolCall("publish-ticket", "publish_report", "v1", PublishArguments(triggerArtifactId.ToString(), [])),
                TestContext.Current.CancellationToken);

            Assert.Equal(item.Expected, await ReadLimitationAsync(scope.ConnectionString, ingested.FaultId));
            Assert.Equal(1, await CountToolResultsAsync(scope.ConnectionString, job.Id));
            Assert.Equal(0, await CountTicketEvidenceAsync(scope.ConnectionString, ingested.FaultId));
        }
    }

    private async Task<TestScope> CreateScopeAsync(ITicketSearch ticketSearch)
    {
        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        await PostgresTriageJobTestIsolation.CompleteClaimableJobsAsync(connectionString);
        var configPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "test-triage-config", "incidentcompass.config.json");
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:IncidentCompass", connectionString);
            builder.UseExplicitMockProviders();
            builder.UseSetting("IncidentCompass:ConfigSource:Path", configPath);
            builder.UseSetting("IncidentCompass:Tickets:GitHub:Owner", "owner");
            builder.UseSetting("IncidentCompass:Tickets:GitHub:Repository", "repo");
            builder.UseSetting("IncidentCompass:Tickets:GitHub:Token", "host-secret");
            builder.ConfigureTestServices(services =>
                services.Replace(ServiceDescriptor.Scoped<ITicketSearch>(_ => ticketSearch)));
        });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        return new TestScope(factory, client, connectionString);
    }

    private static async Task<(IncidentCompass.Domain.Incidents.TriageJob Job, IServiceProvider Services,
        TriageConfiguration Configuration, TriageJobInvestigationContext Investigation)> ClaimAsync(
        TestScope scope, Guid jobId, string workerId)
    {
        var serviceScope = scope.Factory.Services.CreateScope();
        scope.ServiceScopes.Add(serviceScope);
        var services = serviceScope.ServiceProvider;
        var job = await services.GetRequiredService<ITriageJobRunner>().ClaimNextAsync(
            workerId, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        Assert.NotNull(job);
        Assert.Equal(jobId, job.Id);
        var configuration = await services.GetRequiredService<ITriageConfigurationRepository>()
            .GetByHashAsync(job.ConfigHash, TestContext.Current.CancellationToken);
        var investigation = await services.GetRequiredService<ITriageJobInvestigationContextRepository>()
            .GetAsync(job.Id, job.Attempt, TestContext.Current.CancellationToken);
        return (job, services, configuration, investigation);
    }

    private static async Task<IngestResponse> PostSignalAsync(HttpClient client, string prefix)
    {
        // The service name is fixed because the ticket fixtures are bound to it, so the signal's
        // fingerprint is made distinct through the error message instead.
        var unique = IngestFingerprintUniqueness.Token();
        var response = await client.PostAsJsonAsync("/api/v1/incidents", new
        {
            sourceKind = "otel",
            serviceName = "checkout",
            environment = "prod",
            externalId = prefix + "-" + unique,
            observedAtUtc = DateTimeOffset.UtcNow,
            attributes = new { errorType = "TimeoutException", errorMessage = prefix + " " + unique }
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<IngestResponse>(TestContext.Current.CancellationToken))!;
    }

    private static JsonElement PublishArguments(string artifactId, string[] limitations) =>
        JsonSerializer.SerializeToElement(new
        {
            report_json = new
            {
                status = "Completed",
                summary = "Ticket context report.",
                classification = "SimpleKnownError",
                confidence = "Medium",
                documentationFit = "Missing",
                evidence = new[] { new { referenceId = artifactId, quote = (string?)null } },
                limitations,
                recommendedNextAction = "Review the evidence."
            }
        });

    private static async Task<EvidenceRow> ReadEvidenceAsync(string connectionString, Guid faultId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT e.kind, a.redacted_payload->>'evidenceKind', a.redacted_payload->>'repository',
                   (a.redacted_payload->>'issueNumber')::integer, (a.redacted_payload->>'score')::double precision
            FROM incidentcompass.triage_evidence e
            JOIN incidentcompass.triage_artifacts a ON a.id = e.artifact_id
            JOIN incidentcompass.triage_reports r ON r.id = e.report_id
            WHERE r.fault_id = @fault_id;
            """, connection);
        command.Parameters.AddWithValue("fault_id", faultId);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        return new EvidenceRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetDouble(4));
    }

    private static Task<int> CountToolResultsAsync(string connectionString, Guid jobId) =>
        ScalarAsync<int>(connectionString,
            "SELECT count(*)::integer FROM incidentcompass.triage_artifacts WHERE job_id = @id AND kind = 'ToolResult' AND domain_ref = 'tool:ticket_search';", jobId);

    private static Task<int> CountTicketEvidenceAsync(string connectionString, Guid faultId) =>
        ScalarAsync<int>(connectionString,
            "SELECT count(*)::integer FROM incidentcompass.triage_evidence e JOIN incidentcompass.triage_reports r ON r.id=e.report_id JOIN incidentcompass.triage_artifacts a ON a.id=e.artifact_id WHERE r.fault_id=@id AND a.domain_ref LIKE 'ticket:%';", faultId);

    private static Task<string> ReadLimitationAsync(string connectionString, Guid faultId) =>
        ScalarAsync<string>(connectionString,
            "SELECT limitations[1] FROM incidentcompass.triage_reports WHERE fault_id=@id;", faultId);

    private static Task<Guid> ReadTriggerArtifactIdAsync(string connectionString, Guid jobId) =>
        ScalarAsync<Guid>(connectionString,
            "SELECT id FROM incidentcompass.triage_artifacts WHERE job_id=@id AND kind='TriggerSignal';", jobId);

    private static Task<Guid> ReadReportIdAsync(string connectionString, Guid faultId) =>
        ScalarAsync<Guid>(connectionString,
            "SELECT id FROM incidentcompass.triage_reports WHERE fault_id=@id;", faultId);

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private sealed class StubTicketSearch(TicketSearchResult result) : ITicketSearch
    {
        public Task<TicketSearchResult> SearchAsync(TicketSearchRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed record IngestResponse(Guid FaultId, Guid? JobId);
    private sealed record EvidenceRow(string Kind, string EvidenceKind, string Repository, int IssueNumber, double Score);

    private sealed record TestScope(WebApplicationFactory<Program> Factory, HttpClient Client, string ConnectionString) : IDisposable
    {
        public List<IServiceScope> ServiceScopes { get; } = [];

        public void Dispose()
        {
            foreach (var scope in ServiceScopes) scope.Dispose();
            Client.Dispose();
            Factory.Dispose();
        }
    }
}
