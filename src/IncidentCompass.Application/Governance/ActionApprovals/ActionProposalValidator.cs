using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Domain.Incidents.Actions;

namespace IncidentCompass.Application.Governance.ActionApprovals;

public static class ActionProposalValidator
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string ValidateAndComputePayloadHash(PreparedActionProposal proposal)
    {
        ValidateCommon(
            proposal.TenantId, proposal.ToolId, proposal.ProposalKey,
            proposal.AdapterBindingFingerprint, proposal.LogicalTargetId,
            proposal.CanonicalPayload, proposal.ReviewSummary,
            proposal.ApprovalTtlMinutes, proposal.EvidenceArtifactIds);

        if (proposal.Mode == ActionExecutionMode.Disabled ||
            !Enum.IsDefined(proposal.Category) || !Enum.IsDefined(proposal.Mode))
        {
            throw new ActionProposalValidationException("Action proposal category or mode is invalid.");
        }

        if (StrictUtf8.GetByteCount(proposal.PolicyDecisionReason) is < 1 or > ActionApprovalLimits.MaximumSummaryBytes)
        {
            throw new ActionProposalValidationException("Action proposal policy summary exceeds its bound.");
        }

        return ActionApprovalContractV1.ComputePayloadSha256(proposal.CanonicalPayload);
    }

    public static string ValidateAndComputePayloadHash(GovernedActionProposal proposal)
    {
        if (proposal.RegisteredTool.Capability != AgentToolCapability.ExternalAction ||
            proposal.RegisteredTool.Category is null || proposal.RegisteredTool.LogicalTargetId is null)
        {
            throw new ActionProposalValidationException("Governed action registration is invalid.");
        }

        ValidateCommon(
            proposal.TenantId, proposal.ToolId, proposal.ProposalKey,
            proposal.AdapterBindingFingerprint, proposal.RegisteredTool.LogicalTargetId,
            proposal.CanonicalPayload, proposal.ReviewSummary,
            proposal.ApprovalTtlMinutes, proposal.EvidenceArtifactIds);
        return ActionApprovalContractV1.ComputePayloadSha256(proposal.CanonicalPayload);
    }

    private static void ValidateCommon(
        string tenantId,
        string toolId,
        string proposalKey,
        string bindingFingerprint,
        string logicalTargetId,
        byte[] canonicalPayload,
        string reviewSummary,
        int approvalTtlMinutes,
        IReadOnlyList<Guid> evidenceArtifactIds)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || !AgentToolIdentity.IsValid(toolId) ||
            string.IsNullOrWhiteSpace(proposalKey) ||
            proposalKey.Length > ActionApprovalLimits.MaximumProposalKeyCharacters)
        {
            throw new ActionProposalValidationException("Action proposal identity is invalid.");
        }

        if (logicalTargetId.Length is < 1 or > ActionApprovalLimits.MaximumLogicalTargetCharacters ||
            !IsLowerHexSha256(bindingFingerprint))
        {
            throw new ActionProposalValidationException("Action proposal target binding is invalid.");
        }

        if (approvalTtlMinutes is < ActionApprovalLimits.MinimumTtlMinutes or > ActionApprovalLimits.MaximumTtlMinutes ||
            evidenceArtifactIds.Count is < ActionApprovalLimits.MinimumEvidenceCount or > ActionApprovalLimits.MaximumEvidenceCount ||
            evidenceArtifactIds.Distinct().Count() != evidenceArtifactIds.Count)
        {
            throw new ActionProposalValidationException("Action proposal lifetime or evidence count is invalid.");
        }

        if (canonicalPayload.Length is < 1 or > ActionApprovalLimits.MaximumPayloadBytes ||
            StrictUtf8.GetByteCount(reviewSummary) is < 1 or > ActionApprovalLimits.MaximumSummaryBytes)
        {
            throw new ActionProposalValidationException("Action proposal payload or summary exceeds its bound.");
        }

        ValidateCanonicalJson(canonicalPayload);
    }

    public static bool IsLowerHexSha256(string value) =>
        value.Length == 64 && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void ValidateCanonicalJson(byte[] payload)
    {
        try
        {
            var text = StrictUtf8.GetString(payload);
            var node = JsonNode.Parse(text) ?? throw new JsonException();
            var canonical = CanonicalJsonSerializer.Canonicalize(node);
            if (!payload.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(canonical)))
            {
                throw new ActionProposalValidationException("Action proposal payload must be canonical JSON.");
            }
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
        {
            throw new ActionProposalValidationException("Action proposal payload must be valid canonical UTF-8 JSON.", exception);
        }
    }
}
