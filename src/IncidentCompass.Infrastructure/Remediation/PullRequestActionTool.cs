using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Governance.Validation;
using IncidentCompass.Application.Remediation;
using IncidentCompass.Domain.Incidents.Actions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IncidentCompass.Infrastructure.Remediation;

/// <summary>
/// The external-action adapter behind the governed <c>pr_create</c> approval: it proves the head is
/// still the commit an earlier approved push confirmed, and opens at most one pull request.
/// </summary>
/// <remarks>
/// <para>
/// <b>What executing an approved pull request does, and what it does not.</b> It refuses a host bound
/// to another repository or another base branch, re-derives the exact title and body the approval
/// froze, reads the head reference and refuses unless it points at the approved commit, asks whether a
/// pull request for that head already exists, and only then opens one. It merges nothing, enables no
/// automatic merge, moves and deletes nothing, and changes no repository setting, because the port it
/// reaches the provider through has no operation that could and the request shape has no field that
/// could ask for one.
/// </para>
/// <para>
/// <b>At most one, and the preflight is also the reconciliation.</b> The head branch is derived from
/// the origin report and is created only by a governed push, so a pull request from that head is the
/// marker this create would leave. The listing read runs unconditionally before every create, so a
/// replay answers with the pull request that already exists rather than opening a second, and an
/// earlier attempt whose outcome was never known is settled by that same single read. There is no
/// state in which this adapter writes without having asked the question first.
/// </para>
/// <para>
/// <b>Nothing a model writes reaches a public page.</b> The description is composed from a report
/// identifier, an issue number, one of three confidence words, counts and digests; the diff is not a
/// parameter of it. The payload's own title and body are re-derived from those numbers and compared
/// byte for byte before anything is sent, so a row edited by hand cannot publish a sentence.
/// </para>
/// <para>
/// <b>No model can call it.</b> Only <c>IImmediateAgentTool</c> implementations are offered to a model;
/// this is not one, and the remediation pass asks its model with no tools at all.
/// </para>
/// </remarks>
public sealed partial class PullRequestActionTool : IExternalActionTool
{
    private readonly ICodePublicationGateway gateway;
    private readonly ILogger logger;

    public PullRequestActionTool(
        ICodePublicationGateway gateway,
        ILogger<PullRequestActionTool>? logger = null)
    {
        this.gateway = gateway;
        this.logger = logger ?? NullLogger<PullRequestActionTool>.Instance;
    }

    public ActionCategory Category => ActionCategory.PrCreate;

    public string LogicalTargetId => PullRequestToolDescriptor.LogicalTargetId;

    public string AdapterBindingFingerprint => gateway.BindingFingerprint;

    /// <summary>
    /// The argument contract, declared for the same reason every other external action declares one.
    /// It is not a model-facing surface; see the type remarks.
    /// </summary>
    public AiToolDefinition Definition { get; } = new(
        PullRequestToolDescriptor.ToolId,
        "Open one pull request from an approved, already pushed head into the configured base branch.",
        "v1",
        CanonicalJsonSerializer.ToElement(PullRequestArgumentSchema.Build()));

    public ToolValidationResult Validate(JsonElement arguments)
    {
        var payload = PullRequestPayloadFactory.TryReadArguments(arguments);
        return payload is null
            ? ToolValidationResult.Invalid("invalid_arguments", "Pull request arguments are invalid.")
            : ToolValidationResult.Valid(PullRequestPayloadFactory.BuildArguments(payload));
    }

    public ExternalActionPreparation Prepare(JsonElement sanitizedArguments) =>
        PullRequestPayloadFactory.Create(
            PullRequestPayloadFactory.TryReadArguments(sanitizedArguments)
            ?? throw new ArgumentException(
                "Pull request arguments are invalid.", nameof(sanitizedArguments)));

    public async Task<ExternalActionExecutionResult> ExecuteAsync(
        Guid actionId,
        ReadOnlyMemory<byte> canonicalPayload,
        CancellationToken cancellationToken)
    {
        var payload = PullRequestPayloadFactory.TryReadPayload(canonicalPayload.Span);
        if (payload is null)
        {
            return Failure(PullRequestCodes.PayloadInvalid);
        }

        if (!gateway.IsConfigured)
        {
            return Failure(CodePublicationCodes.BindingUnavailable);
        }

        if (!string.Equals(payload.Repository, gateway.ConfiguredRepository, StringComparison.Ordinal) ||
            !string.Equals(payload.BaseBranch, gateway.BaseBranch, StringComparison.Ordinal))
        {
            return Failure(PullRequestCodes.BindingChanged);
        }

        var opened = await gateway.CreatePullRequestAsync(
            new CodePublicationPullRequestRequest(
                payload.HeadBranch,
                payload.HeadCommitSha,
                PullRequestNarrative.Title(payload.OriginReportId),
                PullRequestNarrative.Body(payload)),
            cancellationToken);
        return opened.Number is null
            ? Refuse(actionId, opened.Code)
            : Opened(payload, opened);
    }

    /// <summary>
    /// The terminal record of a pull request that exists. Every claim in it is one this release can
    /// make, and the outcome distinguishes a create from a replay without changing either.
    /// </summary>
    private static ExternalActionExecutionResult Opened(
        PullRequestPayload payload,
        CodePublicationPullRequestResult opened)
    {
        var number = opened.Number!.Value.ToString(CultureInfo.InvariantCulture);
        var result = new JsonObject
        {
            ["baseBranch"] = payload.BaseBranch,
            ["headBranch"] = payload.HeadBranch,
            ["headCommitSha"] = payload.HeadCommitSha,
            ["issueNumber"] = payload.IssueNumber,
            ["merged"] = false,
            ["outcome"] = opened.Code,
            ["pullRequestNumber"] = number,
            ["reportConfidence"] = payload.ReportConfidence,
            ["repository"] = payload.Repository,
            ["schemaVersion"] = 1,
            ["testOutcome"] = RemediationDiff.TestNotExecuted
        };
        return new ExternalActionExecutionResult(
            true,
            Encoding.UTF8.GetBytes(CanonicalJsonSerializer.Canonicalize(result)),
            "One pull request is open from the approved head into the configured base branch. " +
            "Nothing was merged, no automatic merge was enabled, no reference was moved or deleted, " +
            "no repository setting was changed, and no test was executed.",
            AuditProjection: ExternalActionAuditProjection.GitHubPullRequestOpened(number));
    }

    private ExternalActionExecutionResult Refuse(Guid actionId, string code)
    {
        LogExecutionRefused(logger, actionId, code);
        return Failure(code);
    }

    private static ExternalActionExecutionResult Failure(string code)
    {
        var result = new JsonObject
        {
            ["code"] = code,
            ["merged"] = false,
            ["schemaVersion"] = 1
        };
        return new ExternalActionExecutionResult(
            false,
            Encoding.UTF8.GetBytes(CanonicalJsonSerializer.Canonicalize(result)),
            "No pull request was opened and nothing was merged.",
            code);
    }

    [LoggerMessage(
        EventId = 3815,
        Level = LogLevel.Warning,
        Message = "Approved pull request {ActionId} was refused: {Outcome}")]
    private static partial void LogExecutionRefused(ILogger logger, Guid actionId, string outcome);
}
