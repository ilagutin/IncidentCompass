using IncidentCompass.Application.Core.Dispatching;
using IncidentCompass.Application.Core.Exceptions;
using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Governance.ActionApprovals.Propose;
using IncidentCompass.Application.Tickets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IncidentCompass.Application.Remediation;

/// <summary>
/// Turns one executed, approved branch push into one backend-owned <c>pr_create</c> proposal a person
/// must approve, or refuses with a closed code and creates nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything it proposes comes from durable state.</b> The head branch and the change it describes
/// are read back out of the bytes a person already approved for the push; the head commit is the one
/// that push recorded in its own audit projection, so the pull request is bound to what actually landed
/// rather than to what was intended; the repository and the base branch are host options behind a port
/// that has no way to be told otherwise; the confidence is the report's own; and the issue is the one
/// existing ticket the report cited, resolved by the same reader the governed ticket comment uses.
/// </para>
/// <para>
/// <b>No text on this path was written by a model.</b> The title and the body are composed from
/// identifiers, counts and digests, and the patch never reaches either. The publisher does not compose
/// them itself: it hands the frozen fields to the payload factory, which composes and bounds them, so
/// the dispatch can re-derive exactly the same bytes.
/// </para>
/// <para>
/// <b>Refusals create nothing at all.</b> Every code below is returned before the proposal command is
/// dispatched, so an unconfigured host, a missing push, an unconfirmed head or an unreadable report
/// leaves no action row and nothing for a person to approve by mistake.
/// </para>
/// </remarks>
internal sealed partial class PullRequestProposalPublisher(
    IRemediationPredecessorReader predecessorReader,
    ITicketUpdateEvidenceResolver ticketResolver,
    ICodePublicationGateway gateway,
    IApplicationDispatcher dispatcher,
    ILogger<PullRequestProposalPublisher>? logger = null)
{
    private readonly ILogger logger = logger ?? NullLogger<PullRequestProposalPublisher>.Instance;

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

        var predecessor = await predecessorReader.FindExecutedBranchPushAsync(
            tenantId, originReportId, cancellationToken);
        if (predecessor is null)
        {
            return Refuse(jobId, originReportId, PullRequestCodes.PredecessorMissing);
        }

        var approved = BranchPushPayloadFactory.TryReadPayload(predecessor.CanonicalPayload);
        if (approved is null || approved.OriginReportId != originReportId ||
            !string.Equals(approved.Repository, repository, StringComparison.Ordinal) ||
            !string.Equals(approved.BaseBranch, gateway.BaseBranch, StringComparison.Ordinal))
        {
            return Refuse(jobId, originReportId, PullRequestCodes.PredecessorPayloadInvalid);
        }

        if (!IsConfirmedCommit(predecessor.ExternalResourceId))
        {
            return Refuse(jobId, originReportId, PullRequestCodes.PredecessorHeadUnconfirmed);
        }

        if (!PullRequestNarrative.IsKnownConfidence(predecessor.ReportConfidence))
        {
            return Refuse(jobId, originReportId, PullRequestCodes.OriginReportUnreadable);
        }

        var ticket = await ticketResolver.ResolveAsync(tenantId, originReportId, cancellationToken);
        return await ProposeAsync(
            Build(approved, predecessor, repository, ticket), tenantId, jobId, cancellationToken);
    }

    /// <summary>
    /// Whether the push recorded a commit rather than nothing. The projection column is constrained to
    /// a git object name by the database, so this only has to refuse an absent value; it is checked
    /// anyway, because a payload field that is sometimes a commit and sometimes empty is the kind of
    /// field a later reader stops checking.
    /// </summary>
    private static bool IsConfirmedCommit(string? value) =>
        ExternalActionAuditProjection.IsValidResourceIdentity(
            ExternalActionAuditProjection.GitBranchKind, value);

    private PullRequestPayload Build(
        BranchPushPayload approved,
        RemediationPredecessor predecessor,
        string repository,
        TicketUpdateEvidence? ticket) =>
        new(
            approved.OriginReportId,
            approved.ServiceName,
            approved.Release,
            repository,
            gateway.BaseBranch,
            approved.BranchName,
            predecessor.ExternalResourceId!,
            approved.BaseCommitSha,
            approved.BaseTreeIdentity,
            approved.ResultTreeIdentity,
            approved.FilesChanged,
            approved.ProvedPathCount,
            approved.ExcludedPathCount,
            approved.CorrespondenceDigest,
            predecessor.ReportConfidence!,
            ParseIssueNumber(ticket),
            predecessor.ActionId,
            predecessor.ResultSha256);

    /// <summary>
    /// The cited ticket's number, or zero when the report cited none. A ticket id that is not a plain
    /// positive integer is treated as no ticket rather than rendered: the number reaches a public page,
    /// and the only shape that page will carry is one the provider itself numbers with.
    /// </summary>
    private static int ParseIssueNumber(TicketUpdateEvidence? ticket) =>
        ticket is not null && int.TryParse(
            ticket.TicketId,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var number) && number > 0
            ? number
            : 0;

    private async Task<string> ProposeAsync(
        PullRequestPayload payload,
        string tenantId,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        if (PullRequestPayloadFactory.Create(payload).CanonicalPayload.Length >
            ActionApprovalLimits.MaximumPayloadBytes)
        {
            return Refuse(jobId, payload.OriginReportId, PullRequestCodes.PayloadOversized);
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
                    PullRequestToolDescriptor.ToolId,
                    PullRequestPayloadFactory.ProposalKey(payload.OriginReportId),
                    PullRequestPayloadFactory.BuildArguments(payload)),
                cancellationToken);
        }
        catch (ConflictException)
        {
            // Same report, same key, different immutable input: the host binding, the cited ticket or
            // the confirmed head moved between two evaluations. Settled rather than retried, because
            // the frozen proposal a person may already be reading must not be repointed.
            return Refuse(jobId, payload.OriginReportId, PullRequestCodes.ProposalConflict);
        }

        switch (response.Outcome)
        {
            case PostReportActionProposalOutcome.Requested:
                LogProposalRequested(
                    logger, jobId, payload.OriginReportId, payload.HeadBranch, payload.IssueNumber,
                    payload.ReportConfidence, response.IsReplay);
                return PullRequestCodes.ProposalRequested;
            case PostReportActionProposalOutcome.Approved:
                LogProposalAutoApproved(logger, jobId, payload.OriginReportId);
                return PullRequestCodes.ProposalAutoApproved;
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
        EventId = 3812,
        Level = LogLevel.Information,
        Message = "Pull request for triage job {JobId} report {ReportId} was frozen into a proposal from head {HeadBranch}, citing issue number {IssueNumber} and {Confidence} report confidence, and is waiting for human approval (replay: {IsReplay}). No test was executed.")]
    private static partial void LogProposalRequested(
        ILogger logger,
        Guid jobId,
        Guid reportId,
        string headBranch,
        int issueNumber,
        string confidence,
        bool isReplay);

    [LoggerMessage(
        EventId = 3813,
        Level = LogLevel.Warning,
        Message = "Pull request for triage job {JobId} report {ReportId} produced no approvable proposal: {Outcome}")]
    private static partial void LogProposalRefused(
        ILogger logger,
        Guid jobId,
        Guid reportId,
        string outcome);

    [LoggerMessage(
        EventId = 3814,
        Level = LogLevel.Error,
        Message = "Pull request for triage job {JobId} report {ReportId} was auto-approved, which no shipped policy allows.")]
    private static partial void LogProposalAutoApproved(ILogger logger, Guid jobId, Guid reportId);
}
