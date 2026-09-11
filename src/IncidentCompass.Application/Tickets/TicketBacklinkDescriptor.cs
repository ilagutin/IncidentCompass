using IncidentCompass.Application.Governance.PostReportActions;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Domain.Incidents.Actions;

namespace IncidentCompass.Application.Tickets;

/// <summary>
/// The backend descriptor for the governed <c>ticket_backlink</c> action: one comment on the ticket a
/// report cited, saying which pull request now answers it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is a second tool id and not a second <c>ticket_update</c>.</b> The queue holds at most one
/// intent per report per tool, and the approval table at most one proposal per report, tool and key. So
/// a report has exactly one <c>ticket_update</c> for its whole life, and that one is proposed when the
/// report is published, long before any pull request exists; its frozen payload cannot learn a number
/// that was not known when a person approved it. Deferring the comment until the chain settles would
/// have been the other way out, and it is worse: the chain can stop at any of three human approvals
/// that may simply never come, and a report with no remediation at all would then never get the comment
/// it gets today. A second id costs one configuration entry and leaves the existing behaviour exactly
/// as it was.
/// </para>
/// <para>
/// <b>It is the same category, on purpose.</b> Adding a comment to an existing ticket is
/// <see cref="ActionCategory.TicketUpdate" /> whatever the comment says, and the audit projection,
/// the approval policy and the terminal validator all reason about the category. Giving this a category
/// of its own would have meant a fourth kind of write for something that is the same write.
/// </para>
/// <para>
/// <b>What it does not change.</b> The comment still lands on an issue, never on a pull request: the
/// preflight refuses any target the provider reports as a pull request, and the evidence shape requires
/// an issue URL in the configured repository. That refusal is what keeps a governed comment from being
/// posted into a conversation this product opened and then read back as evidence, and it stands
/// untouched. The backlink goes on the issue and points at the pull request, which is the direction it
/// was always meant to go.
/// </para>
/// </remarks>
public static class TicketBacklinkDescriptor
{
    public const string ToolId = "ticket_backlink";

    /// <summary>The same configured ticket repository the governed comment already writes to.</summary>
    public const string LogicalTargetId =
        TicketUpdatePostReportActionWorkflow.UpdateLogicalTargetId;

    public static AgentToolDescriptor Descriptor { get; } = new(
        ToolId,
        AgentToolCapability.ExternalAction,
        ActionCategory.TicketUpdate,
        LogicalTargetId);
}
