using IncidentCompass.Application.Core.ModelClients;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// Repetition detection through the real runner, the real <c>memory_search</c> tool and PostgreSQL.
/// </summary>
public sealed partial class MemorySearchTests
{
    [DockerAvailableFact]
    public async Task ProcessClaimedAsync_RepeatedMemorySearchWithTheSameMatches_IsRefusedAndRecordedWithoutARateCap()
    {
        // The shipped rate_cap (50 per attempt) never fires here, so only repetition detection can
        // stop the worker from searching again. Every search finds the seeded runbook under a fresh
        // RetrievedItem artifact id, which the result identity must see through.
        using var scope = await CreateScopeAsync(configureServices: services =>
        {
            services.RemoveAll<IAiModelClient>();
            services.AddScoped<IAiModelClient, RepeatMemorySearchModelClient>();
        });
        await SeedCheckoutRunbookAsync(scope.Factory);
        var ingested = await PostIngestAsync(scope.Client, TesterEnvelope("payments-api", "TimeoutException", "Checkout timed out while calling inventory", "/checkout"));

        await RunClaimedJobAsync(scope, ingested.JobId!.Value, "worker-memory-repeat", maxAttempts: 1);

        var toolRows = await ReadToolLedgerRowsAsync(scope.ConnectionString, ingested.JobId.Value);
        var retrievedIds = await ReadArtifactIdsAsync(scope.ConnectionString, ingested.JobId.Value, "RetrievedItem");
        var refusals = await ReadNoProgressRowsAsync(scope.ConnectionString, ingested.JobId.Value);

        // The default MaxEquivalentCalls of 2 lets the search run three times; every later identical
        // search is refused before it runs, until the worker's own turn limit ends the attempt.
        Assert.Equal(3, toolRows.Count(row => row.EventType == "ToolResult" && row.ToolStatus == "Succeeded"));
        Assert.Equal(3, retrievedIds.Distinct().Count());
        Assert.DoesNotContain(toolRows, row => row.EventType == "PolicyDecision" && row.Decision == "Denied");
        Assert.NotEmpty(refusals);
        Assert.All(refusals, row =>
        {
            Assert.Equal("memory", row.Role);
            Assert.Equal("memory_search", row.ToolName);
            Assert.Matches(
                "^no_progress: repeated_call role=memory tool=memory_search fingerprint=[0-9a-f]{16} unproductive_repeats=2 max_equivalent_calls=2$",
                row.Rationale);
        });
        Assert.Equal(
            toolRows.Count(row => row.EventType == "PolicyDecision" && row.Decision == "Allowed"),
            3 + refusals.Count);
    }

    private static async Task<IReadOnlyList<NoProgressLedgerRow>> ReadNoProgressRowsAsync(string connectionString, Guid jobId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT role, tool_name, rationale
            FROM incidentcompass.triage_ledger
            WHERE job_id = @job_id AND event_type = 'BudgetEvent' AND rationale LIKE 'no_progress: repeated_call%'
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

    private sealed record NoProgressLedgerRow(string? Role, string? ToolName, string Rationale);
}
