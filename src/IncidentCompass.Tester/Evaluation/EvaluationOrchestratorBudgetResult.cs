namespace IncidentCompass.Tester.Evaluation;

/// <summary>
/// The orchestrator budget an evaluation ran under. <see cref="MaxAttemptDurationSeconds"/> is the
/// resolved attempt ceiling, where <c>0</c> means no ceiling, and <see cref="AttemptDurationSetting"/>
/// names the key that supplied it: <c>MaxAttemptDurationSeconds</c>, the deprecated
/// <c>MaxWallClockSeconds</c>, or <c>default</c> when neither is set.
/// </summary>
/// <remarks>
/// <see cref="MaxWallClockSeconds"/> stays in the result, as the deprecated key's configured value
/// or <see langword="null"/>, so the fields a result already carried keep their meaning; the resolved
/// ceiling is added beside it rather than replacing it.
/// </remarks>
internal sealed record EvaluationOrchestratorBudgetResult(
    int MaxWorkers,
    int MaxTokens,
    int? MaxWallClockSeconds,
    int? MaxReprompts,
    int MaxAttemptDurationSeconds,
    string AttemptDurationSetting)
{
    public const string CurrentAttemptDurationSetting = "MaxAttemptDurationSeconds";

    public const string DeprecatedAttemptDurationSetting = "MaxWallClockSeconds";

    public const string DefaultAttemptDurationSetting = "default";

    /// <summary>The ceiling the backend applies when the configuration sets neither key.</summary>
    public const int DefaultMaxAttemptDurationSeconds = 14_400;
}
