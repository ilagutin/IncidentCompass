using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Governance.Validation;
using IncidentCompass.Application.Remediation;
using IncidentCompass.Domain.Incidents.Actions;
using IncidentCompass.Infrastructure.SourceContext;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.Remediation;

/// <summary>
/// The external-action adapter behind the governed <c>code_write</c> approval: it freezes one
/// remediation diff into the payload a person approves, and re-applies exactly those bytes to a fresh
/// copy of the approved base when they do.
/// </summary>
/// <remarks>
/// <para>
/// <b>What executing an approved proposal does, and what it does not.</b> It materializes the
/// configured checkout again, refuses unless that copy is the tree the approval named, applies the
/// frozen diff to the copy, recomputes the resulting tree identity and compares it with the one the
/// approval named. Then the copy is discarded. Nothing is landed: no branch is pushed, no pull
/// request is opened, nothing is merged and no test is run, because the workspace port has no
/// operation that could and no process is started anywhere in the product. The recorded result says
/// so in the same words.
/// </para>
/// <para>
/// <b>Why that is worth doing rather than skipping.</b> It is the proof that the approval hash covers
/// everything that decides the tree. If the frozen bytes were enough, the recomputed identity equals
/// the approved one; if they were not, the two differ and the action fails instead of leaving a
/// silent discrepancy. It is also where base drift is caught the second time: the first check runs
/// when the proposal is created, and a checkout can move in the days between that and an approval.
/// </para>
/// <para>
/// <b>Nothing a model writes selects anything here.</b> The service and release come out of the
/// frozen payload, which was built from a durable row the backend wrote; the workspace root and the
/// directories those two resolve to are host options; the binding between them is hashed into
/// <see cref="AdapterBindingFingerprint" /> and compared before this type is reached. The only model
/// text on the path is the diff itself, and it is applied to a disposable copy under the same bounds
/// the source-read boundary enforces.
/// </para>
/// </remarks>
public sealed partial class RemediationApplyActionTool : IExternalActionTool
{
    /// <summary>The frozen payload is not the shape this release can execute.</summary>
    public const string PayloadInvalidCode = "remediation_payload_invalid";

    /// <summary>
    /// The approved diff applied, but the tree it produced is not the tree the approval named.
    /// </summary>
    public const string ResultMismatchCode = "remediation_result_mismatch";

    private readonly IRemediationWorkspace workspace;
    private readonly ILogger logger;

    public RemediationApplyActionTool(
        IRemediationWorkspace workspace,
        IOptions<SourceContextOptions> options,
        ILogger<RemediationApplyActionTool>? logger = null)
    {
        this.workspace = workspace;
        this.logger = logger ?? NullLogger<RemediationApplyActionTool>.Instance;
        AdapterBindingFingerprint = RemediationWorkspaceBinding.ComputeFingerprint(options.Value);
    }

    public ActionCategory Category => ActionCategory.CodeWrite;

    public string LogicalTargetId => RemediationApplyToolDescriptor.LogicalTargetId;

    public string AdapterBindingFingerprint { get; }

