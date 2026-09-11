using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Domain.Incidents.Actions;

namespace IncidentCompass.Application.Remediation;

/// <summary>
/// The backend descriptor for the governed <c>code_write</c> action a produced remediation diff is
/// frozen into: one approval, over one exact diff, bound to one base tree.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a second descriptor rather than reusing the pass's own.</b>
/// <see cref="RemediationDiffToolDescriptor" /> switches on the work of preparing a diff: it spends
/// model budget, copies a checkout and writes a row. This one switches on what a human may approve
/// afterwards. They are different decisions with different blast radii, an operator has to be able to
/// make either one without making the other - preparing diffs for review while approving none is the
/// normal state - and the approval contract keys a proposal by tool id, so sharing one id would make
/// the pass and the approval the same switch and the same idempotency key.
/// </para>
/// <para>
/// <b>What the category means here.</b> <see cref="ActionCategory.CodeWrite" /> is the category for
/// writing code, and it is deliberately not auto-approvable: <c>ActionGovernanceDefaults</c> names
/// <see cref="ActionCategory.Notification" /> as the only category policy may approve on its own, so
/// a proposal carrying this category is always created in the <c>requested</c> state and waits for a
/// person. Configuration can tighten that and has no way to widen it.
/// </para>
/// <para>
/// <b>What approving one authorizes, and what it does not.</b> Executing an approved proposal
/// re-applies the frozen diff to a fresh disposable copy of the approved base and records the
/// resulting tree identity. It does not push a branch, open a pull request, merge anything or run a
/// test: nothing on this path reaches a remote, and no process is started anywhere in the product.
/// </para>
/// <para>
/// <b>It does schedule the next decision, and only the next decision.</b> Executing this action
/// writes one queue entry for <see cref="BranchPushToolDescriptor" /> in the same transaction that
/// records the execution, and that entry can only ever produce another proposal for a person to
/// approve. Approving a code write therefore approves applying a diff to a copy and nothing more;
/// whether those bytes ever leave this host is a second, separately approved decision, under a
/// separate category an operator can leave switched off. Pull-request publication remains a category
/// this release does not implement.
/// </para>
/// <para>
/// <b>The logical target is the pass's own.</b> Both act on the checkout an operator configured, so
/// naming two targets for one thing would let the two entries drift apart in configuration while
/// still describing the same directory. The concrete root stays a host option that never appears
/// here.
/// </para>
/// </remarks>
public static class RemediationApplyToolDescriptor
{
    public const string ToolId = "remediation_apply";

    public const string LogicalTargetId = RemediationDiffToolDescriptor.LogicalTargetId;

    public static AgentToolDescriptor Descriptor { get; } = new(
        ToolId,
        AgentToolCapability.ExternalAction,
        ActionCategory.CodeWrite,
        LogicalTargetId);
}
