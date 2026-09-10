using System.Text;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Governance.ActionApprovals.Testing;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Domain.Incidents.Statuses;
using IncidentCompass.Infrastructure.Postgres;
using Npgsql;

namespace IncidentCompass.Infrastructure.Governance.ActionApprovals;

internal static class PostgresActionProposalWriter
{
    public static async Task SupersedeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ActionApprovalRecord action,
        CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes("{\"code\":\"origin_report_superseded\"}");
        const string summary = "Origin report was superseded before notification dispatch.";
        await using var command = new NpgsqlCommand("""
            UPDATE incidentcompass.action_approvals
            SET state = 'failed', result_payload = @payload, result_summary = @summary,
                failure_code = 'origin_report_superseded', completed_at_utc = clock_timestamp()
            WHERE id = @id AND state IN ('requested', 'approved') AND dispatch_started_at IS NULL
            RETURNING completed_at_utc;
            """, connection, transaction);
        command.AddParameter("payload", payload);
        command.AddParameter("summary", summary);
        command.AddParameter("id", action.Id);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null)
        {
            return;
        }

        var completed = value is DateTimeOffset offset
            ? offset
            : new DateTimeOffset(DateTime.SpecifyKind((DateTime)value, DateTimeKind.Utc));

        var artifactId = await PostgresActionResultWriter.InsertAsync(
            connection, transaction, action, payload, summary,
            "origin_report_superseded", completed, cancellationToken);
        var actionOrigin = await PostgresActionOriginContext.ReadAsync(
            connection, transaction, action, cancellationToken);
        await PostgresActionLedgerWriter.InsertAsync(
            connection, transaction, actionOrigin, TriageLedgerEventType.ActionCompleted,
            action.ToolId, "system:supersession", "origin_report_superseded", null,
            TriageLedgerToolStatus.Failed, "artifact:" + artifactId, completed, cancellationToken);
    }

    public static async Task InsertActionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ActionApprovalRecord action,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO incidentcompass.action_approvals (
                id, tenant_id, origin_report_id, fault_id, job_id, attempt, tool_id, proposal_key,
                category, mode, logical_target_id, adapter_binding_fingerprint,
                approval_contract_version, provenance_sha256, state, canonical_payload,
                payload_sha256, approval_sha256, proposal_artifact_id, review_summary,
                created_at_utc, expires_at_utc, decision_actor, decision_at_utc)
            VALUES (
                @id, @tenant_id, @origin_report_id, @fault_id, @job_id, @attempt, @tool_id, @proposal_key,
                @category, @mode, @logical_target_id, @adapter_binding_fingerprint,
                @approval_contract_version, @provenance_sha256, @state, @canonical_payload,
                @payload_sha256, @approval_sha256, @proposal_artifact_id, @review_summary,
                @created_at_utc, @expires_at_utc, @decision_actor, @decision_at_utc);
            """, connection, transaction);
        command.AddParameter("id", action.Id);
        command.AddParameter("tenant_id", action.TenantId);
        command.AddParameter("origin_report_id", action.OriginReportId);
        command.AddParameter("fault_id", action.FaultId);
        command.AddParameter("job_id", action.JobId);
        command.AddParameter("attempt", action.Attempt);
        command.AddParameter("tool_id", action.ToolId);
        command.AddParameter("proposal_key", action.ProposalKey);
        command.AddParameter("category", action.Category.ToStorageValue());
        command.AddParameter("mode", action.Mode.ToStorageValue());
        command.AddParameter("logical_target_id", action.LogicalTargetId);
        command.AddParameter("adapter_binding_fingerprint", action.AdapterBindingFingerprint);
        command.AddParameter("approval_contract_version", action.ApprovalContractVersion);
        command.AddParameter("provenance_sha256", action.ProvenanceSha256);
        command.AddParameter("state", action.State.ToStorageValue());
        command.AddParameter("canonical_payload", action.CanonicalPayload);
        command.AddParameter("payload_sha256", action.PayloadSha256);
        command.AddParameter("approval_sha256", action.ApprovalSha256);
        command.AddParameter("proposal_artifact_id", action.ProposalArtifactId);
        command.AddParameter("review_summary", action.ReviewSummary);
        command.AddParameter("created_at_utc", action.CreatedAtUtc);
        command.AddParameter("expires_at_utc", action.ExpiresAtUtc);
        command.AddParameter("decision_actor", action.DecisionActor);
        command.AddParameter("decision_at_utc", action.DecisionAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task InsertArtifactAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ActionApprovalRecord action,
        CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["actionId"] = action.Id,
            ["approvalContractVersion"] = action.ApprovalContractVersion,
            ["originReportId"] = action.OriginReportId,
            ["toolId"] = action.ToolId,
            ["category"] = action.Category.ToStorageValue(),
            ["mode"] = action.Mode.ToStorageValue(),
            ["logicalTargetId"] = action.LogicalTargetId,
            ["canonicalPayload"] = JsonNode.Parse(action.CanonicalPayload),
            ["payloadSha256"] = action.PayloadSha256,
            ["provenanceSha256"] = action.ProvenanceSha256,
            ["approvalSha256"] = action.ApprovalSha256,
            ["reviewSummary"] = action.ReviewSummary,
            ["expiresAtUtc"] = action.ExpiresAtUtc
        };
        var canonical = payload.ToJsonString();

        // redaction_applied is left unwritten, so the row carries NULL: no redaction pass ran on the
        // way here. This payload is assembled from backend-derived approval state rather than from
        // connector text, and NULL is the column's "no boundary recorded an outcome" value, which is
        // deliberately not the same claim as false. ProposedAction is also not a citable evidence
        // kind, so no report marker reads this row today; the reason NULL is correct is the first
        // one, not the second.
        await using var command = new NpgsqlCommand("""
            INSERT INTO incidentcompass.triage_artifacts (
                id, job_id, attempt, kind, domain_ref, redacted_payload, content_hash, created_at_utc)
            VALUES (
                @id, @job_id, @attempt, @kind, @domain_ref, @payload, @content_hash, @created_at_utc);
            """, connection, transaction);
        command.AddParameter("id", action.ProposalArtifactId);
        command.AddParameter("job_id", action.JobId);
        command.AddParameter("attempt", action.Attempt);
        command.AddParameter("kind", ArtifactKind.ProposedAction.ToDbString());
        command.AddParameter("domain_ref", "action:" + action.Id);
        command.AddJsonbParameter("payload", canonical);
        command.AddParameter("content_hash", ActionApprovalContractV1.ComputePayloadSha256(Encoding.UTF8.GetBytes(canonical)));
        command.AddParameter("created_at_utc", action.CreatedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task InsertProvenanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid actionId,
        IReadOnlyList<ActionApprovalProvenance> provenance,
        IActionApprovalTransactionFaultInjector faultInjector,
        CancellationToken cancellationToken)
    {
        foreach (var item in provenance)
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO incidentcompass.action_approval_provenance (
                    action_id, ordinal, source_type, source_id, artifact_kind, trust_class)
                VALUES (@action_id, @ordinal, @source_type, @source_id, @artifact_kind, @trust_class);
                """, connection, transaction);
            command.AddParameter("action_id", actionId);
            command.AddParameter("ordinal", item.Ordinal);
            command.AddParameter("source_type", item.SourceType);
            command.AddParameter("source_id", item.SourceId);
            command.AddParameter("artifact_kind", item.ArtifactKind);
            command.AddParameter("trust_class", item.TrustClass.ToStorageValue());
            await command.ExecuteNonQueryAsync(cancellationToken);
            await faultInjector.AfterProvenanceRowAsync(item.Ordinal, cancellationToken);
        }
    }
}
