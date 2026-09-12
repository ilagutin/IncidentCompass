using System.Text;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Domain.Incidents.Actions;
using IncidentCompass.Infrastructure.Postgres;
using Npgsql;

namespace IncidentCompass.Infrastructure.Governance.ActionApprovals;

internal static class PostgresActionResultWriter
{
    public static async Task<Guid> InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ActionApprovalRecord action,
        byte[] resultPayload,
        string resultSummary,
        string? failureCode,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var artifactId = Guid.NewGuid();
        var payload = new JsonObject
        {
            ["actionId"] = action.Id,
            ["status"] = (failureCode is null ? ActionApprovalState.Executed : ActionApprovalState.Failed).ToStorageValue(),
            ["result"] = JsonNode.Parse(resultPayload),
            ["resultSummary"] = resultSummary,
            ["failureCode"] = failureCode
        }.ToJsonString();

        // redaction_applied is left unwritten, so the row carries NULL: no redaction pass ran on the
        // way here. This payload is the dispatched action's own result and backend-derived status
        // rather than connector text meeting the redactor, and NULL is the column's "no boundary
        // recorded an outcome" value, which is deliberately not the same claim as false.
        // ActionResult is also not a citable evidence kind, so no report marker reads this row
        // today; the reason NULL is correct is the first one, not the second.
        //
        // The column is named redacted_payload, and for this one writer that name describes the
        // column rather than these bytes. `result` is whatever the action adapter put in
        // ExternalActionExecutionResult.CanonicalResult, and nothing between that field and this
        // column inspects it. Every shipped adapter composes it out of backend-derived values only -
        // identifiers parsed out of a provider response, a closed outcome code, the configured
        // repository, tree identities, counts and booleans - and none copies a provider response
        // body into it, so what reaches this column today is bounded by what the adapter chose to
        // compose. The gap is structural rather than present, and nothing here could close it: the
        // write runs inside the dispatch transaction, deep in Infrastructure, with no triage
        // configuration and therefore no RedactionSettings in scope. So the honest move is to say so
        // where an operator reads about the column instead of quietly implying a pass that did not
        // run. That statement is docs/security-model.md, "Redaction And Pseudonymization", under
        // "The action-result exception". Change what an adapter puts in CanonicalResult and that
        // section is wrong.
        await using var command = new NpgsqlCommand("""
            INSERT INTO incidentcompass.triage_artifacts (
                id, job_id, attempt, kind, domain_ref, redacted_payload, content_hash, created_at_utc)
            VALUES (@id, @job_id, @attempt, @kind, @domain_ref, @payload, @content_hash, @created_at_utc);
            """, connection, transaction);
        command.AddParameter("id", artifactId);
        command.AddParameter("job_id", action.JobId);
        command.AddParameter("attempt", action.Attempt);
        command.AddParameter("kind", ArtifactKind.ActionResult.ToDbString());
        command.AddParameter("domain_ref", "action:" + action.Id);
        command.AddJsonbParameter("payload", payload);
        command.AddParameter(
            "content_hash",
            ActionApprovalContractV1.ComputePayloadSha256(Encoding.UTF8.GetBytes(payload)));
        command.AddParameter("created_at_utc", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return artifactId;
    }
}
