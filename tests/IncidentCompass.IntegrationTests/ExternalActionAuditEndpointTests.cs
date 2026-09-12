using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Domain.Incidents.Actions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class ExternalActionAuditEndpointTests(PostgresRepositoryFixture postgres)
{
    private const string OperatorKey = "projection_secret_sentinel_abcdefghijklmnopqrstuvwxyz123456";

    [DockerAvailableFact]
    public async Task ExactPairFilterIsIndexedTenantScopedAndExposesOnlySafeProjection()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var tenantA = await ActionApprovalTestSupport.SeedOriginAsync(database.ConnectionString, "tenant-a");
        var tenantB = await ActionApprovalTestSupport.SeedOriginAsync(database.ConnectionString, "tenant-b");
        var actionA = await CompleteAsync(database.ConnectionString, tenantA, "42", "tenant-a-projection");
        var unknownAction = await CompleteFailureAsync(
            database.ConnectionString, tenantA, "tenant-a-outcome-unknown");
        var actionB = await CompleteAsync(database.ConnectionString, tenantB, "84", "tenant-b-projection");
        using (var repositoryServices = ActionApprovalTestSupport.CreateServices(database.ConnectionString))
        {
            var rows = await repositoryServices.GetRequiredService<IActionApprovalReviewRepository>().ListAsync(
                new ActionApprovalListFilter(null, null, null, 51, "github_issue", "42"),
                tenantA.TenantId,
                TestContext.Current.CancellationToken);
            Assert.Single(rows);
            Assert.Equal(actionA, rows[0].Id);
        }

        var capturedLogs = new List<string>();
        using var factory = CreateFactory(database.ConnectionString, capturedLogs);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        using var exact = await SendAsync(
            client,
            "/api/v1/action-approvals?externalResourceKind=github_issue&externalResourceId=42");
        var exactBody = await exact.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var foreign = await SendAsync(
            client,
            "/api/v1/action-approvals?externalResourceKind=github_issue&externalResourceId=84");
        var foreignBody = await foreign.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var absent = await SendAsync(
            client,
            "/api/v1/action-approvals?externalResourceKind=github_issue&externalResourceId=999");
        var absentBody = await absent.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var detail = await SendAsync(client, $"/api/v1/action-approvals/{actionA}");
        var detailBody = await detail.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var unknownDetail = await SendAsync(client, $"/api/v1/action-approvals/{unknownAction}");
        var unknownBody = await unknownDetail.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.True(exact.IsSuccessStatusCode, exactBody);
        Assert.Contains(actionA.ToString(), exactBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(actionB.ToString(), exactBody, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, foreign.StatusCode);
        Assert.Equal(foreignBody, absentBody);
        Assert.DoesNotContain(actionB.ToString(), foreignBody, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        AssertSafeProjection(detailBody, "42");
        Assert.Equal(HttpStatusCode.OK, unknownDetail.StatusCode);
        AssertSafeUnknownOutcome(unknownBody);
        var publicAndLogText = exactBody + foreignBody + absentBody + detailBody + unknownBody +
            string.Join('\n', capturedLogs);
        Assert.DoesNotContain(OperatorKey, publicAndLogText, StringComparison.Ordinal);
        Assert.Equal(0, await CountSecretSentinelAsync(database.ConnectionString));
        Assert.Equal(1, await ActionApprovalTestSupport.CountAsync(database.ConnectionString, """
            SELECT count(*)
            FROM incidentcompass.triage_artifacts
            WHERE kind = 'ActionResult' AND domain_ref = @ref
              AND redacted_payload->>'failureCode' = 'dispatch_outcome_unknown';
            """, ("ref", "action:" + unknownAction)));
        Assert.Equal(1, await ActionApprovalTestSupport.CountAsync(database.ConnectionString, """
            SELECT count(*)
            FROM incidentcompass.triage_ledger
            WHERE event_type = 'ActionCompleted' AND tool_status = 'Failed'
              AND payload_ref IN (
                  SELECT 'artifact:' || id::text
                  FROM incidentcompass.triage_artifacts
                  WHERE domain_ref = @ref);
            """, ("ref", "action:" + unknownAction)));

        foreach (var invalidRoute in new[]
                 {
                     "/api/v1/action-approvals?externalResourceKind=github_issue",
                     "/api/v1/action-approvals?externalResourceId=42",
                     "/api/v1/action-approvals?externalResourceKind=github_issue&externalResourceId=0",
                     "/api/v1/action-approvals?externalResourceKind=unknown&externalResourceId=42"
                 })
        {
            using var invalid = await SendAsync(client, invalidRoute);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }

        var plan = await ReadQueryPlanAsync(database.ConnectionString);
        Assert.Contains("ix_action_approvals_external_resource", plan, StringComparison.Ordinal);
    }

    /// <summary>
    /// The inverse direction of the exact pair lookup above: from an incident to every external
    /// resource its governed actions touched. The two together are what makes a pivot possible - an
    /// operator holding a GitHub issue number reaches the fault through <c>faultId</c> on the item,
    /// and the fault reaches the rest of that incident's external footprint through this filter.
    /// </summary>
    /// <remarks>
    /// Tenant scoping here is not a second mechanism. The fault predicate carries no tenant term of
    /// its own; it composes with the tenant predicate that already scopes every shape of this query,
    /// which is why a fault id belonging to another tenant has to return nothing rather than a
    /// filtered subset. The assertion that it returns byte-identical output to a fault id that does
    /// not exist at all is the point: an operator cannot use this to learn that another tenant's
    /// fault is real.
    /// </remarks>
    [DockerAvailableFact]
    public async Task FaultFilterCorrelatesOneIncidentsExternalResourcesAndStaysTenantScoped()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var incident = await ActionApprovalTestSupport.SeedOriginAsync(database.ConnectionString, "tenant-a");
        var otherIncident = await ActionApprovalTestSupport.SeedOriginAsync(database.ConnectionString, "tenant-a");
        var foreignIncident = await ActionApprovalTestSupport.SeedOriginAsync(database.ConnectionString, "tenant-b");
        var firstIssue = await CompleteAsync(database.ConnectionString, incident, "42", "correlation-first");
        var secondIssue = await CompleteAsync(database.ConnectionString, incident, "43", "correlation-second");
        var unrelatedIssue = await CompleteAsync(
            database.ConnectionString, otherIncident, "77", "correlation-unrelated");
        var foreignIssue = await CompleteAsync(
            database.ConnectionString, foreignIncident, "84", "correlation-foreign");

        var capturedLogs = new List<string>();
        using var factory = CreateFactory(database.ConnectionString, capturedLogs);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        using var correlated = await SendAsync(client, $"/api/v1/action-approvals?faultId={incident.FaultId}");
        var correlatedBody = await correlated.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var foreignFault = await SendAsync(
            client, $"/api/v1/action-approvals?faultId={foreignIncident.FaultId}");
        var foreignBody = await foreignFault.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var absentFault = await SendAsync(client, $"/api/v1/action-approvals?faultId={Guid.NewGuid()}");
        var absentBody = await absentFault.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var pivot = await SendAsync(
            client, "/api/v1/action-approvals?externalResourceKind=github_issue&externalResourceId=42");
        var pivotBody = await pivot.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.True(correlated.IsSuccessStatusCode, correlatedBody);
        Assert.Equal(
            new[] { firstIssue, secondIssue }.Order().ToArray(),
            ReadActionIds(correlatedBody).Order().ToArray());
        Assert.Equal(
            ["42", "43"],
            ReadValues(correlatedBody, "externalResourceId").Order(StringComparer.Ordinal).ToArray());
        Assert.All(
            ReadValues(correlatedBody, "faultId"),
            value => Assert.Equal(incident.FaultId.ToString(), value));
        Assert.DoesNotContain(unrelatedIssue.ToString(), correlatedBody, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.OK, foreignFault.StatusCode);
        Assert.Empty(ReadActionIds(foreignBody));
        Assert.Equal(absentBody, foreignBody);
        Assert.DoesNotContain(foreignIssue.ToString(), foreignBody, StringComparison.OrdinalIgnoreCase);

        // The pivot the two directions exist to support, and the reason `faultId` is on the item.
        Assert.Equal([incident.FaultId.ToString()], ReadValues(pivotBody, "faultId").ToArray());

        // `result_payload` is the raw provider body. Neither direction may render it, and the
        // provider-shaped keys inside it are the cheapest sentinel for that.
        var publicText = correlatedBody + foreignBody + absentBody + pivotBody;
        Assert.DoesNotContain("issueNumber", publicText, StringComparison.Ordinal);
        Assert.DoesNotContain("\"provider\"", publicText, StringComparison.Ordinal);
        Assert.DoesNotContain(OperatorKey, publicText + string.Join('\n', capturedLogs), StringComparison.Ordinal);

        var plan = await ReadFaultQueryPlanAsync(database.ConnectionString, incident.FaultId);
        Assert.Contains("ix_action_approvals_tenant_fault", plan, StringComparison.Ordinal);
    }

    private static Guid[] ReadActionIds(string body) =>
        ReadValues(body, "id").Select(Guid.Parse).ToArray();

    private static string[] ReadValues(string body, string propertyName)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("actions").EnumerateArray()
            .Select(item => item.GetProperty(propertyName).GetString()!)
            .ToArray();
    }

    private static Task<Guid> CompleteAsync(
        string connectionString,
        ActionApprovalOriginFixture origin,
        string issueNumber,
        string proposalKey) =>
        ActionApprovalTestSupport.CompleteGitHubIssueAsync(
            connectionString, origin, issueNumber, proposalKey, "endpoint-projection-worker");

    private static async Task<Guid> CompleteFailureAsync(
        string connectionString,
        ActionApprovalOriginFixture origin,
        string proposalKey)
    {
        using var services = ActionApprovalTestSupport.CreateServices(connectionString);
        var action = (await services.GetRequiredService<IActionProposalRepository>().CreateAsync(
            ActionApprovalTestSupport.Proposal(origin, proposalKey, automaticallyApproved: true),
            TestContext.Current.CancellationToken)).Action;
        var claim = await services.GetRequiredService<IActionDispatchRepository>().TryClaimAsync(
            action.Id, "endpoint-unknown-worker", TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken);
        Assert.NotNull(claim);
        Assert.True(await services.GetRequiredService<IActionDispatchRepository>().CompleteAsync(
            new ActionTerminalRequest(
                action.Id,
                claim!.Fence,
                ActionApprovalState.Failed,
                Encoding.UTF8.GetBytes("{\"code\":\"dispatch_outcome_unknown\"}"),
                "The external action outcome is unknown.",
                "dispatch_outcome_unknown"),
            TestContext.Current.CancellationToken));
        return action.Id;
    }

    private static WebApplicationFactory<Program> CreateFactory(
        string connectionString,
        List<string> capturedLogs) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(new CapturingLoggerProvider(capturedLogs));
            });
            builder.UseExplicitMockProviders();
            builder.UseSetting("ConnectionStrings:ActionApprovalTests", connectionString);
            builder.UseSetting("IncidentCompass:Postgres:ConnectionStringName", "ActionApprovalTests");
            builder.UseSetting("IncidentCompass:ApiKeyAuth:Enabled", "true");
            builder.UseSetting("IncidentCompass:ApiKeyAuth:PermitLimit", "100");
            builder.UseSetting("IncidentCompass:ApiKeyAuth:WindowSeconds", "300");
            builder.UseSetting("IncidentCompass:ApiKeyAuth:Credentials:0:KeyId", "projection-operator");
            builder.UseSetting("IncidentCompass:ApiKeyAuth:Credentials:0:TenantId", "tenant-a");
            builder.UseSetting(
                "IncidentCompass:ApiKeyAuth:Credentials:0:Sha256Digest",
                Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(OperatorKey))));
            builder.ConfigureTestServices(static services => services.RemoveAll<IHostedService>());
        });

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string route)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.Add("X-IncidentCompass-Key", OperatorKey);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static void AssertSafeProjection(string body, string issueNumber)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal("github_issue", root.GetProperty("externalResourceKind").GetString());
        Assert.Equal(issueNumber, root.GetProperty("externalResourceId").GetString());
        Assert.Equal("absent", root.GetProperty("externalBeforeState").GetString());
        Assert.Equal("open", root.GetProperty("externalAfterState").GetString());
        Assert.DoesNotContain("test-token", body, StringComparison.Ordinal);
        Assert.DoesNotContain("api.github.com", body, StringComparison.Ordinal);
        Assert.DoesNotContain("owner/repo", body, StringComparison.Ordinal);
    }

    private static void AssertSafeUnknownOutcome(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal("failed", root.GetProperty("status").GetString());
        Assert.Equal("dispatch_outcome_unknown", root.GetProperty("failureCode").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("externalResourceKind").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("externalResourceId").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("externalBeforeState").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("externalAfterState").ValueKind);
    }

    private static async Task<string> ReadQueryPlanAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("""
            SET enable_seqscan = off;
            EXPLAIN (COSTS OFF)
            SELECT id
            FROM incidentcompass.action_approvals
            WHERE tenant_id = 'tenant-a'
              AND external_resource_kind = 'github_issue'
              AND external_resource_id = '42'
            ORDER BY completed_at_utc DESC, id DESC
            LIMIT 51;
            """, connection);
        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            lines.Add(reader.GetString(0));
        }

        return string.Join('\n', lines);
    }

    private static async Task<string> ReadFaultQueryPlanAsync(string connectionString, Guid faultId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("""
            SET enable_seqscan = off;
            EXPLAIN (COSTS OFF)
            SELECT id
            FROM incidentcompass.action_approvals
            WHERE tenant_id = 'tenant-a'
              AND fault_id = @fault_id
            ORDER BY created_at_utc DESC, id DESC
            LIMIT 51;
            """, connection);
        command.Parameters.AddWithValue("fault_id", faultId);
        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            lines.Add(reader.GetString(0));
        }

        return string.Join('\n', lines);
    }

    private static Task<long> CountSecretSentinelAsync(string connectionString) =>
        ActionApprovalTestSupport.CountAsync(connectionString, """
            SELECT
                (SELECT count(*)
                 FROM incidentcompass.action_approvals
                 WHERE coalesce(convert_from(result_payload, 'UTF8'), '') LIKE '%' || @sentinel || '%'
                    OR coalesce(result_summary, '') LIKE '%' || @sentinel || '%'
                    OR coalesce(failure_code, '') LIKE '%' || @sentinel || '%'
                    OR coalesce(external_resource_kind, '') LIKE '%' || @sentinel || '%'
                    OR coalesce(external_resource_id, '') LIKE '%' || @sentinel || '%'
                    OR coalesce(external_before_state, '') LIKE '%' || @sentinel || '%'
                    OR coalesce(external_after_state, '') LIKE '%' || @sentinel || '%')
              + (SELECT count(*)
                 FROM incidentcompass.triage_ledger
                 WHERE coalesce(role, '') LIKE '%' || @sentinel || '%'
                    OR coalesce(rationale, '') LIKE '%' || @sentinel || '%'
                    OR coalesce(payload_ref, '') LIKE '%' || @sentinel || '%');
            """, ("sentinel", OperatorKey));

    private sealed class CapturingLoggerProvider(List<string> messages) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(messages);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(List<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (messages)
            {
                messages.Add(formatter(state, exception));
                if (exception is not null)
                {
                    messages.Add(exception.ToString());
                }
            }
        }
    }
}
