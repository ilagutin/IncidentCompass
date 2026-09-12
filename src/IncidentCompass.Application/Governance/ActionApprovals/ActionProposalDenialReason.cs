using IncidentCompass.Application.Governance.Tools;

namespace IncidentCompass.Application.Governance.ActionApprovals;

/// <summary>
/// Maps a tool-policy denial's stable reason code onto the public post-report denial vocabulary.
/// </summary>
/// <remarks>
/// This used to recover the cause by matching prefixes of the engine's reason prose, which made the
/// wording of a denial message part of the contract: rephrasing one silently collapsed a specific
/// denial into <c>policy_denied</c>. It now switches on the policy result's own <c>ReasonCode</c>, so
/// only a deliberate change of code changes the public reason, and a code with no public equivalent
/// still falls back to <c>policy_denied</c>.
/// </remarks>
public static class ActionProposalDenialReason
{
    public static string NormalizePolicy(string? reasonCode) => reasonCode switch
    {
        ToolPolicyDenialReasons.ActionNotGranted => "action_not_granted",
        ToolPolicyDenialReasons.ActionRegistrationMismatch => "tool_registration_mismatch",
        ToolPolicyDenialReasons.ActionDisabled => "action_disabled",
        ToolPolicyDenialReasons.RateCapExceeded => "rate_cap_exceeded",
        ToolPolicyDenialReasons.PreconditionUnsatisfied => "precondition_unsatisfied",
        _ => "policy_denied"
    };
}
