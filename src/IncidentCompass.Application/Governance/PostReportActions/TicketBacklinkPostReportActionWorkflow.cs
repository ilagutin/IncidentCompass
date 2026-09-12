using System.Text.Json;
using IncidentCompass.Application.Core.Dispatching;
using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Governance.ActionApprovals.Propose;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Remediation;
using IncidentCompass.Application.Tickets;
using IncidentCompass.Domain.Incidents.Actions;
using Microsoft.Extensions.DependencyInjection;

namespace IncidentCompass.Application.Governance.PostReportActions;

/// <summary>
/// What schedules the one comment saying which pull request now answers a cited ticket.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why <see cref="SelectAsync" /> always declines.</b> A backlink needs a pull request, and there is
/// none when a report is published. The intent is written by the transaction that records the
/// <c>pr_create</c> action as executed, so it exists if and only if a pull request was opened, and the
/// unique key on tenant, report and tool makes a replayed transaction a no-op rather than a second
/// comment.
/// </para>
/// <para>
/// <b>It leaves the governed ticket comment exactly as it was.</b> The <c>ticket_update</c> workflow
/// still enqueues at publication and still posts the comment it posts today, with or without any
/// remediation. This is an addition on top of it, not a replacement for it, which is the whole reason it
/// has its own tool id: a report whose chain stops at any of three human approvals still gets its
/// ordinary comment, and only a report that actually produced a pull request gets a second one.
/// </para>
/// <para>
/// <b>The target is an issue, and the preflight still refuses a pull request.</b> The ticket is resolved
/// by the same reader the governed comment uses, which requires exactly one cited existing ticket whose
/// URL is an issue in the configured repository; the adapter's preflight independently refuses any
/// target the provider reports as a pull request. The backlink goes on the issue and points at the pull
/// request, and neither of those checks is relaxed to let it.
/// </para>
/// </remarks>
public sealed class TicketBacklinkPostReportActionWorkflow(
    ITriageConfigurationRepository configurationRepository,
    IServiceScopeFactory scopeFactory) : IPostReportActionWorkflow
{
    /// <summary>No cited ticket, so there is nothing to link back to.</summary>
    public const string TargetRequiredCode = "ticket_backlink_target_required";

    /// <summary>
    /// No executed pull-request action recorded a number for this report, so there is nothing to link
    /// to. Settled rather than retried: the intent is written by that action's own transaction, so an
    /// evaluation that finds nothing is looking at a report whose pull request was superseded or rolled
    /// back, not at one that has not finished.
    /// </summary>
    public const string PullRequestUnconfirmedCode = "ticket_backlink_pull_request_unconfirmed";

    private const string IntentInvalidCode = "ticket_backlink_intent_invalid";
    private const string DisabledCode = "ticket_backlink_disabled";

    public string ToolId => TicketBacklinkDescriptor.ToolId;

    public int WorkflowVersion => 1;

    public ActionCategory Category => ActionCategory.TicketUpdate;

    public string LogicalTargetId => TicketBacklinkDescriptor.LogicalTargetId;

    /// <summary>Never enqueues. See the type remarks.</summary>
    public Task<(bool ShouldEnqueue, string? RouteId)> SelectAsync(
        string tenantId,
        Guid originReportId,
        Guid faultId,
        Guid jobId,
        int attempt,
        string configHash,
        string serviceName,
        string environment,
        string? severity,
        CancellationToken cancellationToken) =>
        Task.FromResult<(bool, string?)>((false, null));

    public async Task<PostReportActionWorkflowResult> EvaluateAsync(
        PostReportActionIntent intent,
        CancellationToken cancellationToken)
    {
        if (!HasMatchingIdentity(intent))
        {
            return PostReportActionWorkflowResult.DeadLetter(IntentInvalidCode);
        }

        var configuration = await configurationRepository.GetByHashAsync(intent.ConfigHash, cancellationToken);
        if (!IsEnabled(configuration))
        {
            return PostReportActionWorkflowResult.Completed(DisabledCode);
        }

        using var scope = scopeFactory.CreateScope();
        var evidence = await scope.ServiceProvider.GetRequiredService<ITicketUpdateEvidenceResolver>()
            .ResolveAsync(intent.TenantId, intent.OriginReportId, cancellationToken);
        if (evidence is null)
        {
            return PostReportActionWorkflowResult.Completed(TargetRequiredCode);
        }

        var pullRequest = await scope.ServiceProvider.GetRequiredService<IConfirmedPullRequestReader>()
            .FindConfirmedPullRequestNumberAsync(
                intent.TenantId, intent.OriginReportId, cancellationToken);
        if (pullRequest is null)
        {
            return PostReportActionWorkflowResult.Completed(PullRequestUnconfirmedCode);
        }

        var response = await scope.ServiceProvider.GetRequiredService<IApplicationDispatcher>()
            .DispatchAsync<ProposePostReportActionCommand, PostReportActionProposalResponse>(
                new ProposePostReportActionCommand(
                    intent.TenantId,
                    intent.OriginReportId,
                    ToolId,
                    intent.ProposalKey,
                    JsonSerializer.SerializeToElement(new
                    {
                        originReportId = intent.OriginReportId.ToString("N"),
                        proposalKey = intent.ProposalKey,
                        pullRequestNumber = pullRequest,
                        ticketId = evidence.TicketId
                    })),
                cancellationToken);
        return PostReportActionWorkflowResult.Completed(response.Outcome switch
        {
            PostReportActionProposalOutcome.Requested => "requested",
            PostReportActionProposalOutcome.Approved => "approved",
            _ => response.ReasonCode
        });
    }

    private bool HasMatchingIdentity(PostReportActionIntent intent) =>
        string.Equals(intent.ToolId, ToolId, StringComparison.Ordinal) &&
        intent.WorkflowVersion == WorkflowVersion && intent.RouteId is null &&
        string.Equals(
            intent.ProposalKey,
            $"post-report:v1:{intent.OriginReportId:N}:{ToolId}",
            StringComparison.Ordinal) &&
        intent.HasValidCanonicalInput();

    private bool IsEnabled(TriageConfiguration configuration) =>
        configuration.Actions.AllowedTools.Contains(ToolId, StringComparer.Ordinal) &&
        configuration.Tools.TryGetValue(ToolId, out var tool) &&
        string.Equals(tool.Kind, "external_action", StringComparison.Ordinal) &&
        string.Equals(tool.Category, Category.ToStorageValue(), StringComparison.Ordinal) &&
        string.Equals(tool.LogicalTargetId, LogicalTargetId, StringComparison.Ordinal) &&
        !string.Equals(configuration.Actions.DefaultMode, "disabled", StringComparison.Ordinal) &&
        !string.Equals(tool.Mode, "disabled", StringComparison.Ordinal);
}
