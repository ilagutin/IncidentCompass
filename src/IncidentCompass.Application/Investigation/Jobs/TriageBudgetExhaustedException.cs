namespace IncidentCompass.Application.Investigation.Jobs;

/// <summary>
/// Raised when a triage attempt reaches a configured bounded-run budget: tokens, wall clock, the
/// route context window, the attempt worker budget, the bounded orchestrator or worker turn limit,
/// or the orchestrator's bounded reprompt allowance. The condition is permanent for the attempt, so
/// <see cref="TriageJobRunner"/> dead-letters it instead of spending the retry budget on work that
/// cannot succeed. <see cref="ErrorCode"/> lands in the job's <c>last_error_code</c> column.
/// </summary>
internal sealed class TriageBudgetExhaustedException : Exception
{
    public const string MaxTokensReachedCode = "triage_budget_max_tokens_reached";

    public const string WallClockReachedDuringCallCode = "triage_budget_wall_clock_reached_during_call";

    public const string WallClockReachedBeforeCallCode = "triage_budget_wall_clock_reached_before_call";

    public const string ContextWindowExceededCode = "triage_budget_context_window_exceeded";

    public const string OrchestratorTurnLimitReachedCode = "triage_budget_orchestrator_turn_limit_reached";

    public const string OrchestratorRepromptLimitReachedCode = "triage_budget_orchestrator_reprompt_limit_reached";

    public const string MaxWorkersReachedCode = "triage_budget_max_workers_reached";

    public const string WorkerTurnLimitReachedCode = "triage_budget_worker_turn_limit_reached";

    public TriageBudgetExhaustedException(string errorCode, string message)
        : this(errorCode, message, innerException: null)
    {
    }

    /// <summary>
    /// Carries the validation failure the bounded run could not correct, so the original cause stays
    /// on the inner-exception chain that <see cref="TriageNonRetryableFailureClassifier"/> walks.
    /// </summary>
    public TriageBudgetExhaustedException(string errorCode, string message, Exception? innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
