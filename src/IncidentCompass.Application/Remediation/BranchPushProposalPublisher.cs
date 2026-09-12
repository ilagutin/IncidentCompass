using IncidentCompass.Application.Core.Dispatching;
using IncidentCompass.Application.Core.Exceptions;
using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Governance.ActionApprovals.Propose;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IncidentCompass.Application.Remediation;

/// <summary>
/// Turns one executed, approved code write into one backend-owned <c>branch_push</c> proposal a person
/// must approve, or refuses with a closed code and creates nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything it proposes comes from durable state or from a read the backend made.</b> The diff,
/// the base tree identity and the result tree identity are read back out of the bytes a person already
/// approved for the code write; the repository, the base branch and the credential are host options
/// behind a port that has no way to be told otherwise; the branch name is derived from the origin
/// report; the commit instant is the predecessor's own completion time. The only model text on the
/// path is the diff, and it reached the payload by being applied to a disposable copy of a tree the
/// backend named first.
/// </para>
/// <para>
/// <b>The correspondence is proved here, not assumed.</b> Before anything is proposed, the remote base
/// branch is read, its whole regular-file listing is compared with the approved base tree path by
/// path, and the result is frozen into the payload as a digest. A proposal therefore states which
/// remote commit it is a change to, and a dispatch re-proves the same thing against the same frozen
/// values rather than trusting that nothing moved.
/// </para>
/// <para>
/// <b>Refusals create nothing at all.</b> Every code below is returned before the proposal command is
/// dispatched, so an unconfigured host, a missing predecessor, a diverged base or a truncated listing
/// leaves no action row and nothing for a person to approve by mistake.
/// </para>
/// </remarks>
internal sealed partial class BranchPushProposalPublisher(
    IRemediationPredecessorReader predecessorReader,
    ICodePublicationGateway gateway,
    IRemediationWorkspace workspace,
    IApplicationDispatcher dispatcher,
    ILogger<BranchPushProposalPublisher>? logger = null)
{
    private readonly ILogger logger = logger ?? NullLogger<BranchPushProposalPublisher>.Instance;

    public async Task<string> PublishAsync(
        string tenantId,
        Guid originReportId,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        if (!gateway.IsConfigured || gateway.ConfiguredRepository is not { } repository)
        {
            return Refuse(jobId, originReportId, CodePublicationCodes.BindingUnavailable);
        }

        var predecessor = await predecessorReader.FindExecutedCodeWriteAsync(
            tenantId, originReportId, cancellationToken);
        if (predecessor is null)
        {
            return Refuse(jobId, originReportId, BranchPushCodes.PredecessorMissing);
        }

        var approved = RemediationProposalPayloadFactory.TryReadPayload(predecessor.CanonicalPayload);
        if (approved is null || approved.OriginReportId != originReportId)
        {
            return Refuse(jobId, originReportId, BranchPushCodes.PredecessorPayloadInvalid);
        }

        var remote = await gateway.ReadBaseAsync(null, cancellationToken);
        if (!remote.IsRead)
        {
            return Refuse(jobId, originReportId, remote.Code);
        }

        var prepared = await workspace.PrepareForPublicationAsync(
            new RemediationPublicationRequest(
                new RemediationTarget(approved.ServiceName, approved.Release),
                approved.BaseTreeIdentity,
                approved.ResultTreeIdentity,
                approved.PatchText,
                remote.CommitSha!,
                remote.TreeSha!,
                remote.BlobsByPath),
            cancellationToken);
        return prepared.CorrespondenceDigest is null
            ? Refuse(jobId, originReportId, prepared.Code)
            : await ProposeAsync(
                Build(approved, predecessor, repository, remote, prepared),
                tenantId, jobId, cancellationToken);
    }

    private BranchPushPayload Build(
        RemediationProposalPayload approved,
        RemediationPredecessor predecessor,
        string repository,
        CodePublicationBaseResult remote,
        RemediationPublicationResult prepared) =>
        new(
            approved.OriginReportId,
            approved.ServiceName,
            approved.Release,
            repository,
            gateway.BaseBranch,
            RemediationBranchName.For(approved.OriginReportId),
            remote.CommitSha!,
            remote.TreeSha!,
            approved.BaseTreeIdentity,
            approved.ResultTreeIdentity,
            approved.FilesChanged,
            approved.PatchBytes,
            approved.PatchText,
            prepared.CorrespondenceDigest!,
            prepared.ProvedPathCount,
            prepared.LocalOnlyPaths.Count,
            predecessor.ActionId,
            predecessor.ResultSha256,
            predecessor.ExecutedAtUtc);

    private async Task<string> ProposeAsync(
        BranchPushPayload payload,
        string tenantId,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        if (BranchPushPayloadFactory.Create(payload).CanonicalPayload.Length >
            ActionApprovalLimits.MaximumPayloadBytes)
        {
            return Refuse(jobId, payload.OriginReportId, BranchPushCodes.PayloadOversized);
        }

        PostReportActionProposalResponse response;
        try
        {
            response = await dispatcher.DispatchAsync<
                ProposePostReportActionCommand,
                PostReportActionProposalResponse>(
                new ProposePostReportActionCommand(
                    tenantId,
                    payload.OriginReportId,
                    BranchPushToolDescriptor.ToolId,
                    BranchPushPayloadFactory.ProposalKey(payload.OriginReportId),
                    BranchPushPayloadFactory.BuildArguments(payload)),
                cancellationToken);
        }
        catch (ConflictException)
        {
            // Same report, same key, different immutable input: the host binding or the proved base
            // moved between two evaluations. Settled rather than retried, because the frozen proposal
            // a person may already be reading must not be repointed at another commit.
            return Refuse(jobId, payload.OriginReportId, BranchPushCodes.ProposalConflict);
        }

        switch (response.Outcome)
        {
            case PostReportActionProposalOutcome.Requested:
                LogProposalRequested(
                    logger, jobId, payload.OriginReportId, payload.BranchName,
                    payload.ProvedPathCount, payload.ExcludedPathCount, response.IsReplay);
                return BranchPushCodes.ProposalRequested;
            case PostReportActionProposalOutcome.Approved:
                LogProposalAutoApproved(logger, jobId, payload.OriginReportId);
                return BranchPushCodes.ProposalAutoApproved;
            default:
                return Refuse(jobId, payload.OriginReportId, response.ReasonCode);
        }
    }

    private string Refuse(Guid jobId, Guid originReportId, string code)
    {
        LogProposalRefused(logger, jobId, originReportId, code);
        return code;
    }

    [LoggerMessage(
        EventId = 3808,
        Level = LogLevel.Information,
        Message = "Branch push for triage job {JobId} report {ReportId} was frozen into a proposal for branch {BranchName} over {ProvedPathCount} proved path(s) with {ExcludedPathCount} local-only path(s) excluded, and is waiting for human approval (replay: {IsReplay}). No test was executed.")]
    private static partial void LogProposalRequested(
        ILogger logger,
        Guid jobId,
        Guid reportId,
        string branchName,
        int provedPathCount,
        int excludedPathCount,
        bool isReplay);

    [LoggerMessage(
        EventId = 3809,
        Level = LogLevel.Warning,
        Message = "Branch push for triage job {JobId} report {ReportId} produced no approvable proposal: {Outcome}")]
    private static partial void LogProposalRefused(
        ILogger logger,
        Guid jobId,
        Guid reportId,
        string outcome);

    [LoggerMessage(
        EventId = 3810,
        Level = LogLevel.Error,
        Message = "Branch push for triage job {JobId} report {ReportId} was auto-approved, which no shipped policy allows.")]
    private static partial void LogProposalAutoApproved(ILogger logger, Guid jobId, Guid reportId);
}
