using IncidentCompass.Domain.Incidents.Actions;
using IncidentCompass.Domain.Incidents.Statuses;

namespace IncidentCompass.Application.Governance.Tools;

internal sealed record ToolRulePolicyResult
{
    private ToolRulePolicyResult(
        TriageLedgerDecision decision,
        string reason,
        string? reasonCode,
        ActionExecutionMode? effectiveMode)
    {
        Decision = decision;
        Reason = reason;
        ReasonCode = reasonCode;
        EffectiveMode = effectiveMode;
    }

    public TriageLedgerDecision Decision { get; }

    /// <summary>
    /// The reason recorded in the ledger's <c>PolicyDecision</c> row. For a denial this is always
    /// <see cref="ReasonCode" />, optionally followed by "<c>: </c>" and a human detail.
    /// </summary>
    public string Reason { get; }

    /// <summary>
    /// The stable cause of a denial, from <see cref="ToolPolicyDenialReasons" />. It is null for an
    /// allowed or approval-required result: those carry an accumulated description of which rules
    /// matched, which is not a single cause and is not a taxonomy anything aggregates on.
    /// </summary>
    public string? ReasonCode { get; }

    public ActionExecutionMode? EffectiveMode { get; }

    public bool MayProceed => Decision != TriageLedgerDecision.Denied;

    public static ToolRulePolicyResult Allowed(
        string reason,
        ActionExecutionMode? mode = null) =>
        new(TriageLedgerDecision.Allowed, reason, reasonCode: null, mode);

    /// <summary>
    /// Denies with a stable <paramref name="reasonCode" /> and, where there is something
    /// caller-specific worth recording, a human <paramref name="reasonDetail" /> after it. Composing
    /// the stored reason here is what keeps every denial to one shape: a bare code, or the code
    /// followed by its detail. Callers must not pre-compose the two into one string.
    /// </summary>
    public static ToolRulePolicyResult Denied(string reasonCode, string? reasonDetail = null) =>
        new(
            TriageLedgerDecision.Denied,
            string.IsNullOrEmpty(reasonDetail) ? reasonCode : reasonCode + ": " + reasonDetail,
            reasonCode,
            effectiveMode: null);

    public static ToolRulePolicyResult ApprovalRequired(
        string reason,
        ActionExecutionMode? mode = null) =>
        new(TriageLedgerDecision.ApprovalRequired, reason, reasonCode: null, mode);
}
