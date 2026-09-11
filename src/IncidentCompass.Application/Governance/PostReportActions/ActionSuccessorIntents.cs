using IncidentCompass.Application.Remediation;
using IncidentCompass.Domain.Incidents.Actions;

namespace IncidentCompass.Application.Governance.PostReportActions;

/// <summary>
/// The one place that says which governed action, having actually executed, schedules another.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a chain needs a writer rather than a reader.</b> A proposal's origin is a report and cannot
/// be an action: the schema says so, the grounder says so, and the artifact kind that would express it
/// throws. That is the right boundary - what a change is justified by is evidence a person can read,
/// not a machine's own earlier output - but it leaves nothing that expresses "after". Adding a reader
/// that polls for settled actions would make the order a property of a poller's timing. Writing the
/// successor's queue entry inside the predecessor's own terminal transaction makes it a property of
/// the write: the entry cannot exist unless the predecessor executed, and it cannot be lost if it did.
/// </para>
/// <para>
/// <b>What it is not.</b> It is not provenance. The successor's proposal is still grounded in the same
/// report and the same cited artifacts as the predecessor; the predecessor's identity and result
/// digest travel in the successor's canonical payload instead, where the approval hash covers them and
/// a dispatch re-checks them. So the chain is expressed where the contract allows it to be, and the
/// part of the contract that refuses to let a machine's output justify an action is untouched.
/// </para>
/// <para>
/// <b>A dry run schedules nothing.</b> A simulated action writes a terminal row without reaching an
/// adapter, so nothing was applied and there is nothing to publish.
/// </para>
/// </remarks>
public static class ActionSuccessorIntents
{
    /// <summary>
    /// The tool a completed action schedules next, or <see langword="null" /> when it schedules
    /// nothing. Every argument is read off the action row that is being completed, so a caller cannot
    /// pass a combination the database does not already hold.
    /// </summary>
    public static string? SuccessorToolId(
        string toolId,
        ActionCategory category,
        ActionExecutionMode mode,
        ActionApprovalState terminalState) =>
        terminalState == ActionApprovalState.Executed &&
        mode == ActionExecutionMode.Live &&
        category == ActionCategory.CodeWrite &&
        string.Equals(toolId, RemediationApplyToolDescriptor.ToolId, StringComparison.Ordinal)
            ? BranchPushToolDescriptor.ToolId
            : null;
}
