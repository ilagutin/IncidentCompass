namespace IncidentCompass.Application.Governance.Tools;

/// <summary>
/// The window a tool rule is evaluated over. It is the only representation of a rule's scope that
/// crosses a port, so no adapter ever sees the configured string and no adapter decides what an
/// unrecognized one means.
/// </summary>
/// <remarks>
/// There are exactly two members because there are exactly two windows every fact reader can
/// express. Both count within one triage job; the attempt window narrows that to the current
/// attempt. A third window would need a fact reader that can widen past the job it was built for,
/// which the post-report action reader cannot: its origin is one job. Adding a member without
/// giving it an answer in <see cref="ToolRuleScopes" /> is therefore a compile-time-visible,
/// run-time-loud mistake rather than a silent third behaviour.
/// </remarks>
public enum ToolRuleScope
{
    /// <summary>The current attempt of the current job.</summary>
    Attempt,

    /// <summary>Every attempt of the current job.</summary>
    Job
}
