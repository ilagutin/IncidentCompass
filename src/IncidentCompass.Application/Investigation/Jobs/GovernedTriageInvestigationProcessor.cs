using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Core.Text;
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
internal sealed class GovernedTriageInvestigationProcessor : IClaimedTriageJobProcessor
{
    /// <summary>
    /// The classification that opens every orchestrator reprompt rationale in the ledger. It is
    /// followed by the closed reprompt reason and then the turn's diagnostic; the appender charges the
    /// whole prefix against <see cref="TriageLedgerAppender.MaxRepromptRationaleLength" />, so the
    /// classification survives even when the diagnostic has to be cut.
    /// </summary>
    internal const string OrchestratorRepromptRationalePrefix = "orchestrator_reprompt: ";

    /// <summary>
    /// The cap, in UTF-16 code units, on the model-authored tool name the two unknown-tool messages
    /// echo. A tool name the model invented is unbounded text, and both messages exist only to tell
    /// the model which of its own words was not a tool, which a short prefix of the name does. The
    /// cut is <see cref="TextTruncator"/>'s, so it lands on a rune boundary.
    /// </summary>
    internal const int MaxEchoedToolNameLength = 128;

    private readonly ITriageJobInvestigationContextRepository contextRepository;
    private readonly InvestigationModelCaller modelCaller;
    private readonly AnalysisDelegateExecutor delegateExecutor;
    private readonly TriageReportPublisher reportPublisher;
    private readonly TriageLedgerAppender ledgerAppender;
    private readonly TimeProvider timeProvider;
    private readonly ILogger logger;
    private readonly InvestigationNoProgressHandler noProgressHandler;
    private readonly OrchestratorRepromptCharger repromptCharger;

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
        noProgressHandler = new InvestigationNoProgressHandler(modelCaller, reportPublisher, ledgerAppender, this.logger);
        repromptCharger = new OrchestratorRepromptCharger(ledgerAppender, this.logger);
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
        var progress = InvestigationProgressTracker.For(budget);
        var stall = new NoProgressStall(job, configuration, context, workerId, attemptStartedAtUtc, messages, progress);
        for (var turn = 0; turn < maxTurns; turn++)
        {
            OrchestratorTurnOutcome outcome;
            try
            {
                outcome = await RunTurnAsync(
                    job, configuration, context, workerId, attemptStartedAtUtc, messages, reprompts, progress, cancellationToken);
            }
            catch (TriageBudgetExhaustedException exhausted) when (
                exhausted.ErrorCode == TriageBudgetExhaustedException.MaxWorkersReachedCode && progress.StallDetected)
            {
                // The worker budget ran out during a stall the tracker detected and no progress has
                // ended since: the honest end is the backend report. Without such a stall it dead-letters.
                await noProgressHandler.TerminateAsync(stall, NoProgressTerminationReason.WorkerBudgetDuringStall, cancellationToken);
                return;
            }

            reprompts = outcome.Reprompts;
            if (outcome.InvestigationFinished)
            {
                return;
            }

            // Past the no-progress window the handler recovers while a recovery and a further window
            // fit, and otherwise publishes the backend's InsufficientEvidence report.
            if (progress.CompleteTurn().LimitExceeded &&
                await noProgressHandler.HandleAsync(stall, maxTurns - turn - 1, cancellationToken))
            {
                return;
            }
        }

