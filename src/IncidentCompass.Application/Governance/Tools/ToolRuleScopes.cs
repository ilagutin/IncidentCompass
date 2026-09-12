namespace IncidentCompass.Application.Governance.Tools;

/// <summary>
/// The single translation between a configured scope string and a <see cref="ToolRuleScope" />, and
/// the single answer to the only question an adapter has to ask about one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The rule engine is the one tool-policy decision path for immediate worker
/// reads and for backend-owned post-report action proposals, but the scope on a rule used to reach
/// each path's fact reader as a raw string, and each reader mapped it itself. The two mappings
/// disagreed, and neither refused a scope it did not recognize: one silently narrowed an unknown
/// scope to the current attempt, the other silently widened it to the whole job. The same rule,
/// evaluated on the two paths the engine unifies, could therefore allow on one and deny on the
/// other. One of them also carried a <c>fault</c> branch that nothing could reach, which is dead
/// code that does not know it is dead.
/// </para>
/// <para>
/// <b>What an unrecognized scope means.</b> It denies. A scope the backend cannot evaluate is a rule
/// the backend cannot apply, and applying an unevaluated rule by picking a window for it is the
/// failure mode the engine already refuses for an unknown rule type. Configuration load validation
/// restricts the scope to the same two values, so this is the second boundary rather than the first,
/// and the loop must not depend on having been handed a validated configuration: a rehydrated
/// attempt snapshot reaches the engine without passing the loader again.
/// </para>
/// <para>
/// <b>Why a third caller cannot invent a third answer.</b> A fact reader is handed the parsed value
/// and never the string, so it has nothing to guess about. The one question the window comes down to
/// - whether it is narrowed to a single attempt - is answered here, once, by
/// <see cref="NarrowsToAttempt" />, and both adapters call it instead of switching for themselves.
/// A member added to the enum without an answer here throws at that one place rather than being
/// quietly treated as job scope in however many readers exist by then.
/// </para>
/// </remarks>
public static class ToolRuleScopes
{
    /// <summary>The configured spelling of <see cref="ToolRuleScope.Attempt" />.</summary>
    public const string AttemptName = "attempt";

    /// <summary>The configured spelling of <see cref="ToolRuleScope.Job" />.</summary>
    public const string JobName = "job";

    /// <summary>
    /// Parses a configured scope string, or reports that it names no window this backend evaluates.
    /// </summary>
    public static bool TryParse(string? scope, out ToolRuleScope parsed)
    {
        switch (scope)
        {
            case AttemptName:
                parsed = ToolRuleScope.Attempt;
                return true;
            case JobName:
                parsed = ToolRuleScope.Job;
                return true;
            default:
                parsed = ToolRuleScope.Attempt;
                return false;
        }
    }

    /// <summary>
    /// Whether the window is narrowed to the current attempt. Every fact reader already counts
    /// within one job, so this is the whole of what a scope changes for them.
    /// </summary>
    public static bool NarrowsToAttempt(ToolRuleScope scope) => scope switch
    {
        ToolRuleScope.Attempt => true,
        ToolRuleScope.Job => false,
        _ => throw new ArgumentOutOfRangeException(
            nameof(scope), scope, "Tool rule scope has no evaluation window.")
    };
}
