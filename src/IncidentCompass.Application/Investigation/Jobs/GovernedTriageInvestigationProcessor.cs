using System.Text.Json;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Reports;
using IncidentCompass.Domain.Incidents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IncidentCompass.Application.Investigation.Jobs;

/// <summary>
/// Runs the bounded orchestrator investigation loop for one claimed triage job. Each turn ends in
/// exactly one named <see cref="OrchestratorTurnOutcome"/>; the loop itself only decides whether that
/// outcome finished the investigation and how many reprompts the next turn starts from.
/// </summary>
internal sealed partial class GovernedTriageInvestigationProcessor : IClaimedTriageJobProcessor
{
    private readonly ITriageJobInvestigationContextRepository contextRepository;
    private readonly InvestigationModelCaller modelCaller;
    private readonly AnalysisDelegateExecutor delegateExecutor;
    private readonly TriageReportPublisher reportPublisher;
    private readonly TriageLedgerAppender ledgerAppender;
    private readonly TimeProvider timeProvider;
    private readonly ILogger logger;

    public GovernedTriageInvestigationProcessor(
        ITriageJobInvestigationContextRepository contextRepository,
        InvestigationModelCaller modelCaller,
        AnalysisDelegateExecutor delegateExecutor,
        TriageReportPublisher reportPublisher,
        TriageLedgerAppender ledgerAppender,
        TimeProvider timeProvider,
        ILogger<GovernedTriageInvestigationProcessor>? logger = null)
    {
        this.contextRepository = contextRepository;
        this.modelCaller = modelCaller;
        this.delegateExecutor = delegateExecutor;
        this.reportPublisher = reportPublisher;
        this.ledgerAppender = ledgerAppender;
        this.timeProvider = timeProvider;
        this.logger = logger ?? NullLogger<GovernedTriageInvestigationProcessor>.Instance;
    }

    public async Task ProcessAsync(
        TriageJob job,
        TriageConfiguration configuration,
        string workerId,
        CancellationToken cancellationToken)
    {
        var attemptStartedAtUtc = timeProvider.GetUtcNow();
        var context = await contextRepository.GetAsync(job.Id, job.Attempt, cancellationToken);
        var messages = new List<AiChatMessage>
        {
            new(AiMessageRole.System, configuration.Orchestrator.Instructions),
            new(AiMessageRole.User, TriageInvestigationPromptBuilder.BuildOrchestratorPrompt(job, context))
        };

        // A reprompt turn is a correction turn, so the configured reprompt allowance is added on top of
        // the configured work-turn allowance: correcting a turn never costs the orchestrator a work turn.
        var budget = configuration.Orchestrator.Budget;
        var maxTurns = budget.MaxTurns + budget.MaxReprompts;
        var reprompts = 0;
        for (var turn = 0; turn < maxTurns; turn++)
        {
            var outcome = await RunTurnAsync(
                job, configuration, context, workerId, attemptStartedAtUtc, messages, reprompts, cancellationToken);
            reprompts = outcome.Reprompts;
            if (outcome.InvestigationFinished)
            {
                return;
            }
        }

        throw new TriageBudgetExhaustedException(
            TriageBudgetExhaustedException.OrchestratorTurnLimitReachedCode,
            "Orchestrator exceeded the bounded investigation turn limit before publish_report.");
    }