        // Out of turns. A run inside a detected stall that no progress has ended since ends with the
        // backend report; any other run dead-letters on the turn limit as before.
        if (progress.StallDetected)
        {
            await noProgressHandler.TerminateAsync(stall, NoProgressTerminationReason.TurnLimitDuringStall, cancellationToken);
            return;
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
        InvestigationProgressTracker progress,
        CancellationToken cancellationToken)
    {
        var response = await CompleteOrchestratorAsync(job, configuration, attemptStartedAtUtc, messages, cancellationToken);
        var toolCall = response.ProposedToolCalls is { Count: > 0 } proposedToolCalls
            ? proposedToolCalls[0]
            : null;
        if (toolCall is null)
        {
            const string diagnostic = "orchestrator response did not propose delegate or publish_report.";
            var afterNoToolCall = await repromptCharger.ChargeOrThrowAsync(
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
            return await TryDelegateAsync(
                job, configuration, context, toolCall, attemptStartedAtUtc, messages, reprompts, progress, cancellationToken);
        }

        if (string.Equals(toolCall.Name, OrchestratorToolNames.PublishReport, StringComparison.Ordinal))
        {
            return await TryPublishAsync(job, configuration, workerId, toolCall, messages, reprompts, cancellationToken);
        }

        const string unknownToolDiagnostic = "orchestrator proposed an unsupported tool.";
        var afterUnknownTool = await repromptCharger.ChargeOrThrowAsync(
            job,
            configuration,
            reprompts,
            "unknown_tool",
            unknownToolDiagnostic,
            "Orchestrator proposed an unsupported tool after bounded reprompts.",
            cancellationToken);
        // Both messages echo the same capped name, so the tool result and the reprompt cannot
        // disagree about what the model called.
        var unsupportedToolName = CapToolName(toolCall.Name);
        messages.Add(new AiChatMessage(AiMessageRole.Tool, UnknownToolResult(unsupportedToolName), toolCall.Id));
        messages.Add(new AiChatMessage(AiMessageRole.User, UnknownToolReprompt(unsupportedToolName)));
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
            var afterPublishFailure = await repromptCharger.ChargeOrThrowAsync(
                job,
                configuration,
                reprompts,
                "publish_report_validation_failed",
                safeDiagnostic,
                "publish_report remained invalid after bounded reprompts.",
                cancellationToken,
                exception);
            var validationResult = ModelFacingJson.Serialize(new
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
        InvestigationProgressTracker progress,
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
                progress,
                cancellationToken);
            messages.Add(new AiChatMessage(AiMessageRole.Tool, toolResult, toolCall.Id));
            return new OrchestratorTurnOutcome(OrchestratorTurnDisposition.Delegated, reprompts);
        }
        catch (RepeatedDelegateRefusedException)
        {
            // Same shape as a delegate validation result, but no reprompt is charged: the call was
            // well formed, it only asked again for what the attempt already has.
            var refusal = ModelFacingJson.Serialize(new
            {
                errorCode = InvestigationNoProgressRecorder.RepeatedCallErrorCode,
                errorMessage = InvestigationNoProgressRecorder.RepeatedCallErrorMessage
            });
            messages.Add(new AiChatMessage(AiMessageRole.Tool, refusal, toolCall.Id));
            return new OrchestratorTurnOutcome(OrchestratorTurnDisposition.DelegateRefusedAsRepeated, reprompts);
        }
        catch (DelegateToolCallValidationException exception)
        {
            var safeDiagnostic = OrchestratorRepromptDiagnostics.ForDelegateValidation(exception);
            var afterDelegateFailure = await repromptCharger.ChargeOrThrowAsync(
                job,
                configuration,
                reprompts,
                "delegate_validation_failed",
                safeDiagnostic,
                "delegate remained invalid after bounded reprompts.",
                cancellationToken,
                exception);
            var validationResult = ModelFacingJson.Serialize(new
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
    /// Caps the model-authored tool name both unknown-tool messages carry to
    /// <see cref="MaxEchoedToolNameLength"/>.
    /// </summary>
    private static string CapToolName(string toolName) =>
        TextTruncator.Truncate(toolName, MaxEchoedToolNameLength);

    /// <summary>
    /// The tool message for an unsupported tool. <paramref name="toolName"/> is whatever the model
    /// named, already capped, and it is a JSON string literal written by the model-facing encoder
    /// like every other tool message.
    /// </summary>
    private static string UnknownToolResult(string toolName)
    {
        return ModelFacingJson.Serialize(new { errorCode = "unknown_tool", toolName });
    }

    /// <summary>
    /// The user message that asks for a supported tool. The sentence is the backend's; the name in
    /// it is the capped model text, written as one JSON string literal so a name carrying a newline,
    /// a quote or a line that looks backend-authored stays one value on one line.
    /// </summary>
    private static string UnknownToolReprompt(string toolName) =>
        "Validation error: unknown tool " + ModelFacingJson.SerializeString(toolName) +
        ". Call delegate or publish_report.";
}
