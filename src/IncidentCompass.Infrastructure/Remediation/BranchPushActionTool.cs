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
/// The external-action adapter behind the governed <c>branch_push</c> approval: it re-derives the
/// approved change from the approved base, proves that base is the commit the approval named, and
/// creates one branch at one commit.
/// </summary>
/// <remarks>
/// <para>
/// <b>What executing an approved push does, and what it does not.</b> It re-reads the commit the
/// approval pinned, re-proves the correspondence against the approved base tree, re-applies the
/// approved diff to a fresh disposable copy, checks the tree it produces against the approved one, and
/// only then builds content-addressed objects and creates one new branch. It moves nothing, deletes
/// nothing, merges nothing and changes no repository setting, because the port it reaches the provider
/// through has no operation that could and the request shape has no field that could ask for one.
/// </para>
/// <para>
/// <b>Nothing a model writes selects anything here.</b> The repository, the base branch and the
/// credential are host options; the branch name is derived from the origin report; the base commit,
/// the diff and both tree identities come out of frozen bytes a person approved. The only model text
/// on the path is the diff, and it is applied to a disposable copy under the same bounds the
/// source-read boundary enforces before any of its bytes reach a request.
/// </para>
/// <para>
/// <b>No model can call it.</b> Only <c>IImmediateAgentTool</c> implementations are offered to a
/// model; this is not one, and the remediation pass asks its model with no tools at all.
/// </para>
/// </remarks>
public sealed partial class BranchPushActionTool : IExternalActionTool
{
    private readonly IRemediationWorkspace workspace;
    private readonly ICodePublicationGateway gateway;
    private readonly IBranchPushActionHistory history;
    private readonly ILogger logger;

    public BranchPushActionTool(
        IRemediationWorkspace workspace,
        ICodePublicationGateway gateway,
        IBranchPushActionHistory history,
        ILogger<BranchPushActionTool>? logger = null)
    {
        this.workspace = workspace;
        this.gateway = gateway;
        this.history = history;
        this.logger = logger ?? NullLogger<BranchPushActionTool>.Instance;
    }

    public ActionCategory Category => ActionCategory.BranchPush;

    public string LogicalTargetId => BranchPushToolDescriptor.LogicalTargetId;

    public string AdapterBindingFingerprint => gateway.BindingFingerprint;

    /// <summary>
    /// The argument contract, declared for the same reason every other external action declares one.
    /// It is not a model-facing surface; see the type remarks.
    /// </summary>
    public AiToolDefinition Definition { get; } = new(
        BranchPushToolDescriptor.ToolId,
        "Create one branch at one commit carrying an approved, backend-prepared remediation diff.",
        "v1",
        CanonicalJsonSerializer.ToElement(BranchPushArgumentSchema.Build()));

    public ToolValidationResult Validate(JsonElement arguments)
    {
        var payload = BranchPushPayloadFactory.TryReadArguments(arguments);
        return payload is null
            ? ToolValidationResult.Invalid("invalid_arguments", "Branch push arguments are invalid.")
            : ToolValidationResult.Valid(BranchPushPayloadFactory.BuildArguments(payload));
    }

    public ExternalActionPreparation Prepare(JsonElement sanitizedArguments) =>
        BranchPushPayloadFactory.Create(
            BranchPushPayloadFactory.TryReadArguments(sanitizedArguments)
            ?? throw new ArgumentException(
                "Branch push arguments are invalid.", nameof(sanitizedArguments)));

    public async Task<ExternalActionExecutionResult> ExecuteAsync(
        Guid actionId,
        ReadOnlyMemory<byte> canonicalPayload,
        CancellationToken cancellationToken)
    {
        var payload = BranchPushPayloadFactory.TryReadPayload(canonicalPayload.Span);
        if (payload is null)
        {
            return Failure(BranchPushCodes.PayloadInvalid);
        }

        if (!gateway.IsConfigured)
        {
            return Failure(CodePublicationCodes.BindingUnavailable);
        }

        if (!string.Equals(payload.Repository, gateway.ConfiguredRepository, StringComparison.Ordinal) ||
            !string.Equals(payload.BaseBranch, gateway.BaseBranch, StringComparison.Ordinal))
        {
            return Failure(BranchPushCodes.BindingChanged);
        }

        var prior = await history.ReadPriorAsync(actionId, cancellationToken);
        var settled = SettledByHistory(prior);
        if (settled is not null)
        {
            return settled;
        }

        if (prior.HasOutcomeUnknown)
        {
            var reconciled = await ReconcileAsync(payload, cancellationToken);
            if (reconciled is not null)
            {
                return reconciled;
            }
        }

        return await PushAsync(actionId, payload, cancellationToken);
    }

    /// <summary>
    /// Answers from durable state alone, before anything is read from or written to the provider.
    /// </summary>
    private static ExternalActionExecutionResult? SettledByHistory(BranchPushActionHistorySnapshot prior)
    {
        if (prior.HistoryLimitExceeded)
        {
            return Failure(BranchPushCodes.HistoryExceeded);
        }

        if (prior.ConfirmedCanonicalResult is not null)
        {
            return ExternalActionAuditProjection.IsValidResourceIdentity(
                ExternalActionAuditProjection.GitBranchKind, prior.ConfirmedCommitSha)
                ? new ExternalActionExecutionResult(
                    true,
                    prior.ConfirmedCanonicalResult,
                    prior.ConfirmedSummary ?? "A prior governed action already created the branch.",
                    AuditProjection: ExternalActionAuditProjection.GitBranchPushed(prior.ConfirmedCommitSha!))
                : Failure(BranchPushCodes.PriorResultInvalid);
        }

        return prior.HasPendingAction ? Failure(BranchPushCodes.PriorActionPending) : null;
    }

