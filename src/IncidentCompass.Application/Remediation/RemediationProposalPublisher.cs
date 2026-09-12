using IncidentCompass.Application.Core.Dispatching;
using IncidentCompass.Application.Core.Exceptions;
using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Governance.ActionApprovals.Propose;
using IncidentCompass.Application.Intake.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IncidentCompass.Application.Remediation;

/// <summary>
/// Turns a recorded remediation diff into one backend-owned <c>code_write</c> proposal a person must
/// approve, or refuses with a closed code and creates nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything it proposes comes from durable state the backend wrote.</b> The diff, its base
/// identity and the service and release it was prepared against are read back out of
/// <c>remediation_diffs</c>; the report, job, attempt and cited source evidence come from the pass
/// context; the tool id, category and logical target come from a compiled descriptor; the approval
/// state comes from the governance defaults, which do not allow a <c>code_write</c> to be approved by
/// policy. Model text reaches exactly one of those fields, the diff itself, and only after the
/// backend parsed it, applied it to a disposable copy of a tree it named first and recorded what it
/// produced. There is no repository URL, branch, remote or credential anywhere on this path, because
/// no such field exists in the contract it writes.
/// </para>
/// <para>
/// <b>Why the proposal step runs before the pass, and what that buys.</b> The workflow calls this
/// first. When a diff already exists for the report, the proposal is built from that diff and no
/// model is called, so an evaluation retried after a failed write costs nothing and cannot produce a
/// second diff. When nothing exists, <see cref="RemediationProposalCodes.DiffMissing" /> is the
/// answer, and the workflow
/// takes that as its cue to run the pass and call again. That ordering is what keeps one report to
/// one diff, and one diff to one proposal: the second pass that would have made state ambiguous
/// never runs.
/// </para>
/// <para>
/// <b>Refusals create nothing at all.</b> Every code below is returned before the proposal command is
/// dispatched, so a missing, ambiguous, foreign, stale or unsupported artifact leaves no action row,
/// no approval and nothing for a person to approve by mistake.
/// </para>
/// </remarks>
internal sealed partial class RemediationProposalPublisher(
    IRemediationDiffRepository diffRepository,
    IRemediationWorkspace workspace,
    IApplicationDispatcher dispatcher,
    ILogger<RemediationProposalPublisher>? logger = null)
{
    /// <summary>
    /// How many diffs are read to decide whether durable state names one change. Two is enough: the
    /// question is "exactly one", not "how many".
    /// </summary>
    private const int CandidateLimit = 2;

    private readonly ILogger logger = logger ?? NullLogger<RemediationProposalPublisher>.Instance;

    public async Task<string> PublishAsync(
        string tenantId,
        Guid originReportId,
        RemediationPassContext context,
        TriageConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var candidates = await diffRepository.FindForReportAsync(
            tenantId, originReportId, CandidateLimit, cancellationToken);
        if (candidates.Count == 0)
        {
            return RemediationProposalCodes.DiffMissing;
        }

        if (candidates.Count > 1)
        {
            return Refuse(context, originReportId, RemediationProposalCodes.DiffAmbiguous);
        }

        var refusal = Eligibility(candidates[0], context, configuration);
        return refusal is not null
            ? Refuse(context, originReportId, refusal)
            : await ProposeAsync(candidates[0], context, tenantId, originReportId, cancellationToken);
    }

    /// <summary>
    /// Whether one recorded diff is something this release may freeze, and nothing else.
    /// </summary>
    /// <remarks>
    /// The tenant and the report are already settled by the read, so what is left is whether the row
    /// is consistent with the origin that claims it and whether it carries the one shape the frozen
    /// payload can honestly describe. The test check is the important one: every diff this release
    /// produces carries <c>not_executed</c> and no test command, and the payload says so in those
    /// words, so a row that recorded a real outcome is refused rather than described as untested.
    /// </remarks>
    private static string? Eligibility(
        RemediationDiff diff,
        RemediationPassContext context,
        TriageConfiguration configuration)
    {
        if (!configuration.CurrentReleases.TryGetValue(context.Fault.ServiceName, out var release) ||
            string.IsNullOrWhiteSpace(release))
        {
            return RemediationCodes.ReleaseUnavailable;
        }

        if (diff.JobId != context.Job.Id || diff.Attempt != context.Job.Attempt ||
            !string.Equals(diff.ServiceName, context.Fault.ServiceName, StringComparison.Ordinal) ||
            !string.Equals(diff.Release, release, StringComparison.Ordinal))
        {
            return RemediationProposalCodes.DiffForeign;
        }

        if (!string.Equals(diff.ValidationCode, RemediationCodes.Applied, StringComparison.Ordinal) ||
            !string.Equals(diff.TestOutcome, RemediationDiff.TestNotExecuted, StringComparison.Ordinal) ||
            diff.TestCommandId is not null)
        {
            return RemediationProposalCodes.DiffUnsupported;
        }

        return context.SourceEvidence.Count == 0 ? RemediationCodes.SourceEvidenceMissing : null;
    }

    private async Task<string> ProposeAsync(
        RemediationDiff diff,
        RemediationPassContext context,
        string tenantId,
        Guid originReportId,
        CancellationToken cancellationToken)
    {
        // The base is named again here rather than trusted from the row. A diff records the tree that
        // existed when it was prepared; this asks what exists now, and a difference means the change
        // is about a tree nobody can apply it to any more. Checking it before the proposal is what
        // keeps a stale artifact from becoming something a person can approve; the same comparison
        // runs again inside the adapter when an approved proposal executes, because the checkout can
        // move between the two.
        var target = new RemediationTarget(diff.ServiceName, diff.Release);
        var baseResult = await workspace.IdentifyBaseAsync(target, cancellationToken);
        if (baseResult.TreeIdentity is null)
        {
            return Refuse(context, originReportId, baseResult.Code);
        }

        if (!string.Equals(baseResult.TreeIdentity, diff.BaseTreeIdentity, StringComparison.Ordinal))
        {
            return Refuse(context, originReportId, RemediationProposalCodes.BaseStale);
        }

        var payload = new RemediationProposalPayload(
            originReportId,
            diff.ServiceName,
            diff.Release,
            diff.BaseTreeIdentity,
            diff.ResultTreeIdentity,
            diff.FilesChanged,
            diff.PatchBytes,
            diff.PatchText,
            RemediationProposalPayloadFactory.ComputeEvidenceSha256(
                context.SourceEvidence.Select(static artifact => artifact.Id)),
            context.SourceEvidence.Select(static artifact => artifact.Id).Distinct().Count(),
            RemediationDiff.TestNotExecuted);
        if (RemediationProposalPayloadFactory.Create(payload).CanonicalPayload.Length >
            ActionApprovalLimits.MaximumPayloadBytes)
        {
            return Refuse(context, originReportId, RemediationProposalCodes.PayloadOversized);
        }

        return await DispatchAsync(payload, context, tenantId, originReportId, cancellationToken);
    }

    private async Task<string> DispatchAsync(
        RemediationProposalPayload payload,
        RemediationPassContext context,
        string tenantId,
        Guid originReportId,
        CancellationToken cancellationToken)
    {
        PostReportActionProposalResponse response;
        try
        {
            response = await dispatcher.DispatchAsync<
                ProposePostReportActionCommand,
                PostReportActionProposalResponse>(
                new ProposePostReportActionCommand(
                    tenantId,
                    originReportId,
                    RemediationApplyToolDescriptor.ToolId,
                    RemediationProposalPayloadFactory.ProposalKey(originReportId),
                    RemediationProposalPayloadFactory.BuildArguments(payload)),
                cancellationToken);
        }
        catch (ConflictException)
        {
            // The key is the report's, so this means an existing proposal for the same report was
            // built over different immutable input: the diff is the same but the host binding behind
            // it is not. Settled, not retried, because the frozen proposal a person may already be
            // reading must not be repointed at a different binding.
            return Refuse(context, originReportId, RemediationProposalCodes.ProposalConflict);
        }

        switch (response.Outcome)
        {
            case PostReportActionProposalOutcome.Requested:
                LogProposalRequested(
                    logger, context.Job.Id, originReportId, payload.PatchBytes,
                    payload.BaseTreeIdentity, response.IsReplay);
                return RemediationProposalCodes.ProposalRequested;
            case PostReportActionProposalOutcome.Approved:
                LogProposalAutoApproved(logger, context.Job.Id, originReportId);
                return RemediationProposalCodes.ProposalAutoApproved;
            default:
                return Refuse(context, originReportId, response.ReasonCode);
        }
    }

    private string Refuse(RemediationPassContext context, Guid originReportId, string code)
    {
        LogProposalRefused(logger, context.Job.Id, originReportId, code);
        return code;
    }

    [LoggerMessage(
        EventId = 3804,
        Level = LogLevel.Information,
        Message = "Remediation diff for triage job {JobId} report {ReportId} was frozen into a code_write proposal of {PatchBytes} bytes against base {BaseTreeIdentity} and is waiting for human approval (replay: {IsReplay}). No test was executed.")]
    private static partial void LogProposalRequested(
        ILogger logger,
        Guid jobId,
        Guid reportId,
        int patchBytes,
        string baseTreeIdentity,
        bool isReplay);

    [LoggerMessage(
        EventId = 3805,
        Level = LogLevel.Warning,
        Message = "Remediation diff for triage job {JobId} report {ReportId} produced no approvable proposal: {Outcome}")]
    private static partial void LogProposalRefused(
        ILogger logger,
        Guid jobId,
        Guid reportId,
        string outcome);

    [LoggerMessage(
        EventId = 3806,
        Level = LogLevel.Error,
        Message = "Remediation diff for triage job {JobId} report {ReportId} was auto-approved as a code_write action, which no shipped policy allows.")]
    private static partial void LogProposalAutoApproved(ILogger logger, Guid jobId, Guid reportId);
}
