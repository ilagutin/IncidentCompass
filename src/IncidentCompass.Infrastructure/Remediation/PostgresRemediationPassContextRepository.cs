using System.Text.Json;
using IncidentCompass.Application.Investigation.Reports;
using IncidentCompass.Application.Remediation;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Infrastructure.Postgres;
using Npgsql;

namespace IncidentCompass.Infrastructure.Remediation;

/// <summary>
/// Reads back the durable state one remediation pass runs on, for one published report.
/// </summary>
/// <remarks>
/// <para>
/// Two statements on one connection rather than one join, because the two answers have different
/// shapes: the first is one row describing the report, its job and its fault, and the second is a
/// list of cited evidence. Joining them would repeat the report across every evidence row and leave
/// this adapter deduplicating what it had just multiplied.
/// </para>
/// <para>
/// The tenant is a predicate on the fault, not a filter applied afterwards, so a report belonging to
/// another tenant is not found rather than found and discarded. The evidence query is scoped by the
/// report rather than by job and attempt on purpose: what a pass may write a diff against is what
/// the report actually cited, and an artifact the attempt produced but the report did not cite is
/// not evidence for anything.
/// </para>
/// </remarks>
internal sealed class PostgresRemediationPassContextRepository(
    PostgresDataSourceProvider dataSourceProvider) : IRemediationPassContextRepository
{
    /// <summary>
    /// Bound on cited evidence rows one read returns. The prompt builder already caps how many it
    /// renders, so this only keeps a pathological report from being materialized whole in order to
    /// throw most of it away.
    /// </summary>
    private const int MaxEvidenceRows = 256;

    public Task<RemediationPassContext?> FindAsync(
        string tenantId,
        Guid reportId,
        CancellationToken cancellationToken) =>
        PostgresOperation.ExecuteAsync(
            "read remediation pass context",
            () => FindCoreAsync(tenantId, reportId, cancellationToken));

    private async Task<RemediationPassContext?> FindCoreAsync(
        string tenantId,
        Guid reportId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSourceProvider.OpenConnectionAsync(cancellationToken);
        if (await ReadHeaderAsync(connection, tenantId, reportId, cancellationToken) is not
            { } header)
        {
            return null;
        }

        var (evidence, sourceEvidence) = await ReadEvidenceAsync(connection, reportId, cancellationToken);
        return new RemediationPassContext(
            header.Job,
            header.Fault,
            header.Report with { Evidence = evidence },
            sourceEvidence);
    }

    private static async Task<(TriageJob Job, Fault Fault, TriageReport Report)?> ReadHeaderAsync(
        NpgsqlConnection connection,
        string tenantId,
        Guid reportId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT report.status, report.summary, report.classification, report.confidence,
                   report.recommended_next_action, report.limitations,
                   job.id, job.fault_id, job.status, job.attempt, job.config_hash,
                   job.created_at_utc, job.updated_at_utc,
                   job.retriage_trigger_job_id, job.supersedes_report_id,
                   fault.id, fault.trigger_signal_id, fault.tenant_id, fault.status,
                   fault.fingerprint, fault.fingerprint_version, fault.fingerprint_strength,
                   fault.can_group, fault.service_name, fault.environment, fault.severity,
                   fault.correlation_id, fault.created_at_utc, fault.completed_at_utc,
                   fault.recurrence_of, fault.grouping_rule_id, fault.grouping_rule_version
            FROM incidentcompass.triage_reports AS report
            JOIN incidentcompass.triage_jobs AS job ON job.id = report.job_id
            JOIN incidentcompass.faults AS fault ON fault.id = report.fault_id
            WHERE report.id = @report_id AND fault.tenant_id = @tenant_id;
            """,
            connection);
        command.AddParameter("report_id", reportId);
        command.AddParameter("tenant_id", tenantId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (ReadJob(reader), ReadFault(reader), ReadReport(reader));
    }

    private static TriageReport ReadReport(NpgsqlDataReader reader) => new(
        Enum.Parse<TriageReportStatus>(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        [],
        reader.IsDBNull(5) ? [] : reader.GetFieldValue<string[]>(5),
        reader.IsDBNull(4) ? string.Empty : reader.GetString(4));

    /// <summary>
    /// The claim columns are deliberately read as null. A pass runs after the attempt that published
    /// the report released its lease, so carrying a stale lock owner and expiry forward would be a
    /// false statement about who holds the job now. Nothing on the remediation path claims, renews or
    /// releases a triage job; the job is here to name the attempt the budget and the ledger belong to.
    /// </summary>
    private static TriageJob ReadJob(NpgsqlDataReader reader) => new(
        reader.GetGuid(6),
        reader.GetGuid(7),
        Enum.Parse<TriageJobStatus>(reader.GetString(8)),
        reader.GetInt32(9),
        LockedBy: null,
        LockedUntilUtc: null,
        NextAttemptAtUtc: null,
        LastErrorCode: null,
        LastErrorMessage: null,
        reader.GetString(10),
        reader.GetFieldValue<DateTimeOffset>(11),
        reader.GetFieldValue<DateTimeOffset>(12))
    {
        ReTriageTriggerJobId = reader.IsDBNull(13) ? null : reader.GetGuid(13),
        SupersedesReportId = reader.IsDBNull(14) ? null : reader.GetGuid(14)
    };

    private static Fault ReadFault(NpgsqlDataReader reader) => new(
        reader.GetGuid(15),
        reader.GetGuid(16),
        reader.GetString(17),
        Enum.Parse<FaultStatus>(reader.GetString(18)),
        reader.GetString(19),
        reader.GetInt32(20),
        Enum.Parse<FingerprintStrength>(reader.GetString(21), ignoreCase: true),
        reader.GetBoolean(22),
        reader.GetString(23),
        reader.GetString(24),
        reader.IsDBNull(25) ? null : reader.GetString(25),
        reader.IsDBNull(26) ? null : reader.GetString(26),
        reader.GetFieldValue<DateTimeOffset>(27),
        reader.IsDBNull(28) ? null : reader.GetFieldValue<DateTimeOffset>(28),
        reader.IsDBNull(29) ? null : reader.GetGuid(29))
    {
        GroupingRuleId = reader.GetString(30),
        GroupingRuleVersion = reader.GetInt32(31)
    };

    private static async Task<(IReadOnlyList<TriageReportEvidenceReference> Evidence,
        IReadOnlyList<TriageArtifact> SourceEvidence)> ReadEvidenceAsync(
        NpgsqlConnection connection,
        Guid reportId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT evidence.reference, evidence.quote,
                   artifact.id, artifact.job_id, artifact.attempt, artifact.kind,
                   artifact.domain_ref, artifact.redacted_payload::text, artifact.content_hash,
                   artifact.created_at_utc
            FROM incidentcompass.triage_evidence AS evidence
            JOIN incidentcompass.triage_artifacts AS artifact ON artifact.id = evidence.artifact_id
            WHERE evidence.report_id = @report_id
            ORDER BY evidence.created_at_utc, evidence.id
            LIMIT @limit;
            """,
            connection);
        command.AddParameter("report_id", reportId);
        command.AddParameter("limit", MaxEvidenceRows);
        var evidence = new List<TriageReportEvidenceReference>();
        var sourceEvidence = new List<TriageArtifact>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            evidence.Add(new TriageReportEvidenceReference(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1)));
            var domainRef = reader.IsDBNull(6) ? null : reader.GetString(6);
            if (IsSourceEvidence(reader.GetString(5), domainRef))
            {
                sourceEvidence.Add(ReadArtifact(reader, domainRef));
            }
        }

        return (evidence, sourceEvidence);
    }

    /// <summary>
    /// Whether a cited artifact came from the source-read boundary. The pairing of kind and
    /// <c>domain_ref</c> prefix is the one <c>SourceLookupTool</c> writes, and matching both rather
    /// than the prefix alone keeps a future artifact kind that happens to reuse the prefix from
    /// being handed to a diff pass as if it were a file.
    /// </summary>
    private static bool IsSourceEvidence(string kind, string? domainRef) =>
        string.Equals(kind, nameof(ArtifactKind.RetrievedItem), StringComparison.Ordinal) &&
        domainRef is not null &&
        domainRef.StartsWith("source:", StringComparison.Ordinal);

    private static TriageArtifact ReadArtifact(NpgsqlDataReader reader, string? domainRef) => new(
        reader.GetGuid(2),
        reader.GetGuid(3),
        reader.IsDBNull(4) ? null : reader.GetInt32(4),
        Enum.Parse<ArtifactKind>(reader.GetString(5)),
        domainRef,
        JsonDocument.Parse(reader.GetString(7)).RootElement.Clone(),
        reader.GetString(8),
        reader.GetFieldValue<DateTimeOffset>(9));
}
