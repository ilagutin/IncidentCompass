using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Application.Intake.Configuration;
using Microsoft.Extensions.Logging;

namespace IncidentCompass.Application.Investigation.Jobs;

/// <summary>
/// Owns the attempt-budget decisions that surround a single investigation model call: what must
/// still hold before the call is allowed to start, how long the call may run, and what the attempt
/// is charged once the provider has answered.
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
        var usage = await ledgerReader.ReadBudgetUsageAsync(context.Job, cancellationToken);
        if (usage.TokensSpent >= context.Configuration.Orchestrator.Budget.MaxTokens)
        {
            await ledgerAppender.AppendBudgetEventAsync(
                context.Job,
                "max_tokens_reached_before_call: attempt token budget was already reached.",
                tokensDelta: null,
                workersDelta: null,
                cancellationToken: cancellationToken);
            LogBudgetLimitReached(logger, context.Job.Id, context.Job.Attempt, "max_tokens_reached_before_call");
            throw new TriageBudgetExhaustedException(
                TriageBudgetExhaustedException.MaxTokensReachedCode,
                "The triage attempt token budget was reached before the next model call.");
        }

        var elapsed = timeProvider.GetUtcNow() - context.AttemptStartedAtUtc;
        if (elapsed >= TimeSpan.FromSeconds(context.Configuration.Orchestrator.Budget.MaxWallClockSeconds))
        {
            await ledgerAppender.AppendBudgetEventAsync(
                context.Job,
                "wall_clock_limit_reached_before_call: attempt wall-clock budget was already reached.",
                tokensDelta: null,
                workersDelta: null,
                cancellationToken: cancellationToken);
            LogBudgetLimitReached(logger, context.Job.Id, context.Job.Attempt, "wall_clock_limit_reached_before_call");
            throw new TriageBudgetExhaustedException(
                TriageBudgetExhaustedException.WallClockReachedBeforeCallCode,
                "The triage attempt wall-clock budget was reached before the next model call.");
        }

        var estimatedPromptTokens = TriageTokenEstimator.EstimateMessages(messages, tools);
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

    public CancellationTokenSource CreateCallCancellation(
        TriageJobCallContext context,
        CancellationToken cancellationToken)
    {
        var elapsed = timeProvider.GetUtcNow() - context.AttemptStartedAtUtc;
        var remaining = TimeSpan.FromSeconds(context.Configuration.Orchestrator.Budget.MaxWallClockSeconds) - elapsed;
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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
}
