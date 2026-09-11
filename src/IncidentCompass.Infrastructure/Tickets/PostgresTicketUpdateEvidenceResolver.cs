using System.Security.Cryptography;
using System.Text;
using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Tickets;
using IncidentCompass.Domain.Incidents.Actions;
using IncidentCompass.Infrastructure.Governance.ActionApprovals;
using IncidentCompass.Infrastructure.Postgres;
using IncidentCompass.Infrastructure.Remediation;
using Microsoft.Extensions.Options;
using Npgsql;

namespace IncidentCompass.Infrastructure.Tickets;

internal sealed class PostgresTicketUpdateEvidenceResolver(
    PostgresDataSourceProvider dataSourceProvider,
    IOptions<GitHubIssuesOptions> options) : ITicketUpdateEvidenceResolver
{
    public Task<TicketUpdateEvidence?> ResolveAsync(
        string tenantId,
        Guid originReportId,
        CancellationToken cancellationToken) =>
        PostgresOperation.ExecuteAsync(
            "resolve ticket update evidence",
            async () =>
            {
                await using var connection = await dataSourceProvider.OpenConnectionAsync(cancellationToken);
                return await ResolveAsync(
                    connection, null, tenantId, originReportId, Guid.Empty, 0,
                    options.Value.ConfiguredRepository, cancellationToken);
            });

    internal static async Task EnforceProposalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        GovernedActionProposal proposal,
        GroundedActionProposalContext origin,
        string? configuredRepository,
        Func<string, Task> denyAsync,
        CancellationToken cancellationToken)
    {
        var isBacklink = proposal.RegisteredTool.ToolId == TicketBacklinkDescriptor.ToolId;
        if (proposal.RegisteredTool.Category != ActionCategory.TicketUpdate ||
            (!isBacklink && proposal.RegisteredTool.ToolId !=
                IncidentCompass.Application.Governance.PostReportActions
                    .TicketUpdatePostReportActionWorkflow.UpdateToolId))
        {
            return;
        }

        if (configuredRepository is null)
        {
            await denyAsync("ticket_update_binding_unavailable");
            return;
        }

        var expectedBinding = ExternalActionBinding.ComputeFingerprint(
            "github-issues",
            proposal.RegisteredTool.LogicalTargetId!,
            GitHubIssuesTicketSearch.Authority.AbsoluteUri,
            configuredRepository);
        if (!ActionProposalValidator.IsLowerHexSha256(proposal.AdapterBindingFingerprint) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expectedBinding),
                Encoding.ASCII.GetBytes(proposal.AdapterBindingFingerprint)))
        {
            await denyAsync("ticket_update_binding_changed");
            return;
        }

        if (!GitHubIssueCommentMarker.TryReadPayload(
                proposal.CanonicalPayload, out var payload) ||
            payload.OriginReportId != proposal.OriginReportId ||
            (payload.PullRequestNumber is not null) != isBacklink)
        {
            await denyAsync("ticket_update_payload_invalid");
            return;
        }

        var evidence = await ResolveAsync(
            connection, transaction, proposal.TenantId, proposal.OriginReportId,
            origin.JobId, origin.Attempt, configuredRepository, cancellationToken);
        if (evidence is null ||
            !string.Equals(evidence.TicketId, payload.TicketId, StringComparison.Ordinal))
        {
            await denyAsync("ticket_update_target_required");
            return;
        }

        // A backlink states a number that a stranger will read as this product's own claim about what
        // answers their incident. It is re-checked here, inside the proposal transaction, against the
        // audit projection the pull-request action wrote, so the number a person approves is one the
        // database vouches for rather than one a workflow computed and nobody verified since.
        if (isBacklink && !await HasConfirmedPullRequestAsync(
                connection, transaction, proposal, payload.PullRequestNumber!, cancellationToken))
        {
            await denyAsync("ticket_backlink_pull_request_unconfirmed");
        }
    }

    private static async Task<bool> HasConfirmedPullRequestAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        GovernedActionProposal proposal,
        string claimed,
        CancellationToken cancellationToken) =>
        string.Equals(
            await PostgresConfirmedPullRequestReader.ReadAsync(
                connection, transaction, proposal.TenantId, proposal.OriginReportId, cancellationToken),
            claimed,
            StringComparison.Ordinal);

    private static async Task<TicketUpdateEvidence?> ResolveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string tenantId,
        Guid originReportId,
        Guid jobId,
        int attempt,
        string? configuredRepository,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || originReportId == Guid.Empty ||
            configuredRepository is null)
        {
            return null;
        }

        await using var command = new NpgsqlCommand("""
            SELECT a.id, a.domain_ref, a.redacted_payload::text
            FROM incidentcompass.triage_reports r
            JOIN incidentcompass.faults f ON f.id = r.fault_id
            JOIN incidentcompass.triage_jobs j ON j.id = r.job_id
            JOIN incidentcompass.triage_evidence e ON e.report_id = r.id
            JOIN incidentcompass.triage_artifacts a ON a.id = e.artifact_id
            WHERE r.id = @report_id AND f.tenant_id = @tenant_id
              AND r.status = 'Completed' AND j.status = 'Succeeded'
              AND (@job_id = '00000000-0000-0000-0000-000000000000'::uuid OR j.id = @job_id)
              AND (@attempt = 0 OR j.attempt = @attempt)
              AND e.kind = 'RetrievedItem' AND a.kind = 'RetrievedItem'
              AND a.job_id = j.id AND a.attempt = j.attempt
              AND a.redacted_payload->>'evidenceKind' = 'ExistingTicket'
            ORDER BY a.id
            LIMIT 2;
            """, connection, transaction);
        command.AddParameter("report_id", originReportId);
        command.AddParameter("tenant_id", tenantId);
        command.AddParameter("job_id", jobId);
        command.AddParameter("attempt", attempt);
        var candidates = new List<TicketUpdateEvidence>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!ExistingTicketEvidenceShape.TryReadTicketId(
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetString(2),
                    configuredRepository,
                    out var ticketId))
            {
                return null;
            }

            candidates.Add(new TicketUpdateEvidence(reader.GetGuid(0), ticketId));
        }

        return candidates.Count == 1 ? candidates[0] : null;
    }
}
