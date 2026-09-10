using IncidentCompass.Application.Investigation.Reports;
using IncidentCompass.Application.Investigation.Reports.Context;
using IncidentCompass.Application.Tickets;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Infrastructure.Postgres;
using Npgsql;

namespace IncidentCompass.Infrastructure.Investigation;

internal sealed class PostgresReportEvidenceGrounder
{
    private readonly string? configuredTicketRepository;

    public PostgresReportEvidenceGrounder(string? configuredTicketRepository = null)
    {
        this.configuredTicketRepository = configuredTicketRepository;
    }

    public async Task<IReadOnlyList<GroundedReportEvidence>> GroundAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TriageJob job,
        IReadOnlyList<TriageReportEvidenceReference> references,
        CancellationToken cancellationToken)
    {
        var grounded = new List<GroundedReportEvidence>();
        foreach (var reference in references)
        {
            grounded.Add(await GroundOneAsync(connection, transaction, job, reference, cancellationToken));
        }

        return grounded;
    }

    private async Task<GroundedReportEvidence> GroundOneAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TriageJob job,
        TriageReportEvidenceReference reference,
        CancellationToken cancellationToken)
    {
        var artifactId = ParseArtifactId(reference.ReferenceId);
        await using var command = new NpgsqlCommand("""
            SELECT a.kind,
                   a.domain_ref,
                   a.redacted_payload::text,
                   mi.kind,
                   CASE
                       WHEN jsonb_typeof(a.redacted_payload->'score') = 'number'
                       THEN (a.redacted_payload->>'score')::double precision
                       ELSE NULL
                   END AS score,
                   mi.id AS memory_item_id,
                   a.redacted_payload->>'documentationStatus' AS documentation_status,
                   snapshot.serialized_config->'CurrentReleases'->>fault.service_name AS current_release
            FROM incidentcompass.triage_artifacts a
            JOIN incidentcompass.triage_jobs job ON job.id = a.job_id
            JOIN incidentcompass.faults fault ON fault.id = job.fault_id
            JOIN incidentcompass.triage_config_snapshots snapshot ON snapshot.config_hash = job.config_hash
            LEFT JOIN incidentcompass.memory_items mi
              ON a.kind = 'RetrievedItem'
             AND a.domain_ref = 'memory_item:' || mi.id::text
            WHERE a.id = @artifact_id
              AND a.job_id = @job_id
              AND (a.attempt IS NULL OR a.attempt = @attempt)
              AND a.kind = ANY(ARRAY['TriggerSignal','NeighborSet','PriorReport','RecurrenceState','RetrievedItem','ToolResult'])
            LIMIT 1;
            """, connection, transaction);
        command.AddParameter("artifact_id", artifactId);
        command.AddParameter("job_id", job.Id);
        command.AddParameter("attempt", job.Attempt);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new TriageReportValidationException(
                "publish_report evidence referenceId does not resolve to a citable artifact for this job attempt.");
        }

        var artifactKind = reader.GetString(0);
        var payload = reader.GetString(2);
        var memoryKind = reader.IsDBNull(3) ? null : reader.GetString(3);
        double? score = reader.IsDBNull(4) ? null : reader.GetDouble(4);
        Guid? memoryItemId = reader.IsDBNull(5) ? null : reader.GetGuid(5);
        var documentationStatus = reader.IsDBNull(6) ? null : reader.GetString(6);
        var domainRef = reader.IsDBNull(1) ? null : reader.GetString(1);
        var currentRelease = reader.IsDBNull(7) ? null : reader.GetString(7);
        return new GroundedReportEvidence(
            artifactId,
            DeriveEvidenceKind(artifactKind, memoryKind, domainRef, payload, currentRelease),
            reference.ReferenceId,
            ValidateQuote(reference.Quote, payload),
            score,
            memoryItemId,
            documentationStatus);
    }

    private static Guid ParseArtifactId(string referenceId)
    {
        if (ReportEvidenceArtifactReference.TryParse(referenceId, out var artifactId))
        {
            return artifactId;
        }

        throw new TriageReportValidationException("publish_report evidence referenceId must be a triage artifact id.");
    }

    private string DeriveEvidenceKind(
        string artifactKind,
        string? memoryKind,
        string? domainRef,
        string payload,
        string? currentRelease)
    {
        if (artifactKind == "RetrievedItem" && memoryKind is null)
        {
            if (SourceCodeEvidenceShape.IsCitable(domainRef, payload, currentRelease))
            {
                return "RetrievedItem";
            }

            if (ExistingTicketEvidenceShape.IsCitable(domainRef, payload, configuredTicketRepository))
            {
                return "RetrievedItem";
            }

            throw new TriageReportValidationException(
                "publish_report RetrievedItem evidence has an unsupported or invalid evidence shape.");
        }

        return artifactKind switch
        {
            "RetrievedItem" => memoryKind switch
            {
                "runbook" => "Runbook",
                "known_incident" => "KnownIncident",
                "operational_note" => "OperationalNote",
                "release_note" => "ReleaseNote",
                "postmortem" => "Postmortem",
                _ => "RetrievedItem"
            },
            _ => artifactKind
        };
    }

    private static string? ValidateQuote(string? quote, string redactedPayload)
    {
        if (string.IsNullOrWhiteSpace(quote))
        {
            return null;
        }

        return redactedPayload.Contains(quote, StringComparison.Ordinal) ? quote : null;
    }
}
