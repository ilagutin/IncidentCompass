using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Governance.PostReportActions;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Domain.Incidents.Actions;
using Microsoft.Extensions.DependencyInjection;

namespace IncidentCompass.Application.Remediation;

/// <summary>
/// What schedules a remediation diff pass: one post-report action workflow, enqueued when a report
/// is published and evaluated by the Worker's post-report evaluation loop.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why here and not a queue of its own.</b> The pass needs a fenced claim so two Workers cannot
/// run it twice, an attempt cap so a failing pass stops, dead-lettering so a permanently refused one
/// is visible, a closed outcome vocabulary, and a lease that survives a call measured in minutes.
/// The post-report evaluation loop has all five, and its lease renewer already lets a multi-minute
/// evaluation run without touching the lease default. Building a second queue would mean writing
/// claim fencing, retry and dead-lettering a second time, which is the same argument the model call
/// itself follows in going through <c>InvestigationModelCaller</c> rather than beside it. Its
/// trigger is also exactly right: the input to a remediation pass is a published report, and a
/// published report is precisely what writes an intent.
/// </para>
/// <para>
/// <b>Why not operator-triggered.</b> An operator-triggered pass would still need every one of those
/// five properties, because it is still a minutes-long model call that must not run on a request
/// thread and must not run twice. It would therefore end up enqueuing onto a queue like this one
/// anyway, with an endpoint in front of it, and the endpoint would be the only new thing. The cost
/// argument that makes manual triggering attractive - not spending budget on reports nobody wants a
/// fix for - is answered by the switch below instead, which is off in the shipped configuration and
/// is the same switch that governs every other external action.
/// </para>
/// <para>
/// <b>The switch.</b> A pass runs for a report only when the job's own snapshotted configuration
/// enables <c>remediation_diff</c> the way it enables any external action: the tool declared with a
/// matching category and logical target, listed in <c>Actions.AllowedTools</c>, and neither the
/// global mode nor the tool's own mode set to <c>disabled</c>. The shipped configuration declares it
/// with <c>"Mode": "disabled"</c> and an empty <c>AllowedTools</c>, so nothing runs until an
/// operator changes both. Because the configuration is snapshotted per job, turning the switch on
/// affects reports published under the new snapshot, not reports already published under the old
/// one. It is checked twice on purpose: once when the intent is written, and again when it is
/// evaluated, so a switch turned off between the two stops the pass before it spends anything.
/// </para>
/// <para>
/// <b>Disposition.</b> A refusal is a settled outcome, not a retry: nothing about running the same
/// pass again makes an unconfigured host configured or gives an uncited report source evidence, so
/// every closed code the pass returns completes the intent carrying that code. A budget exhaustion
/// and a model-call failure dead-letter, because both mean the attempt has spent what it had and a
/// second pass would only spend more of it. A persistence failure is deliberately not caught here:
/// the evaluation pump already retries that one, which is right, because the work succeeded and only
/// the write did not. Anything else - an adapter that raised a raw provider exception instead of the
/// normalized one its contract requires - is deliberately left to escape: the pump's task set logs
/// it and keeps polling, the lease expires, and the attempt cap ends the intent. Turning an adapter
/// contract violation into a tidy dead-letter code would hide the defect that caused it.
/// </para>
/// </remarks>
public sealed class RemediationPostReportActionWorkflow(
    ITriageConfigurationRepository configurationRepository,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider) : IPostReportActionWorkflow
{
    /// <summary>The intent was malformed, so no pass was run and none can be.</summary>
    internal const string IntentInvalidCode = "remediation_intent_invalid";

    /// <summary>The switch was off when the intent was evaluated. Nothing was spent.</summary>
    internal const string DisabledCode = "remediation_disabled";

    /// <summary>The report the intent names is no longer readable for this tenant.</summary>
    internal const string ReportUnavailableCode = "remediation_report_unavailable";

    /// <summary>The attempt's budget could not admit the pass, or ran out during it.</summary>
    internal const string BudgetExhaustedCode = "remediation_budget_exhausted";

    /// <summary>The model call failed at the provider and its cost was accounted for.</summary>
    internal const string ModelCallFailedCode = "remediation_model_call_failed";

    /// <summary>
    /// The model call failed and the tokens it spent could not be written to the ledger. A distinct
    /// code because it is a different claim: the roll-up is now missing spend that happened.
    /// </summary>
    internal const string AccountingPendingCode = "remediation_model_call_accounting_pending";

    public string ToolId => RemediationDiffToolDescriptor.ToolId;

    public int WorkflowVersion => 1;

    public ActionCategory Category => ActionCategory.CodeWrite;

    public string LogicalTargetId => RemediationDiffToolDescriptor.LogicalTargetId;

    public async Task<(bool ShouldEnqueue, string? RouteId)> SelectAsync(
        string tenantId,
        Guid originReportId,
        Guid faultId,
        Guid jobId,
        int attempt,
        string configHash,
        string serviceName,
        string environment,
        string? severity,
        CancellationToken cancellationToken)
    {
        var configuration = await configurationRepository.GetByHashAsync(configHash, cancellationToken);

        // Null route, always. The intent writer only accepts a route on a notification workflow, and
        // a remediation pass has no route to choose anyway: it runs on the route the job's own
        // snapshot already named for the orchestrator, resolved when the pass runs.
        return IsEnabled(configuration) ? (true, null) : (false, null);
    }

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
        var context = await scope.ServiceProvider
            .GetRequiredService<IRemediationPassContextRepository>()
            .FindAsync(intent.TenantId, intent.OriginReportId, cancellationToken);
        if (context is null)
        {
            return PostReportActionWorkflowResult.DeadLetter(ReportUnavailableCode);
        }

        return await RunAsync(scope.ServiceProvider, intent, configuration, context, cancellationToken);
    }

    /// <summary>
    /// Proposes from what durable state already holds, and only prepares a diff when it holds
    /// nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order is the point. Asking for the proposal first makes an evaluation that is retried
    /// after the pass already recorded a diff cost nothing: it proposes from that diff instead of
    /// spending a second model call, and more importantly it cannot write a second diff for the same
    /// report. One report keeps one diff, one diff keeps one proposal, and the ambiguity that two
    /// rows would create never arises from this path.
    /// </para>
    /// <para>
    /// <c>DiffMissing</c> is therefore not only a refusal code here; it is the answer that means the
    /// pass has not run yet. Every other code the publisher returns is settled, so the pass runs for
    /// exactly one of the states it can be in.
    /// </para>
    /// </remarks>
    private async Task<PostReportActionWorkflowResult> RunAsync(
        IServiceProvider services,
        PostReportActionIntent intent,
        TriageConfiguration configuration,
        RemediationPassContext context,
        CancellationToken cancellationToken)
    {
        var publisher = services.GetRequiredService<RemediationProposalPublisher>();
        var existing = await publisher.PublishAsync(
            intent.TenantId, intent.OriginReportId, context, configuration, cancellationToken);
        if (!string.Equals(existing, RemediationProposalCodes.DiffMissing, StringComparison.Ordinal))
        {
            return PostReportActionWorkflowResult.Completed(existing);
        }

        var request = new RemediationRequest(
            context.Job,
            context.Fault,
            configuration,
            configuration.Orchestrator.RouteId,
            RemediationInstructions.Text,
            intent.OriginReportId,
            context.Report,
            context.SourceEvidence,

            // The wall clock the pass is bounded by starts now. It is a separate bounded operation
            // from the attempt that wrote the report, which may have finished hours ago; the token
            // half of the budget stays that attempt's own, so one incident's model spend stays one
            // number.
            timeProvider.GetUtcNow());

        try
        {
            var result = await services.GetRequiredService<RemediationDiffRunner>()
                .RunAsync(request, cancellationToken);
            return PostReportActionWorkflowResult.Completed(result.IsProduced
                ? await publisher.PublishAsync(
                    intent.TenantId, intent.OriginReportId, context, configuration, cancellationToken)
                : result.Code);
        }
        catch (TriageBudgetExhaustedException)
        {
            return PostReportActionWorkflowResult.DeadLetter(BudgetExhaustedCode);
        }
        catch (InvestigationModelCallFailureException failure)
        {
            return await AccountForFailedCallAsync(services, context, failure);
        }
    }

    /// <summary>
    /// Pays for a model call that failed at the provider.
    /// </summary>
    /// <remarks>
    /// The investigation path writes this accounting from its attempt-failure handler, under the job
    /// lock. A remediation pass has no attempt-failure handler, so without this the tokens a failed
    /// call spent would be owed and never written, and the cost roll-up would under-report exactly
    /// the calls that went wrong. Cancellation is deliberately not propagated into the append: the
    /// spend already happened, and a shutdown is not a reason to lose the record of it.
    /// </remarks>
    private static async Task<PostReportActionWorkflowResult> AccountForFailedCallAsync(
        IServiceProvider services,
        RemediationPassContext context,
        InvestigationModelCallFailureException failure)
    {
        try
        {
            await services.GetRequiredService<TriageLedgerAppender>()
                .AppendModelCallAccountingAsync(context.Job, failure.Accounting, CancellationToken.None);
        }
        catch (InvestigationModelCallFailureException)
        {
            return PostReportActionWorkflowResult.DeadLetter(AccountingPendingCode);
        }

        return PostReportActionWorkflowResult.DeadLetter(ModelCallFailedCode);
    }

    private bool HasMatchingIdentity(PostReportActionIntent intent) =>
        string.Equals(intent.ToolId, ToolId, StringComparison.Ordinal) &&
        intent.WorkflowVersion == WorkflowVersion &&
        intent.RouteId is null &&
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
