using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Governance.PostReportActions;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Domain.Incidents.Actions;
using Microsoft.Extensions.DependencyInjection;

namespace IncidentCompass.Application.Remediation;

/// <summary>
/// What schedules a pull request: one post-report action workflow whose intent is never written when a
/// report is published, only when an approved branch push for that report has actually executed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why <see cref="SelectAsync" /> always declines.</b> The same reason the push declines. A pull
/// request has two preconditions a report does not satisfy - a person approved a change, and a person
/// approved publishing it - and a queue entry written before both is either a row that waits forever or
/// a row retried until approvals arrive, which can take days and would exhaust the attempt cap long
/// first. Declining is the statement that report publication may not schedule a pull request, and it
/// leaves exactly one writer of this intent.
/// </para>
/// <para>
/// <b>That writer is the push's own terminal transaction.</b> The intent row is inserted in the same
/// database transaction that moves the <c>branch_push</c> action to <c>executed</c>, so it cannot exist
/// unless that happened, and it cannot be lost if it did. The ordering is a property of the write
/// rather than of a poller's timing, and the unique key on tenant, report and tool means the transaction
/// can be replayed without producing a second one. That is also what makes at most one pull request per
/// report a structural fact before any provider is asked anything.
/// </para>
/// <para>
/// <b>The switch is checked twice, as everywhere else.</b> A pull request runs for a report only when
/// the job's own snapshotted configuration enables <c>pr_create</c> the way it enables any external
/// action. The shipped configuration declares it disabled with an empty allow-list, so nothing happens
/// until an operator changes both. An operator who wants branches published but no pull requests opened
/// leaves this one off and the push on, which is why it is a separate id.
/// </para>
/// <para>
/// <b>Disposition.</b> Every closed code this workflow returns completes the intent carrying that code.
/// There is nothing here a second attempt would answer differently: an unconfigured host stays
/// unconfigured, a missing push stays missing, and an unconfirmed head is a fresh proposal's problem
/// rather than a retry's. A persistence failure is deliberately not caught, because the evaluation pump
/// already retries that one.
/// </para>
/// </remarks>
public sealed class PullRequestPostReportActionWorkflow(
    ITriageConfigurationRepository configurationRepository,
    IServiceScopeFactory scopeFactory) : IPostReportActionWorkflow
{
    public string ToolId => PullRequestToolDescriptor.ToolId;

    public int WorkflowVersion => 1;

    public ActionCategory Category => ActionCategory.PrCreate;

    public string LogicalTargetId => PullRequestToolDescriptor.LogicalTargetId;

    /// <summary>
    /// Never enqueues. See the type remarks: report publication is not allowed to schedule a pull
    /// request.
    /// </summary>
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
            return PostReportActionWorkflowResult.DeadLetter(PullRequestCodes.IntentInvalid);
        }

        var configuration = await configurationRepository.GetByHashAsync(intent.ConfigHash, cancellationToken);
        if (!IsEnabled(configuration))
        {
            return PostReportActionWorkflowResult.Completed(PullRequestCodes.Disabled);
        }

        using var scope = scopeFactory.CreateScope();
        return PostReportActionWorkflowResult.Completed(
            await scope.ServiceProvider.GetRequiredService<PullRequestProposalPublisher>()
                .PublishAsync(intent.TenantId, intent.OriginReportId, intent.JobId, cancellationToken));
    }

    private bool HasMatchingIdentity(PostReportActionIntent intent) =>
        string.Equals(intent.ToolId, ToolId, StringComparison.Ordinal) &&
        intent.WorkflowVersion == WorkflowVersion &&
        intent.RouteId is null &&
        string.Equals(
            intent.ProposalKey,
            PullRequestPayloadFactory.ProposalKey(intent.OriginReportId),
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
