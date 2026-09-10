using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Application.Investigation.Reports;
using IncidentCompass.Domain.Incidents;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

internal static class TriageReportGroundingTestSupport
{
    public static async Task<TriageReportTestScope> CreateScopeAsync(
        PostgresRepositoryFixture postgres,
        Action<IServiceCollection>? configureServices = null)
    {
        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        await PostgresTriageJobTestIsolation.CompleteClaimableJobsAsync(connectionString);
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:IncidentCompass", connectionString);
            builder.UseExplicitMockProviders();
            builder.UseSetting("IncidentCompass:Pseudonymization:Salt", "redaction-e2e-salt");
            if (configureServices is not null)
            {
                builder.ConfigureTestServices(configureServices);
            }
        });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        return new TriageReportTestScope(factory, client, connectionString);
    }

    public static async Task RunClaimedJobAsync(TriageReportTestScope scope, Guid expectedJobId, string workerId, int maxAttempts)
    {
        var claimed = await ClaimAsync(scope, expectedJobId, workerId);
        using var serviceScope = scope.Factory.Services.CreateScope();
        var runner = serviceScope.ServiceProvider.GetRequiredService<ITriageJobRunner>();
        await runner.ProcessClaimedAsync(
            claimed,
            workerId,
            new TriageJobProcessingSettings(maxAttempts, TimeSpan.FromSeconds(1)),
            TestContext.Current.CancellationToken);
    }

    public static async Task<TriageJob> ClaimAsync(TriageReportTestScope scope, Guid expectedJobId, string workerId)
    {
        using var serviceScope = scope.Factory.Services.CreateScope();
        var runner = serviceScope.ServiceProvider.GetRequiredService<ITriageJobRunner>();
        var claimed = await runner.ClaimNextAsync(workerId, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        Assert.NotNull(claimed);
        Assert.Equal(expectedJobId, claimed.Id);
        return claimed;
    }

    public static TriageReport CreateReport(Guid referenceId)
    {
        return new TriageReport(
            TriageReportStatus.Completed,
            "Direct stale attempt report.",
            "SimpleKnownError",
            "Medium",
            [new TriageReportEvidenceReference(referenceId.ToString(), null)],
            [],
            "Review the trigger signal.");
    }


    public static async Task<TriageReportIngestResponse> PostIngestAsync(HttpClient client, TriageReportTesterEnvelope envelope)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/incidents",
            envelope,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TriageReportIngestResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body;
    }

    public static async Task<TriageReportIngestResponse> PostIngestAsync(HttpClient client, string prefix)
    {
        var unique = Guid.NewGuid().ToString("N");
        var response = await client.PostAsJsonAsync(
            "/api/v1/incidents",
            new TriageReportTesterEnvelope(
                "tester",
                prefix + "-svc-" + unique,
                "prod",
                DateTimeOffset.UtcNow,
                new TriageReportTesterAttributes("TimeoutException", prefix + " timeout " + unique, "/report-grounding")),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TriageReportIngestResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.NotNull(body.JobId);
        return body;
    }

    public static async Task<IReadOnlyList<string>> ReadEvidenceKindsAsync(string connectionString, Guid faultId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT e.kind
            FROM incidentcompass.triage_evidence e
            JOIN incidentcompass.triage_reports r ON r.id = e.report_id
            WHERE r.fault_id = @fault_id
            ORDER BY e.created_at_utc, e.id;
            """, connection);
        command.Parameters.AddWithValue("fault_id", faultId);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    public static async Task<TriageReportEvidenceRow> ReadSingleEvidenceAsync(string connectionString, Guid faultId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT e.kind, e.quote
            FROM incidentcompass.triage_evidence e
            JOIN incidentcompass.triage_reports r ON r.id = e.report_id
            WHERE r.fault_id = @fault_id;
            """, connection);
        command.Parameters.AddWithValue("fault_id", faultId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new TriageReportEvidenceRow(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    public static async Task<Guid> ReadArtifactIdAsync(string connectionString, Guid jobId, string kind)
    {
        return await ScalarAsync<Guid>(connectionString, "SELECT id FROM incidentcompass.triage_artifacts WHERE job_id = @job_id AND kind = @kind ORDER BY created_at_utc, id LIMIT 1;", ("job_id", jobId), ("kind", kind));
    }

    public static async Task<Guid> InsertRetrievedMemoryArtifactAsync(
        string connectionString,
        Guid jobId,
        int attempt,
        string documentationStatus)
    {
        var memoryItemId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var unique = Guid.NewGuid().ToString("N");
        await ExecuteAsync(connectionString, """
            INSERT INTO incidentcompass.memory_items (
                id, tenant_id, kind, source, title, content, content_hash, version, tags, created_at_utc)
            VALUES (
                @id, 'demo', 'runbook', @source, 'Documentation fit test', @content, @content_hash, 1,
                ARRAY['documentation'], now());
            """,
            ("id", memoryItemId),
            ("source", "test://documentation-fit/" + unique),
            ("content", "Documentation status " + documentationStatus),
            ("content_hash", unique));
        await ExecuteAsync(connectionString, """
            INSERT INTO incidentcompass.triage_artifacts (
                id, job_id, attempt, kind, domain_ref, redacted_payload, content_hash, created_at_utc)
            VALUES (
                @id, @job_id, @attempt, 'RetrievedItem', @domain_ref, @payload::jsonb, @content_hash, now());
            """,
            ("id", artifactId),
            ("job_id", jobId),
            ("attempt", attempt),
            ("domain_ref", "memory_item:" + memoryItemId),
            ("payload", JsonSerializer.Serialize(new { documentationStatus, score = 0.9 })),
            ("content_hash", unique));
        return artifactId;
    }

    public static async Task<Guid> InsertSourceArtifactAsync(
        string connectionString,
        Guid jobId,
        int attempt,
        string release)
    {
        var artifactId = Guid.NewGuid();
        await ExecuteAsync(connectionString, """
            INSERT INTO incidentcompass.triage_artifacts (
                id, job_id, attempt, kind, domain_ref, redacted_payload, content_hash, created_at_utc)
            VALUES (
                @id, @job_id, @attempt, 'RetrievedItem', @domain_ref, @payload::jsonb, @content_hash, now());
            """,
            ("id", artifactId),
            ("job_id", jobId),
            ("attempt", attempt),
            ("domain_ref", $"source:{release}:src/Checkout.cs"),
            ("payload", JsonSerializer.Serialize(new
            {
                evidenceKind = "SourceCode",
                relativePath = "src/Checkout.cs",
                lineStart = 10,
                lineEnd = 12,
                excerpt = "line 10\nline 11\nline 12",
                release,
                mappingMethod = "heuristic"
            })),
            ("content_hash", Guid.NewGuid().ToString("N")));
        return artifactId;
    }

    public static Task SetCurrentReleaseAsync(
        string connectionString,
        string configHash,
        string serviceName,
        string release) =>
        ExecuteAsync(connectionString, """
            UPDATE incidentcompass.triage_config_snapshots
            SET serialized_config = jsonb_set(
                serialized_config,
                '{CurrentReleases}',
                jsonb_build_object(@service_name, @release),
                true)
            WHERE config_hash = @config_hash;
            """,
            ("service_name", serviceName),
            ("release", release),
            ("config_hash", configHash));

    public static Task InsertToolOutcomeAsync(string connectionString, Guid jobId, int attempt) =>
        ExecuteAsync(connectionString, """
            INSERT INTO incidentcompass.triage_artifacts (
                id, job_id, attempt, kind, domain_ref, redacted_payload, content_hash, created_at_utc)
            VALUES (
                @id, @job_id, @attempt, 'ToolResult', 'tool:source_lookup',
                '{"outcome":"no_match","code":"source_no_match","matched":false}'::jsonb,
                @content_hash, now());
            """,
            ("id", Guid.NewGuid()),
            ("job_id", jobId),
            ("attempt", attempt),
            ("content_hash", Guid.NewGuid().ToString("N")));
    public static async Task ExecuteAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
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

    public static async Task<T> ScalarAsync<T>(string connectionString, string sql, params (string Name, object Value)[] parameters)
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
}
