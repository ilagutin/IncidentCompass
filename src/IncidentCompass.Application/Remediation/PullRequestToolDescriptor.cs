using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Domain.Incidents.Actions;

namespace IncidentCompass.Application.Remediation;

/// <summary>
/// The backend descriptor for the governed <c>pr_create</c> action: one approval, over one head a push
/// already confirmed, one fixed base branch, and one bounded title and body.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a fourth descriptor.</b> Publishing a branch and asking a team to take a change are different
/// decisions with different audiences. A branch is inert: it sits in a namespace an operator can ignore,
/// mirror or delete. A pull request is a public page with a description, a notification to reviewers and
/// a place in a queue people work from. An operator has to be able to allow the push and not this, and
/// the approval contract keys a proposal by tool id, so sharing an id would make the two one switch and
/// one idempotency key.
/// </para>
/// <para>
/// <b>No model can name it.</b> It is an external action, and only <c>IImmediateAgentTool</c>
/// implementations are ever offered to a model; the adapter behind this id implements
/// <c>IExternalActionTool</c> and nothing else. Granting the id to a role in configuration changes
/// nothing, because a role's tool surface is built by intersecting the grant with the immediate tools
/// that exist. The remediation pass, the one place a model is asked about source code, is called with no
/// tools at all.
/// </para>
/// <para>
/// <b>What the category means here.</b> <see cref="ActionCategory.PrCreate" /> is deliberately not
/// auto-approvable: <c>ActionGovernanceDefaults</c> names <see cref="ActionCategory.Notification" /> as
/// the only category policy may approve on its own, and configuration can tighten that and has no way to
/// widen it. A proposal carrying this category is always created in the <c>requested</c> state.
/// </para>
/// <para>
/// <b>It shares the push's logical target.</b> Both act on the one repository an operator configured,
/// and naming a second target for the same thing would let two configuration entries drift apart while
/// describing one repository. The concrete owner, repository, base branch and credential are host
/// options that never appear here and never appear in a payload.
/// </para>
/// </remarks>
public static class PullRequestToolDescriptor
{
    public const string ToolId = "pr_create";

    /// <summary>The configured repository, the same one a governed push lands a branch in.</summary>
    public const string LogicalTargetId = BranchPushToolDescriptor.LogicalTargetId;

    public static AgentToolDescriptor Descriptor { get; } = new(
        ToolId,
        AgentToolCapability.ExternalAction,
        ActionCategory.PrCreate,
        LogicalTargetId);
}
