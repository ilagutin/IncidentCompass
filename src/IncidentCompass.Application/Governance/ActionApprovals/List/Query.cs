using IncidentCompass.Application.Core.Dispatching;

namespace IncidentCompass.Application.Governance.ActionApprovals.List;

public sealed record ListActionApprovalsQuery(
    string? Status,
    int? Limit,
    string? Cursor,
    string? ExternalResourceKind = null,
    string? ExternalResourceId = null,
    Guid? FaultId = null) : IRequest<ActionApprovalListResponse>;
