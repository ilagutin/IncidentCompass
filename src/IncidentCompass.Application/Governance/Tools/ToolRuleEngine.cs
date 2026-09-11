using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Domain.Incidents.Actions;
using IncidentCompass.Domain.Incidents.Statuses;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IncidentCompass.Application.Governance.Tools;

internal sealed partial class ToolRuleEngine(
    ITriageLedgerReader ledgerReader,
    ILogger<ToolRuleEngine>? logger = null)
{
    private readonly ILogger logger = logger ?? NullLogger<ToolRuleEngine>.Instance;

    public async Task<ToolRulePolicyResult> DecideImmediateAsync(
        TriageJob job,
        TriageConfiguration configuration,
        string roleName,
        string toolName,
        CancellationToken cancellationToken)
    {
        var result = await DecideImmediateCoreAsync(job, configuration, roleName, toolName, cancellationToken);
        LogImmediateDecision(job, roleName, toolName, result);
        return result;
    }

    public async Task<ToolRulePolicyResult> DecideExternalAsync(
        TriageConfiguration configuration,
        AgentToolDescriptor registeredTool,
        IToolRuleFactReader factReader,
        CancellationToken cancellationToken)
    {
        var result = await DecideExternalCoreAsync(configuration, registeredTool, factReader, cancellationToken);
        LogExternalDecision(registeredTool.ToolId, result);
        return result;
    }

    private Task<ToolRulePolicyResult> DecideImmediateCoreAsync(
        TriageJob job,
        TriageConfiguration configuration,
        string roleName,
        string toolName,
        CancellationToken cancellationToken)
    {
        if (!configuration.Tools.TryGetValue(toolName, out var settings) ||
            !string.Equals(settings.Kind, "internal", StringComparison.Ordinal))
        {
            return Task.FromResult(
                ToolRulePolicyResult.Denied(ToolPolicyDenialReasons.UnknownOrUnconfiguredTool));
        }

        if (!configuration.Roles.TryGetValue(roleName, out var role) ||
            !role.Tools.Contains(toolName, StringComparer.Ordinal))
        {
            return Task.FromResult(
                ToolRulePolicyResult.Denied(ToolPolicyDenialReasons.ToolNotGrantedToRole));
        }

        return EvaluateRulesAsync(
            configuration.Rules,
            toolName,
            new TriageLedgerToolRuleFactReader(ledgerReader, job),
            cancellationToken);
    }

    private async Task<ToolRulePolicyResult> DecideExternalCoreAsync(
        TriageConfiguration configuration,
        AgentToolDescriptor registeredTool,
        IToolRuleFactReader factReader,
        CancellationToken cancellationToken)
    {
        if (registeredTool.Capability != AgentToolCapability.ExternalAction ||
            !configuration.Tools.TryGetValue(registeredTool.ToolId, out var settings) ||
            !string.Equals(settings.Kind, "external_action", StringComparison.Ordinal) ||
            !configuration.Actions.AllowedTools.Contains(registeredTool.ToolId, StringComparer.Ordinal))
        {
            return ToolRulePolicyResult.Denied(ToolPolicyDenialReasons.ActionNotGranted);
        }

        if (!MatchesRegistration(settings, registeredTool))
        {
            return ToolRulePolicyResult.Denied(ToolPolicyDenialReasons.ActionRegistrationMismatch);
        }

        var effectiveMode = ActionGovernanceDefaults.EffectiveMode(
            configuration.Actions.DefaultMode, settings.Mode);
        if (effectiveMode == ActionExecutionMode.Disabled)
        {
            return ToolRulePolicyResult.Denied(ToolPolicyDenialReasons.ActionDisabled);
        }

        var rules = await EvaluateRulesAsync(
            configuration.Rules, registeredTool.ToolId, factReader, cancellationToken);
        if (!rules.MayProceed)
        {
            return rules;
        }

        var approvalRequired = rules.Decision == TriageLedgerDecision.ApprovalRequired ||
            configuration.Actions.RequireApprovalForAll ||
            registeredTool.Category != ActionGovernanceDefaults.AutoApprovableCategory;
        var reason = approvalRequired
            ? CombineReason(rules.Reason, "approval required by action ceiling")
            : rules.Reason;
        return approvalRequired
            ? ToolRulePolicyResult.ApprovalRequired(reason, effectiveMode)
            : ToolRulePolicyResult.Allowed(reason, effectiveMode);
    }

    private async Task<ToolRulePolicyResult> EvaluateRulesAsync(
        IEnumerable<TriageRuleSettings> configuredRules,
        string toolName,
        IToolRuleFactReader factReader,
        CancellationToken cancellationToken)
    {
        var decision = TriageLedgerDecision.Allowed;
        var reasons = new List<string>();
        foreach (var rule in MatchingRules(configuredRules, toolName))
        {
            // The scope is parsed once, here, for both governance paths. A window this backend does
            // not evaluate denies for the same reason an unknown rule type does: a rule that cannot
            // be applied must not be applied by guessing at what it meant. Doing it before the
            // switch also means no fact reader is ever handed a scope nobody decided.
            if (!ToolRuleScopes.TryParse(rule.Scope, out var scope))
            {
                return ToolRulePolicyResult.Denied(
                    ToolPolicyDenialReasons.UnknownRuleScope,
                    $"'{rule.Scope}' configured for {toolName}");
            }

            switch (rule.Type)
            {
                case TriageRuleTypes.RateCap:
                    if (rule.Max is not > 0)
                    {
                        return ToolRulePolicyResult.Denied(
                            ToolPolicyDenialReasons.RateCapMissingMax,
                            $"rate_cap rule for {toolName} has no positive Max in {rule.Scope} scope");
                    }

                    var max = rule.Max.Value;
                    var count = await factReader.CountAcceptedUsesAsync(
                        toolName, scope, cancellationToken);
                    if (count >= max)
                    {
                        return ToolRulePolicyResult.Denied(
                            ToolPolicyDenialReasons.RateCapExceeded,
                            $"{toolName} used {count}/{max} in {rule.Scope} scope");
                    }

                    reasons.Add($"rate_cap {count}/{max} in {rule.Scope} scope");
                    break;
                case TriageRuleTypes.Precondition:
                    var prerequisite = rule.RequiresSuccessfulToolResult;
                    if (string.IsNullOrWhiteSpace(prerequisite))
                    {
                        return ToolRulePolicyResult.Denied(
                            ToolPolicyDenialReasons.PreconditionMissingPrerequisite,
                            $"precondition rule for {toolName} names no prerequisite tool");
                    }

                    if (!await factReader.HasSuccessfulToolResultAsync(
                            prerequisite, scope, cancellationToken))
                    {
                        return ToolRulePolicyResult.Denied(
                            ToolPolicyDenialReasons.PreconditionUnsatisfied,
                            $"{prerequisite} has no successful ToolResult in {rule.Scope} scope");
                    }

                    reasons.Add($"precondition satisfied by {prerequisite} in {rule.Scope} scope");
                    break;
                case TriageRuleTypes.RequiresApproval:
                    decision = TriageLedgerDecision.ApprovalRequired;
                    reasons.Add("approval required by rule");
                    break;
                case TriageRuleTypes.Grounding:
                    reasons.Add("grounding required by rule");
                    break;
                default:
                    return ToolRulePolicyResult.Denied(
                        ToolPolicyDenialReasons.UnknownRuleType,
                        $"'{rule.Type}' configured for {toolName}");
            }
        }

        var reason = reasons.Count == 0 ? "no matching rule denied execution" : string.Join("; ", reasons);
        return decision == TriageLedgerDecision.ApprovalRequired
            ? ToolRulePolicyResult.ApprovalRequired(reason)
            : ToolRulePolicyResult.Allowed(reason);
    }

    private static bool MatchesRegistration(TriageToolSettings settings, AgentToolDescriptor registeredTool) =>
        string.Equals(settings.Category, registeredTool.Category?.ToStorageValue(), StringComparison.Ordinal) &&
        string.Equals(settings.LogicalTargetId, registeredTool.LogicalTargetId, StringComparison.Ordinal);

    private static string CombineReason(string first, string second) =>
        string.IsNullOrWhiteSpace(first) ? second : first + "; " + second;

    private static IEnumerable<TriageRuleSettings> MatchingRules(
        IEnumerable<TriageRuleSettings> rules,
        string toolName) => rules.Where(rule =>
            string.Equals(rule.Tool, "*", StringComparison.Ordinal) ||
            string.Equals(rule.Tool, toolName, StringComparison.Ordinal));

    private void LogImmediateDecision(
        TriageJob job,
        string roleName,
        string toolName,
        ToolRulePolicyResult result)
    {
        switch (result.Decision)
        {
            case TriageLedgerDecision.Denied:
                LogImmediateToolDenied(logger, job.Id, job.Attempt, roleName, toolName, result.Reason);
                break;
            case TriageLedgerDecision.ApprovalRequired:
                LogImmediateToolApprovalRequired(logger, job.Id, job.Attempt, roleName, toolName, result.Reason);
                break;
            default:
                LogImmediateToolAllowed(logger, job.Id, job.Attempt, roleName, toolName, result.Reason);
                break;
        }
    }

    private void LogExternalDecision(string toolId, ToolRulePolicyResult result)
    {
        switch (result.Decision)
        {
            case TriageLedgerDecision.Denied:
                LogPostReportActionDenied(logger, toolId, result.Reason);
                break;
            case TriageLedgerDecision.ApprovalRequired:
                LogPostReportActionApprovalRequired(logger, toolId, result.EffectiveMode, result.Reason);
                break;
            default:
                LogPostReportActionAllowed(logger, toolId, result.EffectiveMode, result.Reason);
                break;
        }
    }

    [LoggerMessage(
        EventId = 3501,
        Level = LogLevel.Debug,
        Message = "Tool policy allowed {ToolName} for role {Role} on triage job {JobId} attempt {Attempt}: {PolicyReason}.")]
    private static partial void LogImmediateToolAllowed(
        ILogger logger,
        Guid jobId,
        int attempt,
        string role,
        string toolName,
        string policyReason);

    [LoggerMessage(
        EventId = 3502,
        Level = LogLevel.Warning,
        Message = "Tool policy denied {ToolName} for role {Role} on triage job {JobId} attempt {Attempt}: {PolicyReason}.")]
    private static partial void LogImmediateToolDenied(
        ILogger logger,
        Guid jobId,
        int attempt,
        string role,
        string toolName,
        string policyReason);

    [LoggerMessage(
        EventId = 3503,
        Level = LogLevel.Information,
        Message = "Tool policy requires approval for {ToolName} for role {Role} on triage job {JobId} attempt {Attempt}: {PolicyReason}.")]
    private static partial void LogImmediateToolApprovalRequired(
        ILogger logger,
        Guid jobId,
        int attempt,
        string role,
        string toolName,
        string policyReason);

    [LoggerMessage(
        EventId = 3511,
        Level = LogLevel.Information,
        Message = "Post-report action policy allowed {ToolId} in mode {EffectiveMode}: {PolicyReason}.")]
    private static partial void LogPostReportActionAllowed(
        ILogger logger,
        string toolId,
        ActionExecutionMode? effectiveMode,
        string policyReason);

    [LoggerMessage(
        EventId = 3512,
        Level = LogLevel.Warning,
        Message = "Post-report action policy denied {ToolId}: {PolicyReason}.")]
    private static partial void LogPostReportActionDenied(
        ILogger logger,
        string toolId,
        string policyReason);

    [LoggerMessage(
        EventId = 3513,
        Level = LogLevel.Information,
        Message = "Post-report action policy requires approval for {ToolId} in mode {EffectiveMode}: {PolicyReason}.")]
    private static partial void LogPostReportActionApprovalRequired(
        ILogger logger,
        string toolId,
        ActionExecutionMode? effectiveMode,
        string policyReason);
}
