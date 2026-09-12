using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Investigation.Jobs;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// A host composed with a model client that breaks the <see cref="IAiModelClient" /> contract -
/// raising a raw transport exception instead of the normalized provider exception the port requires.
/// The point is what the Worker makes of it. The attempt always failed in an ordinary way on this
/// path, because the job runner catches anything the processor raises; what it failed with was an
/// unclassified exception and no <c>ModelCall</c> row at all, so the disposition was decided without
/// a failure kind and the call left no trace. What is asserted here is the part that was missing: a
/// durable, classified row naming the defect, and a job whose error code says which defect it was
/// rather than the generic <c>triage_job_attempt_failed</c>. The row is a record of the call, not of
/// its cost, which is asserted here too because the difference is easy to overstate.
/// </summary>
/// <remarks>
/// This substitutes the registration rather than decorating it, which is the case the enforcement
/// exists for: it is how a test double and a future adapter reach the governed caller, so a
/// composition-time wrapper around the shipped selection would never see them.
/// </remarks>
[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class ModelClientContractBreachTests(PostgresRepositoryFixture postgres)
{
    private const string WorkerId = "worker-contract-breach";

    [DockerAvailableFact]
    public async Task ContractViolatingModelClient_DeadLettersWithAnAccountedClassifiedFailure()
    {
        using var scope = await CreateScopeAsync();
        var ingested = await PostIngestAsync(scope.Client);

        using var serviceScope = scope.Factory.Services.CreateScope();
        var runner = serviceScope.ServiceProvider.GetRequiredService<ITriageJobRunner>();
        var claimed = await runner.ClaimNextAsync(
            WorkerId,
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);
        Assert.NotNull(claimed);
        Assert.Equal(ingested.JobId, claimed.Id);

        // Before the caller enforced the port contract this raw HttpRequestException reached the
        // runner unwrapped. The runner still caught it and still dead-lettered the attempt, so the
        // change here is not that the job survives: it is that the attempt now carries a failure
        // kind and a durable row for the failed call, where before it carried neither.
        await runner.ProcessClaimedAsync(
            claimed,
            WorkerId,
            new TriageJobProcessingSettings(MaxAttempts: 1, RetryDelay: TimeSpan.FromSeconds(1)),
            TestContext.Current.CancellationToken);

        var modelCall = Assert.Single(
            await ReadModelCallsAsync(scope.ConnectionString, claimed.Id, claimed.Attempt));
        Assert.Equal("failed", modelCall.Outcome);
        Assert.Equal("provider_contract_violation", modelCall.ErrorCode);

        // What the row can still say: which call this was. Those are the backend's own facts about
        // the request it sent, and they survive the adapter misbehaving.
        Assert.Equal("report-chat", modelCall.RouteId);
        Assert.Equal("local-model", modelCall.Model);
        Assert.Equal("local-oai", modelCall.ProviderId);

        // What it cannot: anything that would have come back. No answering adapter, no usage, and
        // consequently no BudgetEvent and no tokens_delta - the call is recorded, its spend is not
        // recovered, and this system does not invent a number to fill the gap.
        Assert.Equal("unknown", modelCall.Provider);
        Assert.Equal("unknown", modelCall.UsageSource);
        Assert.Null(modelCall.TotalTokens);
        Assert.Equal(
            0,
            await CountBudgetEventsAsync(scope.ConnectionString, claimed.Id, claimed.Attempt));

        var job = await ReadJobAsync(scope.ConnectionString, claimed.Id);
        Assert.Equal("DeadLettered", job.Status);
        Assert.Equal("provider_contract_violation", job.LastErrorCode);

        // The stored message is the closed code plus an exception type, and nothing the offending
        // adapter authored: a raw transport failure can carry an endpoint, a host name or a
        // credential in its text, and this row is readable through the API.
        Assert.NotNull(job.LastErrorMessage);
        Assert.StartsWith("provider_contract_violation:", job.LastErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Connection reset", job.LastErrorMessage, StringComparison.Ordinal);
    }

    private async Task<TestScope> CreateScopeAsync()
    {
        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        await PostgresTriageJobTestIsolation.CompleteClaimableJobsAsync(connectionString);
        var configPath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "test-triage-config",
            "incidentcompass.config.json");
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:IncidentCompass", connectionString);
            builder.UseExplicitMockProviders();
            builder.UseSetting("IncidentCompass:ConfigSource:Path", configPath);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAiModelClient>();
                services.AddScoped<IAiModelClient, ContractViolatingModelClient>();
            });
        });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        return new TestScope(factory, client, connectionString);
    }

    private static async Task<IngestSignalResponseDto> PostIngestAsync(HttpClient client)
    {
        var unique = Guid.NewGuid().ToString("N");
        var response = await client.PostAsJsonAsync(
            "/api/v1/incidents",
            new TesterEnvelopeDto(
                "tester",
                "contract-breach-" + unique,
                "prod",
                DateTimeOffset.UtcNow,
                new TesterAttributesDto("TimeoutException", "contract breach probe " + unique, "/breach")),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<IngestSignalResponseDto>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.NotNull(body.JobId);
        return body;
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

    private static async Task<long> CountBudgetEventsAsync(
        string connectionString,
        Guid jobId,
        int attempt)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT COUNT(*)
            FROM incidentcompass.triage_ledger
            WHERE job_id = @job_id AND attempt = @attempt AND event_type = 'BudgetEvent';
            """, connection);
        command.Parameters.AddWithValue("job_id", jobId);
        command.Parameters.AddWithValue("attempt", attempt);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<JobRow> ReadJobAsync(string connectionString, Guid jobId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT status, last_error_code, last_error_message
            FROM incidentcompass.triage_jobs
            WHERE id = @job_id;
            """, connection);
        command.Parameters.AddWithValue("job_id", jobId);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        return new JobRow(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    /// <summary>
    /// An adapter that forgot one catch around its transport, which is the realistic shape of this
    /// defect. It normalizes nothing, so what leaves it is a raw transport exception.
    /// </summary>
    private sealed class ContractViolatingModelClient : IAiModelClient
    {
        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            throw new HttpRequestException("Connection reset by peer.");
        }
    }

    private sealed record TestScope(
        WebApplicationFactory<Program> Factory,
        HttpClient Client,
        string ConnectionString) : IDisposable
    {
        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
        }
    }

    private sealed record JobRow(string Status, string? LastErrorCode, string? LastErrorMessage);

    private sealed record TesterAttributesDto(string ErrorType, string ErrorMessage, string HttpRoute);

    private sealed record TesterEnvelopeDto(
        string SourceKind,
        string ServiceName,
        string Environment,
        DateTimeOffset ObservedAtUtc,
        TesterAttributesDto Attributes);

    private sealed record IngestSignalResponseDto(Guid SignalId, Guid FaultId, Guid? JobId, string? ConfigHash);
}
