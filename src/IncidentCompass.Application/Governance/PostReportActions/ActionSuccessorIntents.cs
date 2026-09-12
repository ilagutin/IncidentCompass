using IncidentCompass.Application.Remediation;
using IncidentCompass.Application.Tickets;
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
/// <para>
/// <b>The chain is a list, not a graph, and each step is still a separate human decision.</b> An
/// applied code write schedules a branch push, an executed push schedules a pull request, and an opened
/// pull request schedules the ticket backlink that points at it. Each entry only ever produces a
/// proposal, under its own tool id and its own configuration switch, so an operator who wants branches
/// but no pull requests, or pull requests but no ticket comments, switches off the one they do not want
/// and the chain stops there. Nothing here approves anything.
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
        ActionApprovalState terminalState)
    {
        if (terminalState != ActionApprovalState.Executed || mode != ActionExecutionMode.Live)
        {
            return null;
        }

        return (toolId, category) switch
        {
            (RemediationApplyToolDescriptor.ToolId, ActionCategory.CodeWrite) =>
                BranchPushToolDescriptor.ToolId,
            (BranchPushToolDescriptor.ToolId, ActionCategory.BranchPush) =>
                PullRequestToolDescriptor.ToolId,
            (PullRequestToolDescriptor.ToolId, ActionCategory.PrCreate) =>
                TicketBacklinkDescriptor.ToolId,
            _ => null
        };
    }
}
