using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Intake.FaultGrouping;
using IncidentCompass.Domain.Incidents;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// the API error boundary must map NotFoundException/ConflictException/
/// ForbiddenRequestException/ValidationException to a stable code and an authored, client-safe
/// detail, and must never echo the exception's own developer-facing message. Each test drives a
/// real HTTP request through a mapped exception type and asserts on the response body only.
/// </summary>
public sealed class ApiErrorResponseTests
{
    private const string OperatorKey = "api_error_response_operator_key_abcdefghijklmnopqrstuvwx";

    [Fact]
    public async Task NotFound_FaultLookup_ReturnsStableCodeAndAuthoredDetailWithoutExceptionText()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureLogging(static logging => logging.ClearProviders());
            builder.UseExplicitMockProviders();
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IFaultRepository>();
                services.AddSingleton<IFaultRepository>(new MissingFaultRepository());
            });
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        var faultId = Guid.NewGuid();

        var response = await client.GetAsync($"/api/v1/faults/{faultId}", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("\"errorCode\":\"fault_not_found\"", body, StringComparison.Ordinal);
        Assert.Contains("The requested fault does not exist.", body, StringComparison.Ordinal);
        Assert.DoesNotContain(faultId.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("was not found", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Conflict_ActionApprovalDecision_ReturnsStableCodeAndAuthoredDetailWithoutExceptionText()
    {
        var repository = new ConflictingReviewRepository("stale_state");
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureLogging(static logging => logging.ClearProviders());
            builder.UseExplicitMockProviders();
            builder.UseSetting("IncidentCompass:ApiKeyAuth:Enabled", "true");
            builder.UseSetting("IncidentCompass:ApiKeyAuth:PermitLimit", "100");
            builder.UseSetting("IncidentCompass:ApiKeyAuth:WindowSeconds", "300");
            builder.UseSetting("IncidentCompass:ApiKeyAuth:Credentials:0:KeyId", "operator-error-response");
            builder.UseSetting("IncidentCompass:ApiKeyAuth:Credentials:0:TenantId", "tenant-error-response");
            builder.UseSetting("IncidentCompass:ApiKeyAuth:Credentials:0:Sha256Digest", Digest(OperatorKey));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IActionApprovalReviewRepository>();
                services.AddSingleton<IActionApprovalReviewRepository>(repository);
            });
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/action-approvals/{Guid.NewGuid()}/approve")
        {
            Content = JsonContent.Create(new { payloadSha256 = new string('a', 64), approvalSha256 = new string('b', 64) })
        };
        request.Headers.Add("X-IncidentCompass-Key", OperatorKey);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("\"errorCode\":\"action_approval_conflict_stale_state\"", body, StringComparison.Ordinal);
        Assert.Contains("The action approval is no longer in the expected state.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Action approval conflict:", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forbidden_CostRollupsWithoutAuthenticatedTenant_ReturnsStableCodeAndAuthoredDetailWithoutExceptionText()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // Explicit demo-auth opt-in without a demo user header authenticates as anonymous
            // (see ApiV1EndpointTests.DemoAuth_NonProductionWithExplicitOptInWithoutUserHeaderIsAnonymous),
            // which is what drives CostRollupQueryHandler's "no authenticated tenant" branch.
            builder.UseEnvironment("Staging");
            builder.ConfigureLogging(static logging => logging.ClearProviders());
            builder.UseExplicitMockProviders();
            builder.UseSetting("IncidentCompass:DemoAuth:Enabled", "true");
            builder.UseSetting("IncidentCompass:DemoAuth:AllowInNonDevelopment", "true");
            builder.ConfigureTestServices(static services => services.RemoveAll<IHostedService>());
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        var response = await client.GetAsync(
            "/api/v1/observability/cost-rollups?fromUtc=2026-01-01T00:00:00Z&toUtc=2026-01-02T00:00:00Z",
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("\"errorCode\":\"tenant_context_required\"", body, StringComparison.Ordinal);
        Assert.Contains("Authentication did not resolve a tenant for this request.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Authenticated tenant context is required.", body, StringComparison.Ordinal);
    }

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(value)));

    private sealed class MissingFaultRepository : IFaultRepository
    {
        public Task<Fault?> FindOpenFaultAsync(
            string tenantId, string serviceName, string environment, string fingerprint,
            int fingerprintVersion, string groupingRuleId, int groupingRuleVersion,
            CancellationToken cancellationToken) => Task.FromResult<Fault?>(null);

        public Task<Fault?> FindMostRecentClosedFaultAsync(
            string tenantId, string serviceName, string environment, string fingerprint,
            int fingerprintVersion, string groupingRuleId, int groupingRuleVersion,
            CancellationToken cancellationToken) => Task.FromResult<Fault?>(null);

        public Task<Fault?> TryInsertAsync(Fault fault, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not needed for this test.");

        public Task<Fault?> FindByIdForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not needed for this test.");

        public Task<Fault?> FindByIdAsync(Guid id, string tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<Fault?>(null);
    }

    private sealed class ConflictingReviewRepository(string conflictCode) : IActionApprovalReviewRepository
    {
        public Task<IReadOnlyList<ActionApprovalRecord>> ListAsync(
            ActionApprovalListFilter filter, string tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ActionApprovalRecord>>([]);

        public Task<(ActionApprovalRecord Action, IReadOnlyList<ActionApprovalProvenance> Provenance)?> FindAsync(
            Guid actionId, string tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<(ActionApprovalRecord, IReadOnlyList<ActionApprovalProvenance>)?>(null);

        public Task<ActionDecisionResult> DecideAsync(
            ActionDecisionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ActionDecisionResult(ActionDecisionOutcome.Conflict, null, conflictCode));
    }
}
