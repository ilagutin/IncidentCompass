using System.Net;
using System.Net.Http.Json;
using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.ModelGateway;
using IncidentCompass.Application.Investigation.Jobs;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// Covers what <c>GET /api/v1/faults/{id}</c> tells a caller about a job that is waiting: the
/// durable classification code and the scheduled retry time, and nothing the provider authored.
/// </summary>
[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class FaultDelayReasonEndpointTests(PostgresRepositoryFixture postgres)
{
    [DockerAvailableFact]
    public async Task GetFaultById_DelayedByProviderOutage_ReturnsDurableCodeAndNextAttemptTime()
    {
        using var scope = await CreateScopeAsync(useOutageModelClient: true);
        var ingested = await PostIngestAsync(scope.Client);
        await ProcessOnceAsync(scope.Factory, "worker-delay-reason", ingested.JobId!.Value);

        var fault = await GetFaultAsync(scope.Client, ingested.FaultId);

        Assert.NotNull(fault.Job);
        Assert.Equal(ingested.JobId!.Value, fault.Job.Id);
        // A delayed job is delayed, not failed: the status stays non-terminal and the fault itself
        // is still open, so the populated code reads as "why it is waiting", not "how it ended".
        Assert.Equal("RetryPending", fault.Job.Status);
        Assert.NotEqual("Failed", fault.Status);
        Assert.Equal("provider_unavailable", fault.Job.LastErrorCode);
        Assert.NotNull(fault.Job.NextAttemptAtUtc);
        Assert.True(fault.Job.NextAttemptAtUtc > fault.Job.CreatedAtUtc);
        // The outage path deliberately does not spend the attempt budget.
        Assert.Equal(1, fault.Job.Attempt);
    }

    [DockerAvailableFact]
    public async Task GetFaultById_HealthyJob_CarriesNoReason()
    {
        using var scope = await CreateScopeAsync(useOutageModelClient: false);
        var ingested = await PostIngestAsync(scope.Client);

        var queued = await GetFaultAsync(scope.Client, ingested.FaultId);
        Assert.NotNull(queued.Job);
        Assert.Equal("Pending", queued.Job.Status);
        Assert.Null(queued.Job.LastErrorCode);
        Assert.Null(queued.Job.NextAttemptAtUtc);

        await ProcessOnceAsync(scope.Factory, "worker-healthy-reason", ingested.JobId!.Value);

        var succeeded = await GetFaultAsync(scope.Client, ingested.FaultId);
        Assert.NotNull(succeeded.Job);
        Assert.Equal("Succeeded", succeeded.Job.Status);
        Assert.Null(succeeded.Job.LastErrorCode);
        Assert.Null(succeeded.Job.NextAttemptAtUtc);
    }

    [DockerAvailableFact]
    public async Task GetFaultById_DelayedByProviderOutage_DoesNotEchoProviderSuppliedText()
    {
        using var scope = await CreateScopeAsync(useOutageModelClient: true);
        var ingested = await PostIngestAsync(scope.Client);
        await ProcessOnceAsync(scope.Factory, "worker-no-provider-text", ingested.JobId!.Value);

        var response = await scope.Client.GetAsync(
            $"/api/v1/faults/{ingested.FaultId}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Everything the injected provider authored, plus the exception type name that the durable
        // last_error_message column would have carried had it been projected.
        string[] forbidden =
        [
            OutageModelClient.ProviderName,
            OutageModelClient.ProviderMessage,
            OutageModelClient.ProviderEndpoint,
            OutageModelClient.ProviderSecret,
            OutageModelClient.ProviderAuthoredErrorCode,
            OutageModelClient.RawProviderErrorCode,
            nameof(AiModelException),
            "ServiceUnavailable",
            "Triage delayed"
        ];
        foreach (var fragment in forbidden)
        {
            Assert.DoesNotContain(fragment, body, StringComparison.OrdinalIgnoreCase);
        }

        // The response is not merely empty: it still carries the application-owned classification.
        Assert.Contains("provider_unavailable", body, StringComparison.Ordinal);
    }

    private async Task<TestScope> CreateScopeAsync(bool useOutageModelClient)
    {
        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        await PostgresTriageJobTestIsolation.CompleteClaimableJobsAsync(connectionString);

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:IncidentCompass", connectionString);
            builder.UseExplicitMockProviders();
            builder.UseSetting("IncidentCompass:ProviderResilience:FailureThreshold", "1");
            builder.UseSetting("IncidentCompass:ProviderResilience:BackpressureSeconds", "30");
            if (useOutageModelClient)
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IAiModelClient>();
                    services.AddScoped<IAiModelClient, OutageModelClient>();
                });
            }
        });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        return new TestScope(factory, client);
    }

    private static async Task ProcessOnceAsync(
        WebApplicationFactory<Program> factory,
        string workerId,
        Guid expectedJobId)
    {
        using var serviceScope = factory.Services.CreateScope();
        var runner = serviceScope.ServiceProvider.GetRequiredService<ITriageJobRunner>();
        var claimed = await runner.ClaimNextAsync(
            workerId,
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);
        Assert.NotNull(claimed);
        Assert.Equal(expectedJobId, claimed.Id);
        await runner.ProcessClaimedAsync(
            claimed,
            workerId,
            new TriageJobProcessingSettings(MaxAttempts: 3, RetryDelay: TimeSpan.FromMinutes(5)),
            TestContext.Current.CancellationToken);
    }

    private static async Task<IngestionFaultDetails> GetFaultAsync(HttpClient client, Guid faultId)
    {
        var response = await client.GetAsync($"/api/v1/faults/{faultId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<IngestionFaultDetails>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body;
    }

    private static async Task<IngestedFault> PostIngestAsync(HttpClient client)
    {
        var unique = IngestFingerprintUniqueness.Token();
        var response = await client.PostAsJsonAsync(
            "/api/v1/incidents",
            new EnvelopeDto(
                "tester",
                "delay-reason-svc-" + unique,
                "prod",
                DateTimeOffset.UtcNow,
                new AttributesDto("TimeoutException", "delay reason probe " + unique, "/delay-reason")),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<IngestedFault>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.NotNull(body.JobId);
        return body;
    }

    /// <summary>
    /// A model client whose failure carries every kind of provider-authored text the response must
    /// never repeat: a provider name, a free-text message with an endpoint and a credential in it,
    /// an adapter-shaped error code and a raw upstream error code.
    /// </summary>
    private sealed class OutageModelClient : IAiModelClient
    {
        public const string ProviderName = "leakyprovider";
        public const string ProviderEndpoint = "https://models.leaky.invalid/v1/chat/completions";
        public const string ProviderSecret = "sk-leakedkeymaterial";
        public const string ProviderMessage =
            "Upstream refused " + ProviderEndpoint + " for key " + ProviderSecret + ".";
        public const string ProviderAuthoredErrorCode = "leaked_adapter_error_code";
        public const string RawProviderErrorCode = "leaked_upstream_error_code";

        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            throw new AiModelException(
                ProviderName,
                ProviderMessage,
                ProviderAuthoredErrorCode,
                HttpStatusCode.ServiceUnavailable,
                RawProviderErrorCode,
                failureKind: ProviderFailureKind.Unavailable);
        }
    }

    private sealed record TestScope(WebApplicationFactory<Program> Factory, HttpClient Client) : IDisposable
    {
        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
        }
    }

    private sealed record AttributesDto(string ErrorType, string ErrorMessage, string HttpRoute);

    private sealed record EnvelopeDto(
        string SourceKind,
        string ServiceName,
        string Environment,
        DateTimeOffset ObservedAtUtc,
        AttributesDto Attributes);

    private sealed record IngestedFault(Guid SignalId, Guid FaultId, Guid? JobId, string? ConfigHash);
}
