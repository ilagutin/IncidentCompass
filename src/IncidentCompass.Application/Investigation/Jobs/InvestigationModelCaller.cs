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
/// Dispatches one governed investigation model call, classifies how it ended and, for the failures
/// a route's declared fallback exists for, dispatches it once more there. Attempt-budget admission
/// and charging belong to <see cref="TriageAttemptBudgetGate"/>, and durable <c>ModelCall</c>
/// accounting belongs to <see cref="ModelCallLedgerAccountant"/>; both share this type's logger
/// instance so log categories and event ids are unchanged by that split.
/// <para>
/// Fail-over executes here rather than in the job runner or the provider adapter, and the reason is
/// the disposition table. The runner maps one provider failure kind to one job disposition; if it
/// were the runner that retried elsewhere, a single attempt would end carrying two failure kinds and
/// the table would stop being a statement about anything. Below this type, the adapter sees one
/// endpoint and one credential and has neither the route table nor the attempt budget, so it could
/// not decide the second call or pay for it. Here the route, its fallback, the ledger and the one
/// deadline are all in scope, and what leaves this type is still exactly one outcome.
/// </para>
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

    /// <summary>
    /// Runs one governed model call on <paramref name="route" />, and, when the route declares a
    /// fallback and the provider failed in a way a different provider could plausibly answer, once
    /// more on that fallback.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two calls share one admission and one deadline. Admission is charged once, before the
    /// primary call: a fail-over is the same logical call reaching a second endpoint, not a new
    /// request asking the budget for permission again. The deadline is the single
    /// <see cref="CancellationTokenSource" /> the budget gate created for the primary, handed to the
    /// fallback unchanged, so the attempt's remaining wall clock is what bounds both calls and no
    /// configured bound is silently doubled. A fallback that runs into that deadline therefore ends
    /// as a budget exhaustion, because the attempt really did run out of time.
    /// </para>
    /// <para>
    /// What the runner sees is still one failure kind with one disposition. A fallback that succeeds
    /// raises nothing; a fallback that fails at the provider raises the primary's failure, because
    /// the primary's kind is what ended this call and its disposition is the one the disposition
    /// table promises. The fallback's own accounting rides on that exception, and the primary's was
    /// already made durable before the second call was allowed to spend anything.
    /// </para>
    /// </remarks>
    public async Task<AiModelResponse> CompleteAsync(
        TriageJobCallContext context,
        TriageRouteSettings route,
        IReadOnlyList<AiChatMessage> messages,
        IReadOnlyList<AiToolDefinition>? tools,
        CancellationToken cancellationToken)
    {
        var usageBefore = await budgetGate.EnsureMayStartAsync(context, route, messages, tools, cancellationToken);
        using var callCancellation = budgetGate.CreateCallCancellation(context, cancellationToken);
        try
        {
            return await DispatchAsync(
                context,
                route,
                messages,
                tools,
                usageBefore,
                fallbackForRouteId: null,
                callCancellation.Token,
                cancellationToken);
        }
        catch (InvestigationModelCallFailureException primaryFailure)
        {
            var fallback = ModelRouteFallbackPolicy.TryResolve(context, route, primaryFailure);

            // No fallback to take, or no deadline left to spend on one. Either way the primary's
            // failure stands exactly as it does for a route that declared nothing.
            if (fallback is null || callCancellation.IsCancellationRequested)
            {
                throw;
            }

            return await CompleteOnFallbackAsync(
                context,
                fallback,
                messages,
                tools,
                usageBefore,
                primaryFailure,
                callCancellation.Token,
                cancellationToken);
        }
    }

    /// <summary>
    /// Retries a failed call once on <paramref name="fallback" />, reusing the primary's deadline.
    /// </summary>
    private async Task<AiModelResponse> CompleteOnFallbackAsync(
        TriageJobCallContext context,
        ModelRouteFallback fallback,
        IReadOnlyList<AiChatMessage> messages,
        IReadOnlyList<AiToolDefinition>? tools,
        TriageBudgetLedgerUsage usageBefore,
        InvestigationModelCallFailureException primaryFailure,
        CancellationToken callToken,
        CancellationToken cancellationToken)
    {
        var primaryAccounting = primaryFailure.Accounting;

        // The failed call is paid for before the second one is allowed to start. Nothing else would
        // write it: a fail-over that succeeds ends the attempt in success, and the attempt-failure
        // path that carries failed accounting today is the path this call is about to avoid.
        try
        {
            await ledgerAppender.AppendModelCallAccountingAsync(
                context.Job,
                primaryAccounting,
                CancellationToken.None);
        }
        catch (InvestigationModelCallFailureException accountingFailure)
        {
            // The failed call could not be charged, so no fail-over is attempted: the primary's
            // failure propagates with its accounting still owed, and the attempt-failure path
            // persists it under the job lock exactly as it does without a fallback.
            LogFallbackAbandoned(
                logger,
                context.Job.Id,
                context.RouteId,
                fallback.RouteId,
                (accountingFailure.InnerException ?? accountingFailure).GetType().Name);
            throw primaryFailure;
        }

        LogFallbackStarted(
            logger,
            context.Job.Id,
            context.Role,
            context.RouteId,
            fallback.RouteId,
            primaryAccounting.Metadata.ErrorCode ?? "unknown");

        try
        {
            return await DispatchAsync(
                context with { RouteId = fallback.RouteId },
                fallback.Route,
                messages,
                tools,
                usageBefore with
                {
                    TokensSpent = usageBefore.TokensSpent + (primaryAccounting.ChargeTokens ?? 0)
                },
                context.RouteId,
                callToken,
                cancellationToken);
        }
        catch (InvestigationModelCallFailureException fallbackFailure)
            when (ProviderOutageExceptionClassifier.FindFailureKind(fallbackFailure) is not null)
        {
            // One hop, and the primary's disposition. The new exception carries the fallback's own
            // accounting, which is what is still owed, over the primary failure, whose provider kind
            // is what the runner classifies. A fail-over whose ledger append failed carries no
            // provider exception and is left alone by this filter, so a successful fallback call
            // whose accounting is pending still reaches the runner unchanged.
            throw new InvestigationModelCallFailureException(fallbackFailure.Accounting, primaryFailure);
        }
    }

    private async Task<AiModelResponse> DispatchAsync(
        TriageJobCallContext context,
        TriageRouteSettings route,
        IReadOnlyList<AiChatMessage> messages,
        IReadOnlyList<AiToolDefinition>? tools,
        TriageBudgetLedgerUsage usageBefore,
        string? fallbackForRouteId,
        CancellationToken callToken,
        CancellationToken cancellationToken)
    {
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
        using var modelTelemetry = telemetry?.StartModelCall();
        AiModelResponse response;
        try
        {
            callToken.ThrowIfCancellationRequested();
            response = await modelClient.CompleteAsync(request, callToken);
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
                ModelCallLedgerAccountant.CreateFailureAccounting(
                    context,
                    request,
                    callId,
                    modelException,
                    duration,
                    fallbackForRouteId),
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
            fallbackForRouteId,
            CancellationToken.None);
        await budgetGate.ChargeTokensAsync(context, chargedTokens, usageBefore, CancellationToken.None);

        // Only a call on the route's own provider clears claim backpressure. A fallback answering is
        // evidence about the fallback's provider and none at all about the one that just failed, and
        // clearing on it would resume claiming against a provider that is still down.
        if (fallbackForRouteId is null)
        {
            providerOutageTracker?.RecordProviderSuccess();
        }

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

    [LoggerMessage(
        EventId = 3205,
        Level = LogLevel.Warning,
        Message = "Model call for triage job {JobId} role {Role} failed on route {RouteId} with {ErrorCode} and is being retried once on fallback route {FallbackRouteId}.")]
    private static partial void LogFallbackStarted(
        ILogger logger,
        Guid jobId,
        string? role,
        string routeId,
        string fallbackRouteId,
        string errorCode);

    [LoggerMessage(
        EventId = 3206,
        Level = LogLevel.Warning,
        Message = "Triage job {JobId} did not fail route {RouteId} over to fallback route {FallbackRouteId}: the failed call's accounting could not be made durable ({ExceptionType}).")]
    private static partial void LogFallbackAbandoned(
        ILogger logger,
        Guid jobId,
        string routeId,
        string fallbackRouteId,
        string exceptionType);
}