    /// <summary>
    /// Settles an earlier push whose outcome was never known, with one read.
    /// </summary>
    /// <remarks>
    /// The branch name is derived from the origin report, so it is the marker: no other action could
    /// have created it, and its absence is the answer that the earlier attempt wrote nothing. Absent
    /// means a person decides - nothing is retried automatically - and present means this dispatch
    /// carries on and refuses unless the branch turns out to point at exactly the commit it derives.
    /// </remarks>
    private async Task<ExternalActionExecutionResult?> ReconcileAsync(
        BranchPushPayload payload,
        CancellationToken cancellationToken)
    {
        var existing = await gateway.ReadBranchAsync(payload.BranchName, cancellationToken);
        if (existing.CommitSha is not null)
        {
            return null;
        }

        return existing.Code == CodePublicationCodes.BranchAbsent
            ? Failure(BranchPushCodes.PriorOutcomeUnknown)
            : Failure(existing.Code);
    }

    private async Task<ExternalActionExecutionResult> PushAsync(
        Guid actionId,
        BranchPushPayload payload,
        CancellationToken cancellationToken)
    {
        var remote = await gateway.ReadBaseAsync(payload.BaseCommitSha, cancellationToken);
        if (!remote.IsRead || !string.Equals(remote.TreeSha, payload.BaseTreeSha, StringComparison.Ordinal))
        {
            return Refuse(actionId, remote.IsRead ? BranchPushCodes.BindingChanged : remote.Code);
        }

        var prepared = await workspace.PrepareForPublicationAsync(
            new RemediationPublicationRequest(
                new RemediationTarget(payload.ServiceName, payload.Release),
                payload.BaseTreeIdentity,
                payload.ResultTreeIdentity,
                payload.PatchText,
                payload.BaseCommitSha,
                payload.BaseTreeSha,
                remote.BlobsByPath),
            cancellationToken);
        if (prepared.CorrespondenceDigest is null)
        {
            return Refuse(actionId, prepared.Code);
        }

        if (!string.Equals(prepared.CorrespondenceDigest, payload.CorrespondenceDigest, StringComparison.Ordinal))
        {
            return Refuse(actionId, BranchPushCodes.CorrespondenceChanged);
        }

        var pushed = await gateway.PushAsync(
            new CodePublicationPushRequest(
                payload.BranchName,
                payload.BaseCommitSha,
                payload.BaseTreeSha,
                BranchPushTreeEntries.Build(prepared.ChangedFiles, remote.FileModesByPath),
                BranchPushCommitMessage.For(payload),
                payload.CommitTimestampUtc),
            cancellationToken);
        return pushed.CommitSha is null
            ? Refuse(actionId, pushed.Code)
            : Created(payload, prepared, pushed);
    }

    /// <summary>
    /// The terminal record of a successful push. Every claim in it is one this release can make.
    /// </summary>
    private static ExternalActionExecutionResult Created(
        BranchPushPayload payload,
        RemediationPublicationResult prepared,
        CodePublicationRefResult pushed)
    {
        var result = new JsonObject
        {
            ["baseCommitSha"] = payload.BaseCommitSha,
            ["baseTreeIdentity"] = payload.BaseTreeIdentity,
            ["branchName"] = payload.BranchName,
            ["commitSha"] = pushed.CommitSha,
            ["correspondenceDigest"] = payload.CorrespondenceDigest,
            ["excludedPathCount"] = prepared.LocalOnlyPaths.Count,
            ["filesChanged"] = prepared.ChangedFiles.Count,
            ["landed"] = true,
            ["outcome"] = pushed.Code,
            ["provedPathCount"] = prepared.ProvedPathCount,
            ["repository"] = payload.Repository,
            ["resultTreeIdentity"] = payload.ResultTreeIdentity,
            ["schemaVersion"] = 1,
            ["testOutcome"] = RemediationDiff.TestNotExecuted
        };
        return new ExternalActionExecutionResult(
            true,
            Encoding.UTF8.GetBytes(CanonicalJsonSerializer.Canonicalize(result)),
            "One branch was created at one new commit on the approved base commit. Nothing else " +
            "changed: no existing reference was moved or deleted, nothing was merged, no pull request " +
            "was opened, and no test was executed.",
            AuditProjection: ExternalActionAuditProjection.GitBranchPushed(pushed.CommitSha!));
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
            ["landed"] = false,
            ["schemaVersion"] = 1
        };
        return new ExternalActionExecutionResult(
            false,
            Encoding.UTF8.GetBytes(CanonicalJsonSerializer.Canonicalize(result)),
            "No branch was created and nothing was landed.",
            code);
    }

    [LoggerMessage(
        EventId = 3811,
        Level = LogLevel.Warning,
        Message = "Approved branch push {ActionId} was refused: {Outcome}")]
    private static partial void LogExecutionRefused(ILogger logger, Guid actionId, string outcome);
}