    private async Task<OrchestratorTurnOutcome> RunTurnAsync(
        TriageJob job,
        TriageConfiguration configuration,
        TriageJobInvestigationContext context,
        string workerId,
        DateTimeOffset attemptStartedAtUtc,
        List<AiChatMessage> messages,
        int reprompts,
        CancellationToken cancellationToken)
    {
        var response = await CompleteOrchestratorAsync(job, configuration, attemptStartedAtUtc, messages, cancellationToken);
        var toolCall = response.ProposedToolCalls is { Count: > 0 } proposedToolCalls
            ? proposedToolCalls[0]
            : null;
        if (toolCall is null)
        {
            const string diagnostic = "orchestrator response did not propose delegate or publish_report.";
            var afterNoToolCall = await RepromptOrThrowAsync(
                job,
                configuration,
                reprompts,
                "no_tool_call",
                diagnostic,
                "Orchestrator did not propose delegate or publish_report after bounded reprompts.",
                cancellationToken);
            messages.Add(new AiChatMessage(AiMessageRole.Assistant, response.Content));
            messages.Add(new AiChatMessage(
                AiMessageRole.User,
                "Validation error: the previous turn did not call delegate or publish_report. Call exactly one available tool."));
            return new OrchestratorTurnOutcome(OrchestratorTurnDisposition.NoToolCallReprompted, afterNoToolCall);
        }

        messages.Add(new AiChatMessage(AiMessageRole.Assistant, response.Content, ToolCalls: [toolCall]));
        if (string.Equals(toolCall.Name, OrchestratorToolNames.Delegate, StringComparison.Ordinal))
        {
            return await TryDelegateAsync(job, configuration, context, toolCall, attemptStartedAtUtc, messages, reprompts, cancellationToken);
        }

        if (string.Equals(toolCall.Name, OrchestratorToolNames.PublishReport, StringComparison.Ordinal))
        {
            return await TryPublishAsync(job, configuration, workerId, toolCall, messages, reprompts, cancellationToken);
        }

        const string unknownToolDiagnostic = "orchestrator proposed an unsupported tool.";
        var afterUnknownTool = await RepromptOrThrowAsync(
            job,
            configuration,
            reprompts,
            "unknown_tool",
            unknownToolDiagnostic,
            "Orchestrator proposed an unsupported tool after bounded reprompts.",
            cancellationToken);
        messages.Add(new AiChatMessage(AiMessageRole.Tool, UnknownToolResult(toolCall.Name), toolCall.Id));
        messages.Add(new AiChatMessage(AiMessageRole.User, "Validation error: unknown tool '" + toolCall.Name + "'. Call delegate or publish_report."));
        return new OrchestratorTurnOutcome(OrchestratorTurnDisposition.UnknownToolReprompted, afterUnknownTool);
    }

    private async Task<OrchestratorTurnOutcome> TryPublishAsync(
        TriageJob job,
        TriageConfiguration configuration,
        string workerId,
        AiToolCall toolCall,
        List<AiChatMessage> messages,
        int reprompts,
        CancellationToken cancellationToken)
    {
        try
        {
            await reportPublisher.PublishAsync(job, workerId, toolCall, cancellationToken);
            return new OrchestratorTurnOutcome(OrchestratorTurnDisposition.ReportPublished, reprompts);
        }
        catch (TriageReportValidationException exception)
        {
            var safeDiagnostic = OrchestratorRepromptDiagnostics.ForReportValidation(exception);
            var afterPublishFailure = await RepromptOrThrowAsync(
                job,
                configuration,
                reprompts,
                "publish_report_validation_failed",
                safeDiagnostic,
                "publish_report remained invalid after bounded reprompts.",
                cancellationToken,
                exception);
            var validationResult = JsonSerializer.Serialize(new
            {
                errorCode = "publish_report_validation_failed",
                errorMessage = safeDiagnostic
            });
            messages.Add(new AiChatMessage(AiMessageRole.Tool, validationResult, toolCall.Id));
            messages.Add(new AiChatMessage(
                AiMessageRole.User,
                "Validation error: " + safeDiagnostic + " Call publish_report again with the corrected report_json."));
            return new OrchestratorTurnOutcome(OrchestratorTurnDisposition.PublishRepromptIssued, afterPublishFailure);
        }
    }

    private async Task<OrchestratorTurnOutcome> TryDelegateAsync(
        TriageJob job,
        TriageConfiguration configuration,
        TriageJobInvestigationContext context,
        AiToolCall toolCall,
        DateTimeOffset attemptStartedAtUtc,
        List<AiChatMessage> messages,
        int reprompts,
        CancellationToken cancellationToken)
    {
        try
        {
            var toolResult = await delegateExecutor.ExecuteAsync(
                job,
                configuration,
                context,
                toolCall,
                attemptStartedAtUtc,
                cancellationToken);
            messages.Add(new AiChatMessage(AiMessageRole.Tool, toolResult, toolCall.Id));
            return new OrchestratorTurnOutcome(OrchestratorTurnDisposition.Delegated, reprompts);
        }
        catch (DelegateToolCallValidationException exception)
        {
            var safeDiagnostic = OrchestratorRepromptDiagnostics.ForDelegateValidation(exception);
            var afterDelegateFailure = await RepromptOrThrowAsync(
                job,
                configuration,
                reprompts,
                "delegate_validation_failed",
                safeDiagnostic,
                "delegate remained invalid after bounded reprompts.",
                cancellationToken,
                exception);
            var validationResult = JsonSerializer.Serialize(new
            {
                errorCode = "delegate_validation_failed",
                errorMessage = safeDiagnostic
            });
            messages.Add(new AiChatMessage(AiMessageRole.Tool, validationResult, toolCall.Id));
            messages.Add(new AiChatMessage(AiMessageRole.User, "Validation error: " + safeDiagnostic + " Call delegate again with object arguments containing role and task."));
            return new OrchestratorTurnOutcome(OrchestratorTurnDisposition.DelegateRepromptIssued, afterDelegateFailure);
        }
    }

