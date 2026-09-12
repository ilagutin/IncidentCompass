using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Domain.Incidents.Statuses;
using IncidentCompass.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class PostgresGovernanceLedgerReaderTests(PostgresRepositoryFixture postgres)
{
    [DockerAvailableFact]
    public async Task DecisionReads_DefaultToCurrentAttemptAndCanOptIntoJobScope()
    {
        using var scope = await CreateScopeAsync();
        var job = await SeedJobAsync(scope.ConnectionString);
        var reader = scope.Services.GetRequiredService<ITriageLedgerReader>();

        await InsertLedgerEventAsync(
            scope.ConnectionString,
            job,
            attempt: 1,
            "PolicyDecision",
            "tool_x",
            "Allowed",
            rationale: null);
        await InsertLedgerEventAsync(
            scope.ConnectionString,
            job,
            attempt: 1,
            "ToolResult",
            "tool_y",
            decision: null,
            rationale: "tool_y completed successfully",
            toolStatus: "Succeeded");
        await InsertLedgerEventAsync(
            scope.ConnectionString,
            job,
            attempt: 1,
            "BudgetEvent",
            toolName: null,
            decision: null,
            rationale: "attempt one budget charge",
            tokensDelta: 99,
            workersDelta: 1);
        await InsertLedgerEventAsync(
            scope.ConnectionString,
            job,
            attempt: 2,
            "BudgetEvent",
            toolName: null,
            decision: null,
            rationale: "{\"tokensDelta\":999,\"workerDelta\":999}",
            tokensDelta: 7,
            workersDelta: 1);

        var budget = await reader.ReadBudgetUsageAsync(job, TestContext.Current.CancellationToken);
        var attemptPolicyCount = await reader.CountPolicyDecisionsAsync(
            job,
            "tool_x",
            ToolRuleScope.Attempt,
            TriageLedgerDecision.Allowed,
            TestContext.Current.CancellationToken);
        var jobPolicyCount = await reader.CountPolicyDecisionsAsync(
            job,
            "tool_x",
            ToolRuleScope.Job,
            TriageLedgerDecision.Allowed,
            TestContext.Current.CancellationToken);
        var attemptPrecondition = await reader.HasSuccessfulToolResultAsync(
            job,
            "tool_y",
            ToolRuleScope.Attempt,
            TestContext.Current.CancellationToken);
        var jobPrecondition = await reader.HasSuccessfulToolResultAsync(
            job,
            "tool_y",
            ToolRuleScope.Job,
            TestContext.Current.CancellationToken);

        Assert.Equal(new TriageBudgetLedgerUsage(7, 1), budget);
        Assert.Equal(0, attemptPolicyCount);
        Assert.Equal(1, jobPolicyCount);
        Assert.False(attemptPrecondition);
        Assert.True(jobPrecondition);
    }

    private async Task<RepositoryScope> CreateScopeAsync()
    {
        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:IncidentCompass"] = connectionString,
                ["IncidentCompass:Postgres:ConnectionStringName"] = "IncidentCompass"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestApplication(configuration);
        services.AddInfrastructure(configuration);
        return new RepositoryScope(services.BuildServiceProvider(), connectionString);
    }

    private static async Task<TriageJob> SeedJobAsync(string connectionString)
    {
        var signalId = Guid.NewGuid();
        var faultId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var configHash = "governance-reader-" + Guid.NewGuid().ToString("N");
        var fingerprint = "governance-reader-fingerprint-" + Guid.NewGuid().ToString("N");

        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO incidentcompass.triage_config_snapshots (config_hash, serialized_config, instructions, created_at_utc)
            VALUES (@config_hash, '{}'::jsonb, '{}'::jsonb, now());

            INSERT INTO incidentcompass.signals (
                id, tenant_id, source, fault_id, fingerprint, fingerprint_version, fingerprint_strength,
                external_id, is_suppressed, suppressed_by_fault_id, suppression_reason, trace_id, span_id,
                parent_span_id, service_name, environment, operation_name, severity, error_type, error_message,
                summary, description, http_method, http_route, http_status_code, duration_ms, attributes, body,
                observed_at_utc, received_at_utc)
            VALUES (
                @signal_id, 'local', 'tester', NULL, @fingerprint, 1, 'strong',
                NULL, false, NULL, NULL, NULL, NULL, NULL, 'governance-reader-svc', 'prod', 'POST /reader',
                'warning', 'ReaderProbe', 'reader probe', 'Reader probe', NULL, 'POST', '/reader',
                500, 1000, '{}'::jsonb, '{}'::jsonb, now(), now());

            INSERT INTO incidentcompass.faults (
                id, trigger_signal_id, tenant_id, status, fingerprint, fingerprint_version,
                fingerprint_strength, service_name, environment, severity, correlation_id,
                created_at_utc, completed_at_utc, recurrence_of)
            VALUES (
                @fault_id, @signal_id, 'local', 'Analyzing', @fingerprint, 1,
                'strong', 'governance-reader-svc', 'prod', 'warning', NULL, now(), NULL, NULL);

            UPDATE incidentcompass.signals SET fault_id = @fault_id WHERE id = @signal_id;

            INSERT INTO incidentcompass.triage_jobs (
                id, fault_id, status, attempt, locked_by, locked_until_utc, next_attempt_at_utc,
                last_error_code, last_error_message, config_hash, created_at_utc, updated_at_utc)
            VALUES (
                @job_id, @fault_id, 'Processing', 2, 'reader-test', now() + interval '5 minutes', NULL,
                NULL, NULL, @config_hash, now(), now());
            """,
            ("config_hash", configHash),
            ("fingerprint", fingerprint),
            ("signal_id", signalId),
            ("fault_id", faultId),
            ("job_id", jobId));

        return new TriageJob(
            jobId,
            faultId,
            TriageJobStatus.Processing,
            Attempt: 2,
            LockedBy: "reader-test",
            LockedUntilUtc: DateTimeOffset.UtcNow.AddMinutes(5),
            NextAttemptAtUtc: null,
            LastErrorCode: null,
            LastErrorMessage: null,
            configHash,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
    }

    private static Task InsertLedgerEventAsync(
        string connectionString,
        TriageJob job,
        int attempt,
        string eventType,
        string? toolName,
        string? decision,
        string? rationale,
        string? toolStatus = null,
        int? tokensDelta = null,
        int? workersDelta = null)
    {
        return ExecuteAsync(
            connectionString,
            """
            INSERT INTO incidentcompass.triage_ledger (
                fault_id, job_id, attempt, event_type, role, tool_name, rationale,
                decision, decision_reason, tool_status, tokens_delta, workers_delta,
                payload_ref, config_hash, created_at_utc)
            VALUES (
                @fault_id, @job_id, @attempt, @event_type, 'synthetic', @tool_name, @rationale,
                @decision, NULL, @tool_status, @tokens_delta, @workers_delta,
                NULL, @config_hash, now());
            """,
            ("fault_id", job.FaultId),
            ("job_id", job.Id),
            ("attempt", attempt),
            ("event_type", eventType),
            ("tool_name", toolName ?? (object)DBNull.Value),
            ("rationale", rationale ?? (object)DBNull.Value),
            ("decision", decision ?? (object)DBNull.Value),
            ("tool_status", toolStatus ?? (object)DBNull.Value),
            ("tokens_delta", tokensDelta ?? (object)DBNull.Value),
            ("workers_delta", workersDelta ?? (object)DBNull.Value),
            ("config_hash", job.ConfigHash));
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

    private sealed record RepositoryScope(ServiceProvider Services, string ConnectionString) : IDisposable
    {
        public void Dispose()
        {
            Services.Dispose();
        }
    }
}
