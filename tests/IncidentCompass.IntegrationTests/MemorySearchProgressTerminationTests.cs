using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Investigation.Reports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// Repetition detection, bounded recovery and honest termination through the real runner, the real
/// <c>memory_search</c> tool and PostgreSQL. It reuses the <see cref="MemorySearchTests"/> scope, seed
/// and readers rather than copying them.
/// </summary>
[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class MemorySearchProgressTerminationTests(PostgresRepositoryFixture postgres)
{
    [DockerAvailableFact]
    public async Task ProcessClaimedAsync_ModelThatOnlyRepeatsMemorySearch_EndsSucceededWithTheBackendReport()
    {
        // The shipped rate_cap (50 per attempt) never fires here, so only repetition and progress
        // detection can stop this model: the orchestrator always delegates the same memory task and
        // the worker always runs the same search, which finds the seeded runbook under a fresh
        // RetrievedItem artifact id every time. What happens, with the shipped defaults:
        // - delegate 1: three searches run, the next two are refused in a row, the worker is stopped;
        // - later delegates: the worker's searches are refused at once and it is stopped again, until
        //   the delegate itself is refused as a repeat;
        // - after four turns without progress the one recovery call runs (this model answers it with
        //   text), four more stalled turns end the attempt with the backend's InsufficientEvidence report.
        using var scope = await new MemorySearchTests(postgres).CreateScopeAsync(configureServices: services =>
        {
            services.RemoveAll<IAiModelClient>();
            services.AddScoped<IAiModelClient, MemorySearchTests.RepeatMemorySearchModelClient>();
        });
        await MemorySearchTests.SeedCheckoutRunbookAsync(scope.Factory);
        var ingested = await MemorySearchTests.PostIngestAsync(scope.Client, MemorySearchTests.TesterEnvelope("payments-api", "TimeoutException", "Checkout timed out while calling inventory", "/checkout"));

        await MemorySearchTests.RunClaimedJobAsync(scope, ingested.JobId!.Value, "worker-memory-repeat", maxAttempts: 1);

        var jobId = ingested.JobId.Value;
        var toolRows = await MemorySearchTests.ReadToolLedgerRowsAsync(scope.ConnectionString, jobId);
        var retrievedIds = await MemorySearchTests.ReadArtifactIdsAsync(scope.ConnectionString, jobId, "RetrievedItem");
        var noProgress = await ReadNoProgressRowsAsync(scope.ConnectionString, jobId);

        Assert.Equal(3, toolRows.Count(row => row.EventType == "ToolResult" && row.ToolStatus == "Succeeded"));
        Assert.Equal(3, retrievedIds.Distinct().Count());
        Assert.DoesNotContain(toolRows, row => row.EventType == "PolicyDecision" && row.Decision == "Denied");
        var searchRefusals = noProgress
            .Where(row => row.ToolName == "memory_search" && row.Rationale.StartsWith("no_progress: repeated_call", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(searchRefusals);
        Assert.All(searchRefusals, row =>
        {
            Assert.Equal("memory", row.Role);
            Assert.Matches(
                "^no_progress: repeated_call role=memory tool=memory_search fingerprint=[0-9a-f]{16} unproductive_repeats=2 max_equivalent_calls=2$",
                row.Rationale);
        });
        Assert.Equal(
            toolRows.Count(row => row.EventType == "PolicyDecision" && row.Decision == "Allowed"),
            3 + searchRefusals.Length);
        Assert.Contains(noProgress, row => row.Rationale == "no_progress: worker_stopped role=memory consecutive_refusals=2");
        Assert.Single(noProgress, row => row.Rationale.StartsWith("no_progress: recovery recovery=1/1 ", StringComparison.Ordinal));
        Assert.Single(noProgress, row => row.Rationale.StartsWith("no_progress: terminated reason=no_recovery_left recoveries_used=1 max_recoveries=1 ", StringComparison.Ordinal));

        Assert.Equal("Succeeded", await ReadJobStatusAsync(scope.ConnectionString, jobId));
        var report = await MemorySearchTests.ReadReportAsync(scope.ConnectionString, ingested.FaultId);
        Assert.Equal("InsufficientEvidence", report.Status);
        Assert.Equal("Unknown", report.Classification);
        var persisted = await ReadReportTextAsync(scope.ConnectionString, ingested.FaultId);
        Assert.Equal(NoProgressTerminationReport.Summary, persisted.Summary);
        Assert.Contains(NoProgressTerminationReport.LimitationNoRecoveryLeft, persisted.Limitations);
        Assert.Equal("Missing", persisted.DocumentationFit);
        Assert.Equal(1, persisted.EvidenceCount);
        Assert.Equal(1, await CountRecoveryModelCallsAsync(scope.ConnectionString, jobId));
    }

    private static async Task<IReadOnlyList<NoProgressLedgerRow>> ReadNoProgressRowsAsync(string connectionString, Guid jobId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT role, tool_name, rationale
            FROM incidentcompass.triage_ledger
            WHERE job_id = @job_id AND event_type = 'BudgetEvent' AND rationale LIKE 'no_progress:%'
            ORDER BY id;
            """, connection);
        command.Parameters.AddWithValue("job_id", jobId);
        var rows = new List<NoProgressLedgerRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new NoProgressLedgerRow(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2)));
        }

        return rows;
    }

    private static async Task<string> ReadJobStatusAsync(string connectionString, Guid jobId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT status FROM incidentcompass.triage_jobs WHERE id = @job_id;", connection);
        command.Parameters.AddWithValue("job_id", jobId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<PersistedReportText> ReadReportTextAsync(string connectionString, Guid faultId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT r.summary, r.limitations, r.documentation_fit,
                   (SELECT count(*) FROM incidentcompass.triage_evidence e WHERE e.report_id = r.id)
            FROM incidentcompass.triage_reports r
            WHERE r.fault_id = @fault_id;
            """, connection);
        command.Parameters.AddWithValue("fault_id", faultId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new PersistedReportText(
            reader.GetString(0),
            reader.GetFieldValue<string[]>(1),
            reader.GetString(2),
            (int)reader.GetInt64(3));
    }

    private static async Task<int> CountRecoveryModelCallsAsync(string connectionString, Guid jobId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT count(*)
            FROM incidentcompass.triage_ledger
            WHERE job_id = @job_id AND event_type = 'ModelCall' AND (rationale::jsonb)->>'kind' = 'recovery';
            """, connection);
        command.Parameters.AddWithValue("job_id", jobId);
        return (int)(long)(await command.ExecuteScalarAsync())!;
    }

    private sealed record NoProgressLedgerRow(string? Role, string? ToolName, string Rationale);

    private sealed record PersistedReportText(string Summary, string[] Limitations, string DocumentationFit, int EvidenceCount);
}
