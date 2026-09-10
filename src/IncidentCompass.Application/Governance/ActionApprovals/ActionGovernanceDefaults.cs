using IncidentCompass.Domain.Incidents.Actions;

namespace IncidentCompass.Application.Governance.ActionApprovals;

/// <summary>
/// The governance floors that hold when configuration says nothing: which action category may be
/// auto-approved, and how a global execution mode composes with a per-tool override.
/// </summary>
/// <remarks>
/// Both live where the decision paths that read them can share one answer. The tool rule engine's
/// premise is that policy lives in configuration, so a decision written in C# inside it reads as an
/// accident; these are deliberately not configuration keys, because configuration can only ever
/// tighten them and never loosen them.
/// </remarks>
internal static class ActionGovernanceDefaults
{
    /// <summary>
    /// The one action category that may execute without an approval when no rule and no configuration
    /// setting demands one. A notification writes nothing an operator would have to undo; every other
    /// category is an external state change, so it requires approval by default even where
    /// configuration is silent. Configuration tightens this through <c>Actions.RequireApprovalForAll</c>
    /// or a <c>requires_approval</c> rule, and has no way to widen it.
    /// </summary>
    public const ActionCategory AutoApprovableCategory = ActionCategory.Notification;

    /// <summary>
    /// The effective execution mode for a tool: the configured global mode, tightened by a per-tool
    /// override when one is present. An override can only restrict, never loosen.
    /// </summary>
    public static ActionExecutionMode EffectiveMode(string globalMode, string? overrideMode)
    {
        var global = ActionApprovalVocabulary.ParseMode(globalMode);
        var perTool = overrideMode is null ? global : ActionApprovalVocabulary.ParseMode(overrideMode);
        return MostRestrictive(global, perTool);
    }

    /// <summary>
    /// The more restrictive of two execution modes, written as an explicit ladder.
    /// </summary>
    /// <remarks>
    /// This was a maximum over the enum's ordinal values, which made the declaration order of
    /// <see cref="ActionExecutionMode" /> load-bearing governance: reordering it reads as a cosmetic
    /// edit and would silently let a disabled tool resolve to <c>Live</c>. Stating the precedence
    /// where the decision is made removes that dependency instead of documenting it.
    /// </remarks>
    public static ActionExecutionMode MostRestrictive(ActionExecutionMode first, ActionExecutionMode second) =>
        (first, second) switch
        {
            (ActionExecutionMode.Disabled, _) or (_, ActionExecutionMode.Disabled) => ActionExecutionMode.Disabled,
            (ActionExecutionMode.DryRun, _) or (_, ActionExecutionMode.DryRun) => ActionExecutionMode.DryRun,
            _ => ActionExecutionMode.Live
        };
}
