using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.Observability;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Governance.Validation;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Domain.Governance;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Domain.Incidents.Statuses;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IncidentCompass.Application.Investigation.Jobs;

internal sealed partial class WorkerToolCallExecutor(
    IEnumerable<IImmediateAgentTool> tools,
    ToolRuleEngine ruleEngine,
    TriageLedgerAppender ledgerAppender,
    ITriageToolResultCommitter toolResultCommitter,
    TimeProvider timeProvider,
    IRuntimeTelemetry? telemetry = null,
    ILogger<WorkerToolCallExecutor>? logger = null)
{
    private readonly IReadOnlyList<IImmediateAgentTool> tools = tools.ToArray();

    private readonly ILogger logger = logger ?? NullLogger<WorkerToolCallExecutor>.Instance;

    private readonly WorkerToolExecutionLimiter limiter = new(
        ledgerAppender,
        timeProvider,
        logger ?? NullLogger<WorkerToolCallExecutor>.Instance);

    private readonly InvestigationNoProgressRecorder noProgressRecorder = new(
        ledgerAppender,
        logger ?? NullLogger<WorkerToolCallExecutor>.Instance);

    public IReadOnlyList<AiToolDefinition> CreateToolSurface(TriageConfiguration configuration, TriageRoleSettings role)
    {
        var granted = role.Tools.ToHashSet(StringComparer.Ordinal);
        return tools
            .Where(tool => granted.Contains(tool.Definition.Name) && configuration.Tools.ContainsKey(tool.Definition.Name))
            .Select(static tool => tool.Definition)
            .OrderBy(static definition => definition.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<string> ExecuteAsync(
        TriageJob job,
        TriageConfiguration configuration,
        TriageJobInvestigationContext investigationContext,
        string roleName,
        AiToolCall toolCall,
        DateTimeOffset attemptStartedAtUtc,
        InvestigationProgressTracker progress,
        CancellationToken cancellationToken)
    {
        using var toolTelemetry = telemetry?.StartToolCall();
        await ledgerAppender.AppendAsync(
            job,
            TriageLedgerEventType.ToolProposed,
            roleName,
            toolCall.Name,
            "Worker proposed tool call.",
            payloadRef: toolCall.Id,
            cancellationToken);

        var tool = tools.FirstOrDefault(candidate =>
            string.Equals(candidate.Definition.Name, toolCall.Name, StringComparison.Ordinal));
        var validation = tool?.Validate(toolCall.Arguments);
        var decision = await DecideAsync(job, configuration, roleName, toolCall, tool, validation, cancellationToken);
        await ledgerAppender.AppendPolicyDecisionAsync(
            job,
            roleName,
            toolCall.Name,
            decision.Decision,
            decision.Reason,
            cancellationToken);

        if (decision.Decision == TriageLedgerDecision.ApprovalRequired)
        {
            telemetry?.RecordToolCall(RuntimeTelemetryOutcome.Denied);
            LogWorkerToolApprovalRequired(logger, job.Id, job.Attempt, roleName, toolCall.Name);
            return SerializeToolFailure(ToolExecutionStatus.ApprovalRequired.ToString(), "approval_required", decision.Reason, limitation: decision.Reason);
        }

        if (!decision.MayProceed)
        {
            telemetry?.RecordToolCall(RuntimeTelemetryOutcome.Denied);
            throw new TriageGovernanceDeniedException(
                TriageGovernanceDeniedException.WorkerToolDeniedCode,
                "Worker tool call denied: " + decision.Reason);
        }

        if (validation is null || !validation.IsValid)
        {
            telemetry?.RecordToolCall(RuntimeTelemetryOutcome.Denied);
            throw new TriageGovernanceDeniedException(
                TriageGovernanceDeniedException.WorkerToolValidationFailedCode,
                "Worker tool call validation failed after policy approval.");
        }

        // Governance decides first, so rate_cap and every other rule see the call exactly as before.
        // Only a call the policy allowed can be refused as an unproductive repeat, and a refused
        // repeat is not executed, so it writes no ToolResult: its no_progress budget event says why.
        var fingerprint = EquivalentCallFingerprint.ForWorkerTool(toolCall.Name, validation.SanitizedArguments);
        if (progress.IsRepeatLimitReached(fingerprint, out var unproductiveRepeats))
        {
            telemetry?.RecordToolCall(RuntimeTelemetryOutcome.Refused);
            progress.Activity.RecordRefusal(roleName, toolCall.Name);
            await noProgressRecorder.RecordRepeatedCallAsync(
                job, roleName, toolCall.Name, fingerprint, unproductiveRepeats, progress.MaxEquivalentCalls, cancellationToken);
            return SerializeToolFailure(
                ToolExecutionStatus.NotExecuted.ToString(),
                InvestigationNoProgressRecorder.RepeatedCallErrorCode,
                InvestigationNoProgressRecorder.RepeatedCallErrorMessage,
                limitation: InvestigationNoProgressRecorder.RepeatedCallErrorMessage);
        }

        // The limiter owns every way the call can end short of a returned result: a per-tool timeout
        // comes back as an ordinary Failed result and takes the failure path below, while the attempt
        // ceiling, host cancellation and a thrown tool leave this method as exceptions.
        progress.Activity.RecordToolCallExecuted();
        var execution = await limiter.ExecuteAsync(
            tool!,
            new AgentToolExecutionContext(
                job,
                configuration,
                roleName,
                toolCall.Name,
                investigationContext.Fault.TenantId,
                investigationContext.Fault.ServiceName)
            {
                TriggerSignal = investigationContext.TriggerSignal,
                FaultFingerprint = investigationContext.Fault.Fingerprint
            },
            validation.SanitizedArguments,
            attemptStartedAtUtc,
            cancellationToken);
        if (execution.Status == ToolExecutionStatus.Succeeded)
        {
            // Redaction happens once, here, for both surfaces the connector text reaches: the tool
            // message this turn returns to the model and every durable artifact the commit writes.
            // A tool cannot do this itself - it hands back drafts, not artifacts - so a new tool
            // cannot forget it.
            var redactedOutput = RedactedToolArtifactFactory.RedactOutput(
                execution.Output,
                configuration.Redaction);
            var identity = await CommitSucceededAsync(
                job, configuration, roleName, toolCall.Name, redactedOutput, execution.Artifacts, progress, cancellationToken);
            progress.RecordCallResult(fingerprint, identity);
            progress.RecordEvidence(identity);
            telemetry?.RecordToolCall(RuntimeTelemetryOutcome.Succeeded);
            LogWorkerToolExecuted(logger, job.Id, job.Attempt, roleName, toolCall.Name);
            return redactedOutput.Output.GetRawText();
        }

        // The failure path reaches the same two surfaces the success path does - this turn's tool
        // message and durable ledger state - so the message a tool failed with is redacted on the way
        // out too. Nothing shipped builds it from connector text, and this is what keeps that from
        // being something a new tool can quietly change.
        var errorReason = RedactedToolArtifactFactory.RedactReason(
            execution.ErrorMessage ?? "Tool execution failed.",
            configuration.Redaction);
        await AppendExecutedToolFailureAsync(job, roleName, toolCall.Name, execution.Status.ToString(), errorReason, cancellationToken);
        telemetry?.RecordToolCall(RuntimeTelemetryOutcome.Failed);
        LogWorkerToolExecutionFailed(
            logger,
            job.Id,
            job.Attempt,
            roleName,
            toolCall.Name,
            execution.Status,
            execution.ErrorCode ?? "unspecified");
        var failure = SerializeToolFailure(execution.Status.ToString(), execution.ErrorCode, errorReason, limitation: errorReason);

        // A failure is not evidence, but the same failure again is still no new result for this call.
        progress.RecordCallResult(fingerprint, progress.IdentityOf(failure));
        return failure;
    }

    private async Task<ToolRulePolicyResult> DecideAsync(
        TriageJob job,
        TriageConfiguration configuration,
        string roleName,
        AiToolCall toolCall,
        IImmediateAgentTool? tool,
        ToolValidationResult? validation,
        CancellationToken cancellationToken)
    {
        if (tool is null)
        {
            LogWorkerToolDenied(
                logger, job.Id, job.Attempt, roleName, toolCall.Name, ToolPolicyDenialReasons.ToolNotRegistered);
            return ToolRulePolicyResult.Denied(ToolPolicyDenialReasons.ToolNotRegistered);
        }

        if (validation is null || !validation.IsValid)
        {
            // The validator message can echo model-supplied arguments, so only the bounded
            // classification token reaches the log; the full reason stays in the durable ledger, where
            // it is the detail behind the same reason code the log records.
            LogWorkerToolDenied(
                logger, job.Id, job.Attempt, roleName, toolCall.Name, ToolPolicyDenialReasons.ToolArgumentsInvalid);
            return ToolRulePolicyResult.Denied(
                ToolPolicyDenialReasons.ToolArgumentsInvalid, validation?.ErrorMessage);
        }

        return await ruleEngine.DecideImmediateAsync(job, configuration, roleName, toolCall.Name, cancellationToken);
    }

    /// <summary>
    /// Commits the result and returns its <see cref="EvidenceResultIdentity"/>: each per-item artifact
    /// is registered under its content hash first, so the <c>artifactId</c> the output names for it is
    /// read as that content, and the <c>ToolResult</c> artifact is registered under the identity.
    /// </summary>
    private async Task<string> CommitSucceededAsync(
        TriageJob job,
        TriageConfiguration configuration,
        string roleName,
        string toolName,
        RedactedToolOutput redactedOutput,
        IReadOnlyCollection<ToolArtifactDraft>? drafts,
        InvestigationProgressTracker progress,
        CancellationToken cancellationToken)
    {
        var createdAtUtc = timeProvider.GetUtcNow();
        var artifacts = (drafts ?? [])
            .Select(draft => RedactedToolArtifactFactory.Create(job, draft, configuration.Redaction, createdAtUtc))
            .ToArray();

        // The ToolResult row and the per-item artifacts this call produced are both citable, and the
        // pairing is the guarantee: they carry the same connector text, so if only one of them
        // recorded a redaction the model would choose whether the report's limitation appeared by
        // choosing which of the two to cite. redactedOutput answers only for the output document,
        // and an artifact can be redacted where the output was not - the domain reference is
        // redacted too, and it is not part of the output. So the row answers for the whole call.
        var redactionApplied = redactedOutput.RedactionApplied ||
            Array.Exists(artifacts, artifact => artifact.RedactionApplied == true);
        var canonicalPayload = CanonicalJsonSerializer.Canonicalize(JsonNode.Parse(redactedOutput.Output.GetRawText())!);
        var contentHash = CanonicalJsonSerializer.ComputeSha256Hex(canonicalPayload);
        var toolResult = await toolResultCommitter.CommitSucceededAsync(
            new TriageToolResultCommitRequest(
                job,
                roleName,
                toolName,
                redactedOutput.Output,
                contentHash,
                "Tool completed successfully.",
                redactionApplied,
                artifacts),
            cancellationToken);
        foreach (var artifact in artifacts)
        {
            progress.RegisterArtifact(artifact.Id, artifact.ContentHash);
        }

        var identity = progress.IdentityOf(redactedOutput.Output);
        progress.RegisterArtifact(toolResult.Id, identity);
        return identity;
    }

    private async Task AppendExecutedToolFailureAsync(
        TriageJob job,
        string roleName,
        string toolName,
        string status,
        string reason,
        CancellationToken cancellationToken)
    {
        var rationale = status + ": " + reason;
        await ledgerAppender.AppendToolResultAsync(
            job,
            roleName,
            toolName,
            TriageLedgerToolStatus.Failed,
            rationale,
            payloadRef: null,
            cancellationToken);
    }

    private static string SerializeToolFailure(
        string status,
        string? errorCode,
        string errorMessage,
        string? limitation)
    {
        return JsonSerializer.Serialize(new
        {
            status,
            errorCode,
            errorMessage,
            limitation
        });
    }

    [LoggerMessage(
        EventId = 3301,
        Level = LogLevel.Warning,
        Message = "Worker tool call {ToolName} for role {Role} on triage job {JobId} attempt {Attempt} was denied: {DenialReason}.")]
    private static partial void LogWorkerToolDenied(
        ILogger logger,
        Guid jobId,
        int attempt,
        string role,
        string toolName,
        string denialReason);

    [LoggerMessage(
        EventId = 3302,
        Level = LogLevel.Information,
        Message = "Worker tool call {ToolName} for role {Role} on triage job {JobId} attempt {Attempt} was not executed because it requires approval.")]
    private static partial void LogWorkerToolApprovalRequired(
        ILogger logger,
        Guid jobId,
        int attempt,
        string role,
        string toolName);

    [LoggerMessage(
        EventId = 3303,
        Level = LogLevel.Debug,
        Message = "Worker tool call {ToolName} for role {Role} on triage job {JobId} attempt {Attempt} executed successfully.")]
    private static partial void LogWorkerToolExecuted(
        ILogger logger,
        Guid jobId,
        int attempt,
        string role,
        string toolName);

    [LoggerMessage(
        EventId = 3304,
        Level = LogLevel.Warning,
        Message = "Worker tool call {ToolName} for role {Role} on triage job {JobId} attempt {Attempt} ended as {ToolStatus} with error code {ToolErrorCode}.")]
    private static partial void LogWorkerToolExecutionFailed(
        ILogger logger,
        Guid jobId,
        int attempt,
        string role,
        string toolName,
        ToolExecutionStatus toolStatus,
        string toolErrorCode);
}
