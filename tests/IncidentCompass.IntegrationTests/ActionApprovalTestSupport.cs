using System.Globalization;
using System.Text;
using IncidentCompass.Application;
using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Governance.ActionApprovals.Testing;
using IncidentCompass.Application.Investigation.Reports;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using NpgsqlTypes;

namespace IncidentCompass.IntegrationTests;

internal static class ActionApprovalTestSupport
{
    public static ServiceProvider CreateServices(
        string connectionString,
        IActionApprovalTransactionFaultInjector? faultInjector = null,
        TimeProvider? timeProvider = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ActionApprovalTests"] = connectionString,
                ["IncidentCompass:Postgres:ConnectionStringName"] = "ActionApprovalTests",
                ["IncidentCompass:ModelGateway:Provider"] = "Mock",
                ["IncidentCompass:Embeddings:Provider"] = "Mock",
                ["IncidentCompass:Tickets:GitHub:Owner"] = "owner",
                ["IncidentCompass:Tickets:GitHub:Repository"] = "repo",
                ["IncidentCompass:Tickets:GitHub:Token"] = "test-token"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddApplication(configuration);
        services.AddInfrastructure(configuration);
        configureServices?.Invoke(services);
        if (faultInjector is not null)
        {
            services.RemoveAll<IActionApprovalTransactionFaultInjector>();
            services.AddSingleton(faultInjector);
        }

        if (timeProvider is not null)
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton(timeProvider);
        }

