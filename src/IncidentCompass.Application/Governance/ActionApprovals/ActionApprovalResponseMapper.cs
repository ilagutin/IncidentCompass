using System.Text.Json;
using IncidentCompass.Application.Governance.ActionApprovals.Get;
using IncidentCompass.Application.Governance.ActionApprovals.List;

namespace IncidentCompass.Application.Governance.ActionApprovals;

internal static class ActionApprovalResponseMapper
{
    public static ActionApprovalListItemResponse ToListItem(ActionApprovalRecord action) =>
        new(
            action.Id,
            action.State.ToStorageValue(),
            action.ApprovalContractVersion,
            action.OriginReportId,
            action.FaultId,
            action.ToolId,
            action.Category.ToStorageValue(),
            action.Mode.ToStorageValue(),
            action.LogicalTargetId,
            action.PayloadSha256,
            action.ProvenanceSha256,
            action.ApprovalSha256,
            action.ReviewSummary,
            action.ProvenanceCount,
            action.CreatedAtUtc,
            action.ExpiresAtUtc,
            action.DecisionAtUtc,
            action.CompletedAtUtc,
            action.AuditProjection?.ResourceKind,
            action.AuditProjection?.ResourceId,
            action.AuditProjection?.BeforeState,
            action.AuditProjection?.AfterState);

    public static ActionApprovalDetailsResponse ToDetails(
        ActionApprovalRecord action,
        IReadOnlyList<ActionApprovalProvenance> provenance)
    {
        using var document = JsonDocument.Parse(action.CanonicalPayload);
        return new ActionApprovalDetailsResponse(
            action.Id,
            action.State.ToStorageValue(),
            action.ApprovalContractVersion,
            action.OriginReportId,
            action.ToolId,
            action.Category.ToStorageValue(),
            action.Mode.ToStorageValue(),
            action.LogicalTargetId,
            document.RootElement.Clone(),
            action.PayloadSha256,
            action.ProvenanceSha256,
            action.ApprovalSha256,
            action.ReviewSummary,
            action.CreatedAtUtc,
            action.ExpiresAtUtc,
            action.DecisionAtUtc,
            action.RejectionReason,
            action.CompletedAtUtc,
            action.ResultSummary,
            action.FailureCode,
            action.AuditProjection?.ResourceKind,
            action.AuditProjection?.ResourceId,
            action.AuditProjection?.BeforeState,
            action.AuditProjection?.AfterState,
            provenance.Select(static item => new ActionApprovalProvenanceResponse(
                item.SourceType,
                item.SourceId,
                item.ArtifactKind,
                item.TrustClass.ToStorageValue())).ToArray());
    }
}
