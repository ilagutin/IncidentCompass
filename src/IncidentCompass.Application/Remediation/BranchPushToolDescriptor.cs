using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Domain.Incidents.Actions;

namespace IncidentCompass.Application.Remediation;

/// <summary>
/// The backend descriptor for the governed <c>branch_push</c> action: one approval, over one exact
/// diff, bound to one repository, one base commit and one branch that does not exist yet.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a third descriptor.</b> <see cref="RemediationDiffToolDescriptor" /> switches on preparing a
/// diff and <see cref="RemediationApplyToolDescriptor" /> on what a human may approve applying to a
/// disposable copy. This one switches on the first thing in the feature that a stranger can see: bytes
/// arriving in a repository. An operator has to be able to allow the first two and not this one -
/// reviewing proposed fixes while publishing none is a reasonable place to stop - and the approval
/// contract keys a proposal by tool id, so sharing an id would make two decisions one switch and one
/// idempotency key.
/// </para>
/// <para>
/// <b>No model can name it.</b> It is an external action, and only <c>IImmediateAgentTool</c>
/// implementations are ever offered to a model; the adapter behind this id implements
/// <c>IExternalActionTool</c> and nothing else. Granting the id to a role in configuration changes
/// nothing, because the tool surface a role produces is built by intersecting the grant with the
/// immediate tools that exist, and this is not one of them. The remediation pass, the one place a
/// model is asked about source code, is called with no tools at all.
/// </para>
/// <para>
/// <b>What the category means here.</b> <see cref="ActionCategory.BranchPush" /> is deliberately not
/// auto-approvable: <c>ActionGovernanceDefaults</c> names <see cref="ActionCategory.Notification" />
/// as the only category policy may approve on its own, and configuration can tighten that and has no
/// way to widen it. A proposal carrying this category is always created in the <c>requested</c> state.
/// </para>
/// <para>
/// <b>Its own logical target.</b> A branch push acts on a configured repository, not on the monitored
/// checkout the other two act on. Naming the workspace target here would let one configuration entry
/// describe two different things - a directory on this host and a repository on a provider - and an
/// operator reading it could not tell which they were allowing.
/// </para>
/// </remarks>
public static class BranchPushToolDescriptor
{
    public const string ToolId = "branch_push";

    /// <summary>
    /// What a push acts on, named the way the other logical targets are: the thing an operator
    /// configured. The concrete owner, repository, base branch and credential are host options that
    /// never appear here and never appear in a payload.
    /// </summary>
    public const string LogicalTargetId = "code:configured-repository";

    public static AgentToolDescriptor Descriptor { get; } = new(
        ToolId,
        AgentToolCapability.ExternalAction,
        ActionCategory.BranchPush,
        LogicalTargetId);
}
