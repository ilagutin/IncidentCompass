using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Domain.Incidents.Actions;

namespace IncidentCompass.Application.Remediation;

/// <summary>
/// The backend descriptor the remediation diff pass is switched on and off by.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a tool descriptor and not a new flag.</b> The pass writes outside the triage database - it
/// copies a monitored checkout onto disk and writes files into that copy - and it spends model
/// budget. That is what <see cref="AgentToolCapability.ExternalAction"/> already names, and
/// <see cref="ActionCategory.CodeWrite"/> is already the category for writing code. Expressing the
/// switch as a tool entry means an operator turns the pass on exactly the way they turn ticket
/// creation or notification on: the tool has to appear in <c>Tools</c> with a matching category and
/// logical target, it has to be listed in <c>Actions.AllowedTools</c>, and neither
/// <c>Actions.DefaultMode</c> nor the tool's own <c>Mode</c> may be <c>disabled</c>. All of that is
/// already validated at configuration load, already snapshotted per job, and already documented. A
/// second style of switch would be a second thing to get wrong.
/// </para>
/// <para>
/// <b>There is deliberately no <c>IExternalActionTool</c> behind it.</b> A descriptor with no
/// implementation is an established shape here - <c>telegram_notify</c> is one - and it is the right
/// one: the pass is not dispatched from an approval, it is run by its own post-report workflow, and
/// it proposes nothing. Nothing registers it as an agent tool either, so no model can call it and no
/// role can be granted it; it names a backend capability, not a turn a model may take.
/// </para>
/// </remarks>
public static class RemediationDiffToolDescriptor
{
    public const string ToolId = "remediation_diff";

    /// <summary>
    /// What the pass acts on, named the way the other logical targets are: the thing an operator
    /// configured, not a host path. The concrete checkout and workspace root are host options that
    /// never appear here.
    /// </summary>
    public const string LogicalTargetId = "source:configured-workspace";

    public static AgentToolDescriptor Descriptor { get; } = new(
        ToolId,
        AgentToolCapability.ExternalAction,
        ActionCategory.CodeWrite,
        LogicalTargetId);
}
