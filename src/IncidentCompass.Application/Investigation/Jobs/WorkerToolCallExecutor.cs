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

        var execution = await tool!.ExecuteAsync(
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
            await CommitSucceededAsync(
                job, configuration, roleName, toolCall.Name, redactedOutput, execution.Artifacts, cancellationToken);
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
        return SerializeToolFailure(execution.Status.ToString(), execution.ErrorCode, errorReason, limitation: errorReason);
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

    private async Task CommitSucceededAsync(
        TriageJob job,
        TriageConfiguration configuration,
        string roleName,
        string toolName,
        RedactedToolOutput redactedOutput,
        IReadOnlyCollection<ToolArtifactDraft>? drafts,
        CancellationToken cancellationToken)
    {
        var createdAtUtc = timeProvider.GetUtcNow();
        var artifacts = (drafts ?? [])
            .Select(draft => RedactedToolArtifactFactory.Create(job, draft, configuration.Redaction, createdAtUtc))
            .ToArray();
        var canonicalPayload = CanonicalJsonSerializer.Canonicalize(JsonNode.Parse(redactedOutput.Output.GetRawText())!);
        await toolResultCommitter.CommitSucceededAsync(
            new TriageToolResultCommitRequest(
                job,
                roleName,
                toolName,
                redactedOutput.Output,
                CanonicalJsonSerializer.ComputeSha256Hex(canonicalPayload),
                "Tool completed successfully.",
                redactedOutput.RedactionApplied,
                artifacts),
            cancellationToken);
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
