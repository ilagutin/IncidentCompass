using IncidentCompass.Application.Core.Dispatching;
using IncidentCompass.Application.Core.Exceptions;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Domain.Incidents.Actions;

namespace IncidentCompass.Application.Governance.ActionApprovals.Propose;

internal sealed class ProposePostReportActionCommandHandler(
    IActionProposalRepository repository,
    ITriageConfigurationRepository configurationRepository,
    IAgentToolRegistry toolRegistry,
    IEnumerable<IExternalActionTool> externalTools) : IRequestHandler<ProposePostReportActionCommand, PostReportActionProposalResponse>
{
    private readonly IReadOnlyList<IExternalActionTool> externalTools = externalTools.ToArray();

    public async Task<PostReportActionProposalResponse> HandleAsync(
        ProposePostReportActionCommand request,
        CancellationToken cancellationToken)
    {
        if (!HasValidPreOriginIdentity(request))
        {
            return Denied("invalid_request", false);
        }

        var origin = await repository.FindSafeOriginAsync(
            request.TenantId, request.OriginReportId, cancellationToken);
        if (origin is null)
        {
            return Denied("origin_ineligible", false);
        }

        if (!origin.IsCompleted)
        {
            return await AuditDeniedAsync(request, null, "origin_insufficient_evidence", cancellationToken);
        }

        TriageConfiguration configuration;
        try
        {
            configuration = await configurationRepository.GetByHashAsync(origin.Job.ConfigHash, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or NotFoundException)
        {
            return await AuditDeniedAsync(request, null, "configuration_invalid", cancellationToken);
        }

        if (!toolRegistry.TryGet(request.ToolId, out var descriptor) ||
            descriptor.Capability != AgentToolCapability.ExternalAction)
        {
            return await AuditDeniedAsync(request, null, "tool_not_registered", cancellationToken);
        }

        var tool = FindExactTool(request.ToolId);
        if (tool is null || !MatchesDescriptor(tool, descriptor))
        {
            return await AuditDeniedAsync(request, descriptor.ToolId, "tool_registration_mismatch", cancellationToken);
        }

        ExternalActionPreparation preparation;
        try
        {
            var validation = tool.Validate(request.Arguments);
            if (!validation.IsValid)
            {
                return await AuditDeniedAsync(request, descriptor.ToolId, "arguments_invalid", cancellationToken);
            }

            preparation = tool.Prepare(validation.SanitizedArguments);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return await AuditDeniedAsync(request, descriptor.ToolId, "arguments_invalid", cancellationToken);
        }

        var prepared = new GovernedActionProposal(
            origin.TenantId, origin.ReportId, descriptor, request.ProposalKey,
            tool.AdapterBindingFingerprint, preparation.CanonicalPayload,
            preparation.ReviewSummary, configuration.Actions.ApprovalTtlMinutes,
            origin.EvidenceArtifactIds, configuration);

        try
        {
            ActionProposalValidator.ValidateAndComputePayloadHash(prepared);
            var stored = await repository.CreateGovernedAsync(prepared, cancellationToken);
            return new PostReportActionProposalResponse(
                stored.Action.State == ActionApprovalState.Approved
                    ? PostReportActionProposalOutcome.Approved
                    : PostReportActionProposalOutcome.Requested,
                stored.Action.State == ActionApprovalState.Approved ? "auto_approved" : "approval_required",
                stored.Action,
                stored.IsReplay,
                false);
        }
        catch (ActionProposalPolicyDeniedException exception)
        {
            return Denied(exception.ReasonCode, true);
        }
        catch (ActionProposalValidationException)
        {
            return await AuditDeniedAsync(request, descriptor.ToolId, "proposal_invalid", cancellationToken);
        }
    }

    private IExternalActionTool? FindExactTool(string toolId)
    {
        var matches = externalTools.Where(tool =>
            string.Equals(tool.Definition.Name, toolId, StringComparison.Ordinal)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool MatchesDescriptor(IExternalActionTool tool, AgentToolDescriptor descriptor) =>
        tool.Category == descriptor.Category &&
        string.Equals(tool.LogicalTargetId, descriptor.LogicalTargetId, StringComparison.Ordinal) &&
        ActionProposalValidator.IsLowerHexSha256(tool.AdapterBindingFingerprint);

    private async Task<PostReportActionProposalResponse> AuditDeniedAsync(
        ProposePostReportActionCommand request,
        string? auditedToolId,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        var audited = await repository.RecordDenialAsync(
            request.TenantId, request.OriginReportId, auditedToolId, reasonCode, cancellationToken);
        return Denied(reasonCode, audited);
    }

    private static bool HasValidPreOriginIdentity(ProposePostReportActionCommand request) =>
        !string.IsNullOrWhiteSpace(request.TenantId) &&
        request.OriginReportId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(request.ToolId) &&
        request.ToolId.Length <= AgentToolIdentity.MaximumCharacters &&
        !string.IsNullOrWhiteSpace(request.ProposalKey) &&
        request.ProposalKey.Length <= ActionApprovalLimits.MaximumProposalKeyCharacters;

    private static PostReportActionProposalResponse Denied(string reasonCode, bool audited) =>
        new(PostReportActionProposalOutcome.Denied, reasonCode, null, false, audited);
}
