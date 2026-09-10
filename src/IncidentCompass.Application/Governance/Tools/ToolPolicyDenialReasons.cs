namespace IncidentCompass.Application.Governance.Tools;

/// <summary>
/// The closed set of stable reason codes a tool-policy denial can carry.
/// </summary>
/// <remarks>
/// A denial's reason is written to the durable ledger as <c>code</c> or <c>code: detail</c>, so the
/// cause is always the leading token and denials aggregate by cause without parsing prose. These
/// codes name the engine's own decision; the public post-report denial vocabulary is derived from
/// them by <c>ActionProposalDenialReason</c> rather than by matching on reason text.
/// </remarks>
internal static class ToolPolicyDenialReasons
{
    internal const string UnknownOrUnconfiguredTool = "unknown_or_unconfigured_tool";
    internal const string ToolNotGrantedToRole = "tool_not_granted_to_role";
    internal const string ToolNotRegistered = "tool_not_registered";
    internal const string ToolArgumentsInvalid = "tool_arguments_invalid";
    internal const string ActionNotGranted = "action_not_granted";
    internal const string ActionRegistrationMismatch = "action_registration_mismatch";
    internal const string ActionDisabled = "action_disabled";
    internal const string RateCapExceeded = "rate_cap_exceeded";
    internal const string RateCapMissingMax = "rate_cap_missing_max";
    internal const string PreconditionUnsatisfied = "precondition_unsatisfied";
    internal const string PreconditionMissingPrerequisite = "precondition_missing_prerequisite";
    internal const string UnknownRuleType = "unknown_rule_type";
}
