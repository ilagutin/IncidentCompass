using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Application.Intake.Configuration;

namespace IncidentCompass.Application.Investigation.Jobs;

/// <summary>
/// Turns the attempt token budget into the output allowance a single model call may request.
/// </summary>
/// <remarks>
/// The attempt budget counts what the provider reports for a call, prompt and completion together.
/// Before a call, what is left for the completion is the budget minus what the attempt already spent
/// minus the estimated prompt. Sending that remainder as the provider's output limit is what keeps
/// one call from spending far past the budget; the after-call overshoot event stays as the backstop,
/// because the prompt estimate is an approximation and can be low.
/// </remarks>
internal static class TriageOutputTokenBudget
{
    /// <summary>
    /// The tokens left for the completion of the next call. Zero or less means the call must not
    /// start.
    /// </summary>
    public static int RemainingForOutput(
        OrchestratorBudgetSettings budget,
        TriageBudgetLedgerUsage usage,
        int estimatedPromptTokens)
    {
        var remaining = (long)budget.MaxTokens - usage.TokensSpent - estimatedPromptTokens;
        return (int)Math.Clamp(remaining, int.MinValue, int.MaxValue);
    }

    /// <summary>
    /// The tokens left for the completion of a call carrying <paramref name="messages"/> and
    /// <paramref name="tools"/>.
    /// </summary>
    public static int RemainingForOutput(
        OrchestratorBudgetSettings budget,
        TriageBudgetLedgerUsage usage,
        IReadOnlyList<AiChatMessage> messages,
        IReadOnlyList<AiToolDefinition>? tools) =>
        RemainingForOutput(budget, usage, TriageTokenEstimator.EstimateMessages(messages, tools));

    /// <summary>
    /// The output limit a request carries: the route's own limit when it is smaller than what is
    /// left, otherwise what is left. A route without a limit gets the remainder.
    /// </summary>
    public static int LimitForRequest(TriageRouteSettings route, int remainingForOutput) =>
        Math.Min(route.MaxOutputTokens ?? remainingForOutput, remainingForOutput);
}
