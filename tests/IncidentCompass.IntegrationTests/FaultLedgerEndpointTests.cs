using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class FaultLedgerEndpointTests(PostgresRepositoryFixture postgres)
{
    [DockerAvailableFact]
    public async Task GetFaultLedgerByFaultId_ReturnsDbOrderedGovernanceEvents()
    {
        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        await PostgresTriageJobTestIsolation.CompleteClaimableJobsAsync(connectionString);
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:IncidentCompass", connectionString);
            builder.UseExplicitMockProviders();
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        var ingested = await PostIngestAsync(client);
        await InsertLedgerEventAsync(
            connectionString,
            ingested,
            "ToolProposed",
            role: "memory",
            toolName: "memory_search",
            decision: null,
            rationale: "memory worker proposed a search",
            payloadRef: "tool-call:one");
        await InsertLedgerEventAsync(
            connectionString,
            ingested,
            "PolicyDecision",
            role: "memory",
            toolName: "memory_search",
            decision: "Allowed",
            rationale: "role grant and rate cap allow the call",
            payloadRef: null);
        var artifactId = await InsertArtifactAsync(connectionString, ingested);
        await InsertLedgerEventAsync(
            connectionString,
            ingested,
            "ToolResult",
            role: "memory",
            toolName: "memory_search",
            decision: null,
            rationale: "memory search returned no matches",
            payloadRef: "artifact:" + artifactId,
            toolStatus: "Succeeded");

        var response = await client.GetAsync($"/api/v1/faults/{ingested.FaultId}/ledger", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<FaultLedgerResponseDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal(ingested.FaultId, body.FaultId);
        Assert.Equal(["ToolProposed", "PolicyDecision", "ToolResult"], body.Events.Select(static item => item.EventType));
        Assert.Equal("memory", body.Events[0].Role);
        Assert.Equal("memory_search", body.Events[1].ToolName);
        Assert.Equal("Allowed", body.Events[1].Decision);
        Assert.Equal("memory search returned no matches", body.Events[2].Rationale);
        Assert.Equal("artifact:" + artifactId, body.Events[2].PayloadRef);
        Assert.Equal(ingested.ConfigHash, body.Events[2].ConfigHash);

        // The wire contract for the payload state. `Reaped` is exercised end to end against a real
        // retention run in RetentionLifecycleTests; what this covers is that the field reaches an
        // API caller at all, and that a live payload and an event with no payload are distinguished
        // rather than both rendering as "there is something over there".
        Assert.Equal("NotReapable", body.Events[0].PayloadState);
        Assert.Equal("None", body.Events[1].PayloadState);
        Assert.Equal("Retained", body.Events[2].PayloadState);
    }

    private static async Task<IngestSignalResponseDto> PostIngestAsync(HttpClient client)
    {
        var unique = IngestFingerprintUniqueness.Token();
        var response = await client.PostAsJsonAsync(
            "/api/v1/incidents",
            new TesterEnvelopeDto(
                "tester",
                "fault-ledger-svc-" + unique,
                "prod",
                "warning",
                DateTimeOffset.UtcNow,
                new TesterAttributesDto("TimeoutException", "ledger endpoint timeout " + unique, "/ledger")),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<IngestSignalResponseDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.NotNull(body.JobId);
        Assert.NotNull(body.ConfigHash);
        return body;
    }

    private static async Task InsertLedgerEventAsync(
        string connectionString,
        IngestSignalResponseDto ingested,
        string eventType,
        string role,
        string toolName,
        string? decision,
        string rationale,
        string? payloadRef,
        string? toolStatus = null)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO incidentcompass.triage_ledger (
                fault_id, job_id, attempt, event_type, role, tool_name, rationale,
                decision, decision_reason, tool_status, tokens_delta, workers_delta,
                payload_ref, config_hash, created_at_utc)
            VALUES (
                @fault_id, @job_id, 1, @event_type, @role, @tool_name, @rationale,
                @decision, NULL, @tool_status, NULL, NULL,
                @payload_ref, @config_hash, now());
            """, connection);
        command.Parameters.AddWithValue("fault_id", ingested.FaultId);
        command.Parameters.AddWithValue("job_id", ingested.JobId!.Value);
        command.Parameters.AddWithValue("event_type", eventType);
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("tool_name", toolName);
        command.Parameters.AddWithValue("rationale", rationale);
        command.Parameters.AddWithValue("decision", decision ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("tool_status", toolStatus ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("payload_ref", payloadRef ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("config_hash", ingested.ConfigHash!);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<Guid> InsertArtifactAsync(
        string connectionString,
        IngestSignalResponseDto ingested)
    {
        var artifactId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO incidentcompass.triage_artifacts (
                id, job_id, attempt, kind, domain_ref, redacted_payload, content_hash, created_at_utc)
            VALUES (@id, @job_id, 1, 'ToolResult', 'tool:memory_search',
                    '{"summary":"redacted tool output"}'::jsonb, @content_hash, now());
            """, connection);
        command.Parameters.AddWithValue("id", artifactId);
        command.Parameters.AddWithValue("job_id", ingested.JobId!.Value);
        command.Parameters.AddWithValue("content_hash", "ledger-endpoint-" + artifactId.ToString("N"));
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return artifactId;
    }

    private sealed record TesterAttributesDto(string ErrorType, string ErrorMessage, string HttpRoute);

    private sealed record TesterEnvelopeDto(
        string SourceKind,
        string ServiceName,
        string Environment,
        string Severity,
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

    private sealed record FaultLedgerResponseDto(Guid FaultId, IReadOnlyList<FaultLedgerEventDto> Events);

    private sealed record FaultLedgerEventDto(
        long Id,
        Guid JobId,
        int Attempt,
        string EventType,
        string? Role,
        string? ToolName,
        string? Decision,
        string? Rationale,
        string? PayloadRef,
        string PayloadState,
        string ConfigHash);
}