    /// <summary>
    /// The argument contract, declared for the same reason every other external action declares one.
    /// </summary>
    /// <remarks>
    /// It is not a model-facing surface. Only <c>IImmediateAgentTool</c> implementations are offered
    /// to a model, this is not one, and the remediation pass asks its model with no tools at all, so
    /// no turn a model may take can reach this name.
    /// </remarks>
    public AiToolDefinition Definition { get; } = new(
        RemediationApplyToolDescriptor.ToolId,
        "Apply one approved, backend-prepared remediation diff to a copy of its approved base tree.",
        "v1",
        CanonicalJsonSerializer.ToElement(new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["baseTreeIdentity"] = Hex64(),
                ["evidenceCount"] = new JsonObject { ["type"] = "integer" },
                ["evidenceSha256"] = Hex64(),
                ["filesChanged"] = new JsonObject { ["type"] = "integer" },
                ["originReportId"] = new JsonObject
                {
                    ["type"] = "string",
                    ["pattern"] = "^[0-9a-f]{32}$"
                },
                ["patch"] = new JsonObject { ["type"] = "string" },
                ["patchBytes"] = new JsonObject { ["type"] = "integer" },
                ["release"] = new JsonObject { ["type"] = "string" },
                ["resultTreeIdentity"] = Hex64(),
                ["serviceName"] = new JsonObject { ["type"] = "string" },
                ["testOutcome"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(RemediationDiff.TestNotExecuted)
                }
            },
            ["required"] = new JsonArray(
                "baseTreeIdentity", "evidenceCount", "evidenceSha256", "filesChanged",
                "originReportId", "patch", "patchBytes", "release", "resultTreeIdentity",
                "serviceName", "testOutcome")
        }));

    public ToolValidationResult Validate(JsonElement arguments)
    {
        var payload = RemediationProposalPayloadFactory.TryReadArguments(arguments);
        return payload is null
            ? ToolValidationResult.Invalid("invalid_arguments", "Remediation apply arguments are invalid.")
            : ToolValidationResult.Valid(RemediationProposalPayloadFactory.BuildArguments(payload));
    }

    public ExternalActionPreparation Prepare(JsonElement sanitizedArguments) =>
        RemediationProposalPayloadFactory.Create(
            RemediationProposalPayloadFactory.TryReadArguments(sanitizedArguments)
            ?? throw new ArgumentException(
                "Remediation apply arguments are invalid.", nameof(sanitizedArguments)));

    public async Task<ExternalActionExecutionResult> ExecuteAsync(
        Guid actionId,
        ReadOnlyMemory<byte> canonicalPayload,
        CancellationToken cancellationToken)
    {
        var payload = RemediationProposalPayloadFactory.TryReadPayload(canonicalPayload.Span);
        if (payload is null)
        {
            return Failure(PayloadInvalidCode);
        }

        var applied = await workspace.ApplyAsync(
            new RemediationApplyRequest(
                new RemediationTarget(payload.ServiceName, payload.Release),
                payload.BaseTreeIdentity,
                payload.PatchText),
            cancellationToken);
        if (applied.ResultTreeIdentity is null)
        {
            // A moved checkout arrives here as the adapter's own base-mismatch code, which the
            // workspace decides before the diff is parsed. The approval dies with that code on it
            // and a person has to be given a fresh one: nothing about re-running this makes the tree
            // the approval was taken against come back.
            LogExecutionRefused(logger, actionId, applied.Code);
            return Failure(applied.Code);
        }

        if (!string.Equals(applied.ResultTreeIdentity, payload.ResultTreeIdentity, StringComparison.Ordinal))
        {
            LogExecutionRefused(logger, actionId, ResultMismatchCode);
            return Failure(ResultMismatchCode);
        }

        return Verified(payload, applied.FilesChanged);
    }

    private static JsonObject Hex64() => new()
    {
        ["type"] = "string",
        ["pattern"] = "^[0-9a-f]{64}$"
    };

    /// <summary>
    /// The terminal record of a successful execution. Every claim in it is one this release can
    /// actually make: the diff applied to the approved base, the tree it produced is the approved
    /// one, nothing was landed and no test ran.
    /// </summary>
    private static ExternalActionExecutionResult Verified(RemediationProposalPayload payload, int filesChanged)
    {
        var result = new JsonObject
        {
            ["baseTreeIdentity"] = payload.BaseTreeIdentity,
            ["filesChanged"] = filesChanged,
            ["landed"] = false,
            ["outcome"] = "remediation_applied_to_copy",
            ["resultTreeIdentity"] = payload.ResultTreeIdentity,
            ["schemaVersion"] = 1,
            ["testOutcome"] = RemediationDiff.TestNotExecuted
        };
        return new ExternalActionExecutionResult(
            true,
            Encoding.UTF8.GetBytes(CanonicalJsonSerializer.Canonicalize(result)),
            "The approved diff applied to a fresh copy of the approved base and produced the approved " +
            "tree. Nothing was landed: no branch was pushed, no pull request was opened, nothing was " +
            "merged, and no test was executed.");
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
            "The approved diff was not applied and nothing was landed.",
            code);
    }

    [LoggerMessage(
        EventId = 3807,
        Level = LogLevel.Warning,
        Message = "Approved remediation action {ActionId} was refused before anything was changed: {Outcome}")]
    private static partial void LogExecutionRefused(ILogger logger, Guid actionId, string outcome);
}
