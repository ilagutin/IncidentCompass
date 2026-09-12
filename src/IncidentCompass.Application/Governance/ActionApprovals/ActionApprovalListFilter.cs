using IncidentCompass.Domain.Incidents.Actions;

namespace IncidentCompass.Application.Governance.ActionApprovals;

public sealed record ActionApprovalListFilter(
    ActionApprovalState? Status,
    DateTimeOffset? BeforeCreatedAtUtc,
    Guid? BeforeActionId,
    int Limit,
    string? ExternalResourceKind = null,
    string? ExternalResourceId = null,
    Guid? FaultId = null);