    private async Task<AiModelResponse> CompleteOrchestratorAsync(
        TriageJob job,
        TriageConfiguration configuration,
        DateTimeOffset attemptStartedAtUtc,
        IReadOnlyList<AiChatMessage> messages,
        CancellationToken cancellationToken)
    {
        var routeId = configuration.Orchestrator.RouteId;
        if (!configuration.Routes.TryGetValue(routeId, out var route))
        {
            // Load-time validation rejects a configuration like this, but the loop must not depend on
            // having been handed a validated configuration: a rehydrated snapshot naming a route that
            // is not there fails closed under its own error code instead of throwing KeyNotFoundException.
            throw new TriageGovernanceDeniedException(
                TriageGovernanceDeniedException.OrchestratorRouteMissingCode,
                "Orchestrator route '" + routeId + "' is not a configured route in this triage configuration.");
        }

        return await modelCaller.CompleteAsync(
            new TriageJobCallContext(job, configuration, attemptStartedAtUtc, routeId, TriageModelCallKinds.Orchestrator),
            route,
            messages,
            OrchestratorToolDefinitions.Create(configuration),
            cancellationToken);
    }

    /// <summary>
    /// Charges one reprompt against the configured allowance and returns the new count, or throws when
    /// the allowance is already spent. Returning the count keeps the counter an ordinary local owned by
    /// the loop instead of shared mutable state written through a <c>ref</c> parameter.
    /// </summary>
    /// <remarks>
    /// A spent allowance is a bounded-run limit like any other, so it leaves under
    /// <see cref="TriageBudgetExhaustedException.OrchestratorRepromptLimitReachedCode"/>: the attempt
    /// dead-letters under its own durable code instead of consuming a retry under the generic
    /// attempt-failure code. One code covers every call site because the condition is one condition -
    /// the allowance is spent - while the cause of each individual correction turn is already durable
    /// per reprompt in log event 3401 and the matching <c>orchestrator_reprompt:</c> ledger
    /// <c>BudgetEvent</c>. The exhausting turn's own cause, which never becomes a reprompt, is logged
    /// here as event 3403 so nothing about why the allowance ran out depends on the error code alone.
    /// </remarks>
    private async Task<int> RepromptOrThrowAsync(
        TriageJob job,
        TriageConfiguration configuration,
        int reprompts,
        string repromptReason,
        string validationDiagnostic,
        string exhaustedMessage,
        CancellationToken cancellationToken,
        Exception? innerException = null)
    {
        if (reprompts >= configuration.Orchestrator.Budget.MaxReprompts)
        {
            LogOrchestratorRepromptLimitReached(
                logger,
                job.Id,
                job.Attempt,
                repromptReason,
                validationDiagnostic,
                configuration.Orchestrator.Budget.MaxReprompts);
            throw new TriageBudgetExhaustedException(
                TriageBudgetExhaustedException.OrchestratorRepromptLimitReachedCode,
                exhaustedMessage,
                innerException);
        }

        var chargedReprompts = reprompts + 1;
        LogOrchestratorReprompted(
            logger,
            job.Id,
            job.Attempt,
            repromptReason,
            validationDiagnostic,
            chargedReprompts,
            configuration.Orchestrator.Budget.MaxReprompts);
        await ledgerAppender.AppendRepromptBudgetEventAsync(
            job,
            "orchestrator",
            "orchestrator_reprompt: " + repromptReason + ": " + validationDiagnostic,
            cancellationToken);
        return chargedReprompts;
    }

    private static string UnknownToolResult(string toolName)
    {
        return JsonSerializer.Serialize(new { errorCode = "unknown_tool", toolName });
    }

    [LoggerMessage(
        EventId = 3401,
        Level = LogLevel.Information,
        Message = "Orchestrator for triage job {JobId} attempt {Attempt} was reprompted because of {RepromptReason} ({Reprompts}/{MaxReprompts}): {ValidationDiagnostic}")]
    private static partial void LogOrchestratorReprompted(
        ILogger logger,
        Guid jobId,
        int attempt,
        string repromptReason,
        string validationDiagnostic,
        int reprompts,
        int maxReprompts);

    [LoggerMessage(
        EventId = 3403,
        Level = LogLevel.Warning,
        Message = "Orchestrator for triage job {JobId} attempt {Attempt} spent its bounded reprompt allowance ({MaxReprompts}) and could not correct {RepromptReason}: {ValidationDiagnostic}")]
    private static partial void LogOrchestratorRepromptLimitReached(
        ILogger logger,
        Guid jobId,
        int attempt,
        string repromptReason,
        string validationDiagnostic,
        int maxReprompts);
}