        return services.BuildServiceProvider();
    }

    public static PreparedActionProposal Proposal(
        ActionApprovalOriginFixture origin,
        string proposalKey,
        IReadOnlyList<Guid>? evidence = null,
        bool automaticallyApproved = false) =>
        new(
            origin.TenantId,
            origin.ReportId,
            "ticket_create",
            proposalKey,
            IncidentCompass.Domain.Incidents.Actions.ActionCategory.TicketCreate,
            IncidentCompass.Domain.Incidents.Actions.ActionExecutionMode.Live,
            "github:owner/repository",
            new string('a', 64),
            Encoding.UTF8.GetBytes("{\"title\":\"Investigate incident\"}"),
            "Create a bounded review ticket.",
            60,
            evidence ?? [origin.EvidenceArtifactId],
            automaticallyApproved,
            automaticallyApproved ? "allowed" : "approval_required");

    public static async Task<ActionApprovalOriginFixture> SeedOriginAsync(
        string connectionString,
        string tenantId = "tenant-action-tests",
        string? serializedConfigJson = null,
        string reportStatus = "Completed",
        bool includeEvidence = true,
        string signalSummary = "action test signal",
        string reportSummary = "action test report")
    {
        var suffix = Guid.NewGuid().ToString("N");
        var signalId = Guid.NewGuid();
        var faultId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var configHash = "action-config-" + suffix;
        await ExecuteAsync(connectionString, """
            INSERT INTO incidentcompass.triage_config_snapshots (
                config_hash, serialized_config, instructions, created_at_utc)
            VALUES (@config_hash, CAST(@serialized_config AS jsonb), '{}'::jsonb, clock_timestamp());

            INSERT INTO incidentcompass.signals (
                id, tenant_id, source, fingerprint, fingerprint_version, fingerprint_strength,
                service_name, environment, error_type, summary, body, observed_at_utc, received_at_utc)
            VALUES (@signal_id, @tenant_id, 'tester', @fingerprint, 1, 'strong',
                    'orders', 'test', 'TimeoutException', @signal_summary, '{}'::jsonb,
                    clock_timestamp(), clock_timestamp());

            INSERT INTO incidentcompass.faults (
                id, trigger_signal_id, tenant_id, status, fingerprint, fingerprint_version,
                fingerprint_strength, service_name, environment, created_at_utc, completed_at_utc)
            VALUES (@fault_id, @signal_id, @tenant_id, 'Completed', @fingerprint, 1,
                    'strong', 'orders', 'test', clock_timestamp(), clock_timestamp());

            UPDATE incidentcompass.signals SET fault_id = @fault_id WHERE id = @signal_id;

            INSERT INTO incidentcompass.triage_jobs (
                id, fault_id, status, attempt, config_hash, created_at_utc, updated_at_utc)
            VALUES (@job_id, @fault_id, 'Succeeded', 1, @config_hash, clock_timestamp(), clock_timestamp());

            INSERT INTO incidentcompass.triage_artifacts (
                id, job_id, attempt, kind, domain_ref, redacted_payload, content_hash, created_at_utc)
            VALUES (@artifact_id, @job_id, NULL, 'TriggerSignal', @domain_ref,
                    '{"summary":"redacted evidence"}'::jsonb, @content_hash, clock_timestamp());

            INSERT INTO incidentcompass.triage_reports (
                id, job_id, fault_id, status, summary, classification, confidence,
                documentation_fit, limitations, config_hash, created_at_utc)
            VALUES (@report_id, @job_id, @fault_id, @report_status, @report_summary,
                    @classification, 'High', 'Current', ARRAY[]::text[], @config_hash, clock_timestamp());

            INSERT INTO incidentcompass.triage_evidence (
                id, report_id, kind, artifact_id, reference, created_at_utc)
            SELECT gen_random_uuid(), @report_id, 'TriggerSignal', @artifact_id, @domain_ref, clock_timestamp()
            WHERE @include_evidence;

            INSERT INTO incidentcompass.triage_ledger (
                fault_id, job_id, attempt, event_type, tool_name, rationale,
                payload_ref, config_hash, created_at_utc)
            VALUES (@fault_id, @job_id, 1, 'ReportPublished', 'publish_report',
                    @report_summary, @payload_ref, @config_hash, clock_timestamp());
            """,
            ("config_hash", configHash),
            ("serialized_config", serializedConfigJson ?? "{\"CurrentReleases\":{\"orders\":\"v1\"}}"),
            ("signal_id", signalId),
            ("tenant_id", tenantId),
            ("signal_summary", signalSummary),
            ("fingerprint", "fingerprint-" + suffix),
            ("fault_id", faultId),
            ("job_id", jobId),
            ("artifact_id", artifactId),
            ("domain_ref", "signal:" + signalId),
            ("content_hash", "artifact-" + suffix),
            ("report_id", reportId),
            ("report_status", reportStatus),
            ("report_summary", reportSummary),
            ("classification", reportStatus == "InsufficientEvidence" ? "Unknown" : "KnownIncident"),
            ("include_evidence", includeEvidence),
            ("payload_ref", "report:" + reportId));
        return new ActionApprovalOriginFixture(
            tenantId, signalId, faultId, jobId, reportId, artifactId, configHash);
    }

    public static async Task<(TriageJob Job, TriageReport Report)> SeedSuccessorJobAsync(
        string connectionString,
        ActionApprovalOriginFixture origin,
        string workerId)
    {
        var jobId = Guid.NewGuid();
        var recurrenceArtifactId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await ExecuteAsync(connectionString, """
            INSERT INTO incidentcompass.triage_jobs (
                id, fault_id, status, attempt, locked_by, locked_until_utc,
                config_hash, created_at_utc, updated_at_utc,
                retriage_trigger_job_id, supersedes_report_id)
            VALUES (@job_id, @fault_id, 'Processing', 1, @worker_id, @locked_until,
                    @config_hash, @now, @now, @trigger_job_id, @report_id);

            INSERT INTO incidentcompass.triage_artifacts (
                id, job_id, attempt, kind, domain_ref, redacted_payload, content_hash, created_at_utc)
            VALUES (@artifact_id, @job_id, NULL, 'RecurrenceState', @domain_ref,
                    '{"recurrenceCount":2}'::jsonb, @content_hash, @now);
            """,
            ("job_id", jobId), ("fault_id", origin.FaultId), ("worker_id", workerId),
            ("locked_until", now.AddMinutes(5)), ("config_hash", origin.ConfigHash), ("now", now),
            ("trigger_job_id", origin.JobId), ("report_id", origin.ReportId),
            ("artifact_id", recurrenceArtifactId), ("domain_ref", "job:" + jobId),
            ("content_hash", "recurrence-" + Guid.NewGuid().ToString("N")));
        var job = new TriageJob(
            jobId, origin.FaultId, TriageJobStatus.Processing, 1, workerId, now.AddMinutes(5),
            null, null, null, origin.ConfigHash, now, now)
        {
            ReTriageTriggerJobId = origin.JobId,
            SupersedesReportId = origin.ReportId
        };
        var report = new TriageReport(
            TriageReportStatus.Completed,
            "Successor report for action lock ordering.",
            "KnownIncident",
            "High",
            [new TriageReportEvidenceReference(recurrenceArtifactId.ToString(), null)],
            [],
            "Review the successor report.");
        return (job, report);
    }

    public static async Task<Guid> SeedTicketSearchResultAsync(
        string connectionString,
        ActionApprovalOriginFixture origin,
        string outcome = "no_match",
        string? provider = "github",
        string? repository = "owner/repo")
    {
        var artifactId = Guid.NewGuid();
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            matched = outcome == "matched",
            message = outcome == "matched" ? "matches found" :
                outcome == "no_match" ? "no matches" : "connector unavailable",
            outcome,
            provider,
            repository,
            code = outcome == "matched" ? "ticket_search_matches" :
                outcome == "no_match" ? "ticket_search_no_matches" : "ticket_search_unavailable",
            items = outcome == "matched"
                ? new object[] { new { externalId = "42" } }
                : Array.Empty<object>(),
            noMatchReason = outcome == "matched" ? null : "ticket_search_no_matches"
        });
        await ExecuteAsync(connectionString, """
            INSERT INTO incidentcompass.triage_artifacts (
                id, job_id, attempt, kind, domain_ref, redacted_payload, content_hash, created_at_utc)
            VALUES (@id, @job_id, 1, 'ToolResult', 'tool:ticket_search',
                    CAST(@payload AS jsonb), @content_hash, clock_timestamp());

            INSERT INTO incidentcompass.triage_ledger (
                fault_id, job_id, attempt, event_type, role, tool_name, rationale,
                tool_status, payload_ref, config_hash, created_at_utc)
            VALUES (@fault_id, @job_id, 1, 'ToolResult', 'tickets', 'ticket_search',
                    'ticket search test outcome', 'Succeeded', @payload_ref,
                    @config_hash, clock_timestamp());
            """,
            ("id", artifactId),
            ("job_id", origin.JobId),
            ("payload", payload),
            ("content_hash", "ticket-result-" + artifactId.ToString("N")),
            ("fault_id", origin.FaultId),
            ("payload_ref", "artifact:" + artifactId),
            ("config_hash", origin.ConfigHash));
        return artifactId;
    }

    public static async Task<long> CountAsync(
        string connectionString,
        string sql,
        params (string Name, object Value)[] parameters) =>
        Convert.ToInt64(await ScalarAsync(connectionString, sql, parameters), CultureInfo.InvariantCulture);

    public static async Task<object?> ScalarAsync(
        string connectionString,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        AddParameters(command, parameters);
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    public static async Task ExecuteAsync(
        string connectionString,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        AddParameters(command, parameters);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static void AddParameters(NpgsqlCommand command, IEnumerable<(string Name, object Value)> parameters)
    {
        foreach (var parameter in parameters)
        {
            if (parameter.Value is DBNull)
            {
                command.Parameters.Add(new NpgsqlParameter(parameter.Name, NpgsqlDbType.Text)
                {
                    Value = DBNull.Value
                });
            }
            else
            {
                command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            }
        }
    }
}
