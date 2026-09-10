using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.ModelGateway;
using IncidentCompass.Application.Core.Observability;
using IncidentCompass.Application.Core.Resilience;
using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Application.Intake.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IncidentCompass.Application.Investigation.Jobs;

/// <summary>
/// Dispatches one governed investigation model call and classifies how it ended. Attempt-budget
/// admission and charging belong to <see cref="TriageAttemptBudgetGate"/>, and durable
/// <c>ModelCall</c> accounting belongs to <see cref="ModelCallLedgerAccountant"/>; both share this
/// type's logger instance so log categories and event ids are unchanged by that split.
/// </summary>
internal sealed partial class InvestigationModelCaller(
    IAiModelClient modelClient,
    ITriageLedgerReader ledgerReader,
    TriageLedgerAppender ledgerAppender,
    TimeProvider timeProvider,
    IProviderOutageTracker? providerOutageTracker = null,
    IRuntimeTelemetry? telemetry = null,
    ILogger<InvestigationModelCaller>? logger = null)
{
    private readonly ILogger logger = ResolveLogger(logger);

    private readonly TriageAttemptBudgetGate budgetGate = new(
        ledgerReader,
        ledgerAppender,
        timeProvider,
        ResolveLogger(logger));

    private readonly ModelCallLedgerAccountant accountant = new(
        ledgerAppender,
        ResolveLogger(logger));

    public async Task<AiModelResponse> CompleteAsync(
        TriageJobCallContext context,
        TriageRouteSettings route,
        IReadOnlyList<AiChatMessage> messages,
        IReadOnlyList<AiToolDefinition>? tools,
        CancellationToken cancellationToken)
    {
        var usageBefore = await budgetGate.EnsureMayStartAsync(context, route, messages, tools, cancellationToken);
        var request = new AiModelRequest(
            CorrelationId: context.Job.Id.ToString(),
            Model: route.Model,
            Messages: messages,
            Temperature: route.Temperature,
            MaxOutputTokens: route.MaxOutputTokens,
            Tools: tools,
            ProviderId: route.ProviderId,
            Reasoning: route.Reasoning);

        var callId = Guid.NewGuid();
        var startedAtUtc = timeProvider.GetUtcNow();
        using var callCancellation = budgetGate.CreateCallCancellation(context, cancellationToken);
        using var modelTelemetry = telemetry?.StartModelCall();
        AiModelResponse response;
        try
        {
            callCancellation.Token.ThrowIfCancellationRequested();
            response = await modelClient.CompleteAsync(request, callCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var elapsedMs = (timeProvider.GetUtcNow() - startedAtUtc).TotalMilliseconds;
            telemetry?.RecordModelCall(RuntimeTelemetryOutcome.Cancelled, elapsedMs);
            LogModelCallWallClockCancelled(
                logger, context.Job.Id, context.Role, context.RouteId, context.CallKind, (long)elapsedMs);
            await ledgerAppender.AppendBudgetEventAsync(
                context.Job,
                "wall_clock_limit_reached: model call exceeded remaining attempt wall-clock budget.",
                tokensDelta: null,
                workersDelta: null,
                cancellationToken: CancellationToken.None);
            throw new TriageBudgetExhaustedException(
                TriageBudgetExhaustedException.WallClockReachedDuringCallCode,
                "The triage attempt exceeded MaxWallClockSeconds during a model call.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var elapsedMs = (timeProvider.GetUtcNow() - startedAtUtc).TotalMilliseconds;
            telemetry?.RecordModelCall(RuntimeTelemetryOutcome.Cancelled, elapsedMs);
            LogModelCallHostCancelled(
                logger, context.Job.Id, context.Role, context.RouteId, context.CallKind, (long)elapsedMs);
            throw;
        }
        catch (Exception exception) when (FindAiModelException(exception) is { } modelException)
        {
            var duration = timeProvider.GetUtcNow() - startedAtUtc;
            telemetry?.RecordModelCall(RuntimeTelemetryOutcome.Failed, duration.TotalMilliseconds);
            LogModelCallFailed(
                logger,
                context.Job.Id,
                context.Role,
                context.RouteId,
                context.CallKind,
                exception.GetType().Name,
                (long)duration.TotalMilliseconds);
            throw new InvestigationModelCallFailureException(
                ModelCallLedgerAccountant.CreateFailureAccounting(context, request, callId, modelException, duration),
                exception);
        }
        catch (Exception exception)
        {
            var duration = timeProvider.GetUtcNow() - startedAtUtc;
            telemetry?.RecordModelCall(RuntimeTelemetryOutcome.Failed, duration.TotalMilliseconds);
            LogModelCallFailed(
                logger,
                context.Job.Id,
                context.Role,
                context.RouteId,
                context.CallKind,
                exception.GetType().Name,
                (long)duration.TotalMilliseconds);
            throw;
        }

        var completedDuration = timeProvider.GetUtcNow() - startedAtUtc;
        telemetry?.RecordModelCall(RuntimeTelemetryOutcome.Succeeded, completedDuration.TotalMilliseconds);
        var chargedTokens = await accountant.RecordSuccessAsync(
            context,
            request,
            response,
            callId,
            completedDuration,
            CancellationToken.None);
        await budgetGate.ChargeTokensAsync(context, chargedTokens, usageBefore, CancellationToken.None);
        providerOutageTracker?.RecordProviderSuccess();
        return response;
    }

    private static ILogger<InvestigationModelCaller> ResolveLogger(ILogger<InvestigationModelCaller>? logger) =>
        logger ?? NullLogger<InvestigationModelCaller>.Instance;

    private static AiModelException? FindAiModelException(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is AiModelException modelException)
            {
                return modelException;
            }
        }

        return null;
    }

    [LoggerMessage(
        EventId = 3202,
        Level = LogLevel.Warning,
        Message = "Model call for triage job {JobId} role {Role} route {RouteId} kind {CallKind} failed with {ExceptionType} after {DurationMs}ms.")]
    private static partial void LogModelCallFailed(
        ILogger logger,
        Guid jobId,
        string? role,
        string routeId,
        string callKind,
        string exceptionType,
        long durationMs);

    [LoggerMessage(
        EventId = 3203,
        Level = LogLevel.Warning,
        Message = "Model call for triage job {JobId} role {Role} route {RouteId} kind {CallKind} was cancelled after {DurationMs}ms because the attempt wall-clock budget ran out.")]
    private static partial void LogModelCallWallClockCancelled(
        ILogger logger,
        Guid jobId,
        string? role,
        string routeId,
        string callKind,
        long durationMs);

    [LoggerMessage(
        EventId = 3204,
        Level = LogLevel.Information,
        Message = "Model call for triage job {JobId} role {Role} route {RouteId} kind {CallKind} was cancelled after {DurationMs}ms by host shutdown.")]
    private static partial void LogModelCallHostCancelled(
        ILogger logger,
        Guid jobId,
        string? role,
        string routeId,
        string callKind,
        long durationMs);
}
