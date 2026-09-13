using System.Net;
using System.Net.Http.Json;
using IncidentCompass.Application.Investigation.Jobs;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class TriageScenarioTests(PostgresRepositoryFixture postgres)
{
    [DockerAvailableFact]
    public async Task Scenario3_StrongFingerprintNeighborFloodStampsMassIssueTrue()
    {
        using var scope = await CreateScopeAsync();
        var serviceName = "mass-issue-svc-" + Guid.NewGuid().ToString("N");
        const string errorMessage = "provider unavailable flood";
        var first = await PostIngestAsync(scope.Client, serviceName, "TimeoutException", errorMessage, "/provider");
        for (var index = 0; index < 5; index++)
        {
            await PostIngestAsync(scope.Client, serviceName, "TimeoutException", errorMessage, "/provider");
        }

        await ExecuteAsync(scope.ConnectionString, """
            UPDATE incidentcompass.faults
            SET status = 'Completed',
                created_at_utc = now() - interval '32 minutes',
                completed_at_utc = now() - interval '31 minutes'
            WHERE id = @fault_id;

            UPDATE incidentcompass.triage_jobs
            SET status = 'Succeeded', locked_by = NULL, locked_until_utc = NULL
            WHERE id = @job_id;
            """, ("fault_id", first.FaultId), ("job_id", first.JobId!.Value));

        var recurrence = await PostIngestAsync(scope.Client, serviceName, "TimeoutException", errorMessage, "/provider");
        await ExecuteAsync(scope.ConnectionString, """
            UPDATE incidentcompass.triage_jobs
            SET status = 'DeadLettered', locked_by = NULL, locked_until_utc = NULL
            WHERE id <> @job_id AND status = 'Pending';
            """, ("job_id", recurrence.JobId!.Value));
        var artifactMassIssue = await ScalarAsync<string>(scope.ConnectionString, "SELECT redacted_payload->>'isMassIssue' FROM incidentcompass.triage_artifacts WHERE job_id = @job_id AND kind = 'NeighborSet';", ("job_id", recurrence.JobId.Value));
        Assert.Equal("true", artifactMassIssue);

        await RunClaimedJobAsync(scope, recurrence.JobId.Value, "worker-mass-issue");

        var isMassIssue = await ScalarAsync<bool?>(scope.ConnectionString, "SELECT is_mass_issue FROM incidentcompass.triage_reports WHERE fault_id = @fault_id;", ("fault_id", recurrence.FaultId));
        Assert.True(isMassIssue);
    }

    [DockerAvailableFact]
    public async Task Scenario4_NoiseSignalClosesWithoutMemoryDelegation()
    {
        using var scope = await CreateScopeAsync();
        var ingested = await PostIngestAsync(
            scope.Client,
            "noise-svc-" + Guid.NewGuid().ToString("N"),
            "ValidationNoise",
            "noise validation event from synthetic monitor",
            "/noise");

        await RunClaimedJobAsync(scope, ingested.JobId!.Value, "worker-noise");

        var classification = await ScalarAsync<string>(scope.ConnectionString, "SELECT classification FROM incidentcompass.triage_reports WHERE fault_id = @fault_id;", ("fault_id", ingested.FaultId));
        var memoryDelegations = await ScalarAsync<long>(scope.ConnectionString, "SELECT COUNT(*) FROM incidentcompass.triage_ledger WHERE job_id = @job_id AND event_type = 'Delegated' AND role = 'memory';", ("job_id", ingested.JobId.Value));
        Assert.Equal("Noise", classification);
        Assert.Equal(0, memoryDelegations);
    }

    private async Task<TestScope> CreateScopeAsync()
    {
        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        await PostgresTriageJobTestIsolation.CompleteClaimableJobsAsync(connectionString);
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:IncidentCompass", connectionString);
            builder.UseExplicitMockProviders();
            builder.UseWorkerModelHost();
        });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        return new TestScope(factory, client, connectionString);
    }

    private static async Task RunClaimedJobAsync(TestScope scope, Guid expectedJobId, string workerId)
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
    }

    private static async Task<IngestSignalResponseDto> PostIngestAsync(
        HttpClient client,
        string serviceName,
        string errorType,
        string errorMessage,
        string route)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/incidents",
            new TesterEnvelopeDto(
                "tester",
                serviceName,
                "prod",
                DateTimeOffset.UtcNow,
                new TesterAttributesDto(errorType, errorMessage + " " + Guid.NewGuid().ToString("N"), route)),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<IngestSignalResponseDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body;
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

        var result = await command.ExecuteScalarAsync();
        return result is DBNull ? default! : (T)result!;
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
}


