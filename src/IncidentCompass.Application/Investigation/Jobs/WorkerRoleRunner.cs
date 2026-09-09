using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Domain.Incidents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IncidentCompass.Application.Investigation.Jobs;

internal sealed partial class WorkerRoleRunner(
    InvestigationModelCaller modelCaller,
    WorkerToolCallExecutor toolCallExecutor,
    TriageLedgerAppender ledgerAppender,
    ILogger<WorkerRoleRunner>? logger = null)
{
    /// <summary>
    /// Turns the worker's bound allows beyond one per granted tool and one per configured reprompt.
    /// The role's own turn budget is deliberately not the orchestrator's <c>Budget.MaxTurns</c>: that
    /// one bounds orchestrator work turns for the whole attempt, while this bounds a single worker.
    /// </summary>
    private const int WorkerTurnSlack = 4;
    private const int MaxLoggedValidationDiagnosticLength = 1000;
    internal const string RepromptRationalePrefix = "worker_output_reprompt: ";

    private readonly ILogger logger = logger ?? NullLogger<WorkerRoleRunner>.Instance;

    public async Task<string> RunAsync(
        TriageJob job,
        TriageConfiguration configuration,
        TriageJobInvestigationContext context,
        string roleName,
        TriageRoleSettings role,
        string task,
        DateTimeOffset attemptStartedAtUtc,
        CancellationToken cancellationToken)
    {
        if (!configuration.Routes.TryGetValue(role.RouteId, out var route))
        {
            // Load-time validation rejects a configuration like this, but the worker loop must not
            // depend on having been handed a validated configuration: a rehydrated snapshot whose role
            // names a route it does not contain fails closed under its own governance error code
            // instead of throwing KeyNotFoundException out of the claim loop.
            throw new TriageGovernanceDeniedException(
                TriageGovernanceDeniedException.WorkerRouteMissingCode,
                "Role '" + roleName + "' names route '" + role.RouteId +
                "', which is not a configured route in this triage configuration.");
        }

        var toolSurface = toolCallExecutor.CreateToolSurface(configuration, role);
        var messages = new List<AiChatMessage>
        {
            new(AiMessageRole.System, role.Instructions),
            new(AiMessageRole.User, TriageInvestigationPromptBuilder.BuildWorkerPrompt(roleName, task, job, context))
        };

        var reprompts = 0;
        var maxTurns = configuration.Orchestrator.Budget.MaxReprompts + role.Tools.Count + WorkerTurnSlack;
        for (var turn = 0; turn < maxTurns; turn++)
        {
            var response = await modelCaller.CompleteAsync(
                new TriageJobCallContext(job, configuration, attemptStartedAtUtc, role.RouteId, TriageModelCallKinds.Worker, roleName),
                route,
                messages,
                toolSurface.Count > 0 ? toolSurface : null,
                cancellationToken);
            var toolCall = response.ProposedToolCalls is { Count: > 0 } proposedToolCalls
                ? proposedToolCalls[0]
                : null;
            if (toolCall is not null)
            {
                messages.Add(new AiChatMessage(AiMessageRole.Assistant, response.Content, ToolCalls: [toolCall]));
                var toolResult = await toolCallExecutor.ExecuteAsync(job, configuration, context, roleName, toolCall, cancellationToken);
                messages.Add(new AiChatMessage(AiMessageRole.Tool, toolResult, toolCall.Id));
                continue;
            }

            try
            {
                return AnalysisWorkerOutputSchemaValidator.Validate(response.Content, role.OutputSchema, roleName);
            }
            catch (WorkerOutputValidationException exception)
            {
                if (reprompts >= configuration.Orchestrator.Budget.MaxReprompts)
                {
                    throw new WorkerOutputInvalidException(exception);
                }

                reprompts++;
                var safeDiagnostic = exception.GetSafeDiagnostic(MaxLoggedValidationDiagnosticLength);
                LogWorkerReprompted(
                    logger,
                    job.Id,
                    job.Attempt,
                    roleName,
                    reprompts,
                    configuration.Orchestrator.Budget.MaxReprompts,
                    safeDiagnostic);
                await ledgerAppender.AppendRepromptBudgetEventAsync(
                    job,
                    roleName,
                    RepromptRationalePrefix + safeDiagnostic,
                    cancellationToken);
                messages.Add(new AiChatMessage(AiMessageRole.Assistant, response.Content));
                messages.Add(new AiChatMessage(
                    AiMessageRole.User,
                    TriageInvestigationPromptBuilder.BuildWorkerCorrectionPrompt(exception, role.OutputSchema)));
            }
        }

        throw new TriageBudgetExhaustedException(
            TriageBudgetExhaustedException.WorkerTurnLimitReachedCode,
            "Worker exceeded the bounded tool/reprompt turn limit.");
    }

    [LoggerMessage(
        EventId = 3402,
        Level = LogLevel.Warning,
        Message = "Worker {Role} for triage job {JobId} attempt {Attempt} was reprompted after output validation failed ({Reprompt}/{MaxReprompts}): {ValidationDiagnostic}")]
    private static partial void LogWorkerReprompted(
        ILogger logger,
        Guid jobId,
        int attempt,
        string role,
        int reprompt,
        int maxReprompts,
        string validationDiagnostic);
}
