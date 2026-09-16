using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Application.Intake.Configuration;
using Microsoft.Extensions.Logging;

namespace IncidentCompass.Application.Investigation.Jobs;

/// <summary>
/// Owns the attempt-budget decisions that surround a single investigation model call: what must
/// still hold before the call is allowed to start, how long the call may run, and what the attempt
/// is charged once the provider has answered. How long the call may run comes from the resolved
/// attempt duration ceiling (<see cref="OrchestratorBudgetSettings.ResolveAttemptDurationLimit"/>),
/// which an operator may disable.
/// </summary>
/// <remarks>
/// The logger is the caller's own <c>ILogger</c> instance, so budget events keep the
/// <c>InvestigationModelCaller</c> category and the event ids documented in
/// <c>docs/observability.md</c>.
/// </remarks>
internal sealed partial class TriageAttemptBudgetGate(
    ITriageLedgerReader ledgerReader,
    TriageLedgerAppender ledgerAppender,
    TimeProvider timeProvider,
    ILogger logger)
{
    public async Task<TriageBudgetLedgerUsage> EnsureMayStartAsync(
        TriageJobCallContext context,
        TriageRouteSettings route,
        IReadOnlyList<AiChatMessage> messages,
        IReadOnlyList<AiToolDefinition>? tools,
        CancellationToken cancellationToken)
    {
        var budget = context.Configuration.Orchestrator.Budget;
        var usage = await ledgerReader.ReadBudgetUsageAsync(context.Job, cancellationToken);
        var estimatedPromptTokens = TriageTokenEstimator.EstimateMessages(messages, tools);
        if (TriageOutputTokenBudget.RemainingForOutput(budget, usage, estimatedPromptTokens) <= 0)
        {
            await ledgerAppender.AppendBudgetEventAsync(
                context.Job,
                "max_tokens_reached_before_call: attempt token budget leaves no room for the estimated prompt and any output.",
                tokensDelta: null,
                workersDelta: null,
                cancellationToken: cancellationToken);
            LogBudgetLimitReached(logger, context.Job.Id, context.Job.Attempt, "max_tokens_reached_before_call");
            throw new TriageBudgetExhaustedException(
                TriageBudgetExhaustedException.MaxTokensReachedCode,
                "The triage attempt token budget was reached before the next model call.");
        }

        var elapsed = timeProvider.GetUtcNow() - context.AttemptStartedAtUtc;
        if (budget.ResolveAttemptDurationLimit() is { } attemptLimit && elapsed >= attemptLimit)
        {
            await ledgerAppender.AppendBudgetEventAsync(
                context.Job,
                "wall_clock_limit_reached_before_call: attempt duration ceiling was already reached.",
                tokensDelta: null,
                workersDelta: null,
                cancellationToken: cancellationToken);
            LogBudgetLimitReached(logger, context.Job.Id, context.Job.Attempt, "wall_clock_limit_reached_before_call");
            throw new TriageBudgetExhaustedException(
                TriageBudgetExhaustedException.WallClockReachedBeforeCallCode,
                "The triage attempt reached " + budget.ResolveAttemptDurationSettingName() +
                " before the next model call.");
        }

        if (route.ContextWindowTokens is { } contextWindowTokens && estimatedPromptTokens >= contextWindowTokens)
        {
            await ledgerAppender.AppendBudgetEventAsync(
                context.Job,
                "context_window_exceeded: estimated prompt exceeds the route context window.",
                tokensDelta: null,
                workersDelta: null,
                cancellationToken: cancellationToken);
            LogBudgetLimitReached(logger, context.Job.Id, context.Job.Attempt, "context_window_exceeded");
            throw new TriageBudgetExhaustedException(
                TriageBudgetExhaustedException.ContextWindowExceededCode,
                "The triage prompt exceeds the configured context window.");
        }

        return usage;
    }

    /// <summary>
    /// Whether a fallback hop still has output budget once the failed call is charged. A fallback
    /// that is not taken for this reason leaves the same audit trail as a call refused before
    /// dispatch: the <c>max_tokens_reached_before_call</c> budget event, naming the fallback route,
    /// and log event 3212.
    /// </summary>
    /// <remarks>
    /// The primary call's failure is what the caller rethrows when this returns
    /// <see langword="false"/>, and it carries accounting that is still owed. An audit append that
    /// fails here must not replace that failure, so the append is best-effort; the log event is
    /// written first and survives it.
    /// </remarks>
    public async Task<bool> AdmitsFallbackAsync(
        TriageJobCallContext context,
        string fallbackRouteId,
        TriageBudgetLedgerUsage fallbackUsage,
        IReadOnlyList<AiChatMessage> messages,
        IReadOnlyList<AiToolDefinition>? tools)
    {
        var budget = context.Configuration.Orchestrator.Budget;
        if (TriageOutputTokenBudget.RemainingForOutput(budget, fallbackUsage, messages, tools) > 0)
        {
            return true;
        }

        LogBudgetLimitReached(logger, context.Job.Id, context.Job.Attempt, "max_tokens_reached_before_call");
        try
        {
            await ledgerAppender.AppendBudgetEventAsync(
                context.Job,
                "max_tokens_reached_before_call: attempt token budget leaves no room for fallback route " +
                fallbackRouteId + " after the failed call was charged.",
                tokensDelta: null,
                workersDelta: null,
                cancellationToken: CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFallbackBudgetEventNotRecorded(logger, context.Job.Id, context.Job.Attempt, exception.GetType().Name);
        }

        return false;
    }

    /// <summary>
    /// The cancellation a model call runs under: the host token, plus the time left before the
    /// attempt duration ceiling when one is configured. With the ceiling disabled no attempt-level
    /// timer is armed, and only the host token and the provider transport limits bound the call.
    /// </summary>
    public CancellationTokenSource CreateCallCancellation(
        TriageJobCallContext context,
        CancellationToken cancellationToken)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (context.Configuration.Orchestrator.Budget.ResolveAttemptDurationLimit() is not { } attemptLimit)
        {
            return linked;
        }

        var remaining = attemptLimit - (timeProvider.GetUtcNow() - context.AttemptStartedAtUtc);
        if (remaining > TimeSpan.Zero)
        {
            linked.CancelAfter(remaining);
        }
        else
        {
            linked.Cancel();
        }

        return linked;
    }

    public async Task ChargeTokensAsync(
        TriageJobCallContext context,
        int totalTokens,
        TriageBudgetLedgerUsage usageBefore,
        CancellationToken cancellationToken)
    {
        LogBudgetTokensCharged(logger, context.Job.Id, context.Job.Attempt, totalTokens);

        if (usageBefore.TokensSpent + totalTokens > context.Configuration.Orchestrator.Budget.MaxTokens)
        {
            await ledgerAppender.AppendBudgetEventAsync(
                context.Job,
                "max_tokens_overshot_after_call: provider usage exceeded the attempt token budget after completion.",
                tokensDelta: null,
                workersDelta: null,
                cancellationToken: cancellationToken);
            LogBudgetLimitReached(logger, context.Job.Id, context.Job.Attempt, "max_tokens_overshot_after_call");
        }
    }

    [LoggerMessage(
        EventId = 3211,
        Level = LogLevel.Debug,
        Message = "Triage job {JobId} attempt {Attempt} charged {TokensDelta} model tokens to the attempt budget.")]
    private static partial void LogBudgetTokensCharged(
        ILogger logger,
        Guid jobId,
        int attempt,
        int tokensDelta);

    [LoggerMessage(
        EventId = 3212,
        Level = LogLevel.Warning,
        Message = "Triage job {JobId} attempt {Attempt} hit budget limit {BudgetReason}.")]
    private static partial void LogBudgetLimitReached(
        ILogger logger,
        Guid jobId,
        int attempt,
        string budgetReason);

    [LoggerMessage(
        EventId = 3213,
        Level = LogLevel.Warning,
        Message = "Triage job {JobId} attempt {Attempt} skipped a fallback for lack of token budget, but the budget event could not be recorded ({ExceptionType}).")]
    private static partial void LogFallbackBudgetEventNotRecorded(
        ILogger logger,
        Guid jobId,
        int attempt,
        string exceptionType);
}
