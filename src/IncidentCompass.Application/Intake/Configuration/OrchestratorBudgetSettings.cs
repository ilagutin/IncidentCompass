namespace IncidentCompass.Application.Intake.Configuration;

/// <summary>
/// The per-attempt orchestrator budget. <see cref="MaxTurns"/> bounds the orchestrator work turns;
/// the loop adds <see cref="MaxReprompts"/> on top, so a correction turn never costs a work turn.
/// </summary>
/// <remarks>
/// The attempt duration ceiling has two spellings. <see cref="MaxAttemptDurationSeconds"/> is the
/// current one: absent means <see cref="DefaultMaxAttemptDurationSeconds"/>, and <c>0</c> disables
/// the ceiling. <see cref="MaxWallClockSeconds"/> is the deprecated one a stored snapshot or an older
/// configuration may still carry; when it is the only one present, its explicit value is the
/// ceiling. The rule lives in <see cref="ResolveAttemptDurationSeconds"/> so every reader of the
/// budget resolves the same limit.
/// </remarks>
public sealed record OrchestratorBudgetSettings(
    int MaxWorkers,
    int MaxTokens,
    int? MaxWallClockSeconds = null,
    int MaxReprompts = 1,
    int MaxTurns = OrchestratorBudgetSettings.DefaultMaxTurns,
    int? MaxAttemptDurationSeconds = null,
    int MaxEquivalentCalls = OrchestratorBudgetSettings.DefaultMaxEquivalentCalls,
    int MaxTurnsWithoutProgress = OrchestratorBudgetSettings.DefaultMaxTurnsWithoutProgress,
    int MaxRecoveries = OrchestratorBudgetSettings.DefaultMaxRecoveries)
{
    /// <summary>
    /// The work-turn allowance used when a configuration does not set one. It is the constant the
    /// loop used before the limit became configurable, so the effective bound is unchanged.
    /// </summary>
    public const int DefaultMaxTurns = 16;

    /// <summary>
    /// Below one work turn the orchestrator could never reach <c>publish_report</c>, so the loop would
    /// dead-letter every job on the turn limit.
    /// </summary>
    public const int MinimumMaxTurns = 1;

    /// <summary>
    /// Every turn is a billed model call. The token and attempt-duration budgets already bound an
    /// attempt, but this caps the worst-case call count so a typo cannot turn a bounded run into a
    /// long one.
    /// </summary>
    public const int MaximumMaxTurns = 64;

    /// <summary>
    /// Unproductive equivalent calls allowed per call fingerprint per attempt when a configuration
    /// does not set one. A repeat is unproductive when its result is identical to the previous result
    /// of the same call; a repeat whose result changed resets the count, so rechecking data that moves
    /// is never refused.
    /// </summary>
    public const int DefaultMaxEquivalentCalls = 2;

    /// <summary>Below one, the first identical recheck of unchanged data would already be refused.</summary>
    public const int MinimumMaxEquivalentCalls = 1;

    /// <summary>Past ten identical results in a row a repeat is a loop, not a recheck.</summary>
    public const int MaximumMaxEquivalentCalls = 10;

    /// <summary>
    /// Consecutive orchestrator turns without new evidence or a changed candidate classification
    /// allowed when a configuration does not set one. Time is not an input, so a slow model that
    /// keeps producing never reaches it.
    /// </summary>
    public const int DefaultMaxTurnsWithoutProgress = 4;

    /// <summary>A single turn without progress is ordinary: a turn that corrects a malformed call adds nothing.</summary>
    public const int MinimumMaxTurnsWithoutProgress = 2;

    /// <summary>Half the largest work-turn allowance; beyond it the turn limit is the only bound left.</summary>
    public const int MaximumMaxTurnsWithoutProgress = 32;

    /// <summary>
    /// Recovery calls allowed per attempt when a configuration does not set one. Each is a single
    /// tool-less diagnostic call made when the investigation stops making progress; a no-progress
    /// detection with none left ends the attempt with a backend-authored report.
    /// </summary>
    public const int DefaultMaxRecoveries = 1;

    /// <summary>Zero disables recovery: the first no-progress detection ends the attempt.</summary>
    public const int MinimumMaxRecoveries = 0;

    /// <summary>Each recovery is a billed call that has already been shown not to unstick the run.</summary>
    public const int MaximumMaxRecoveries = 3;

    public const string MaxRecoveriesSettingName = "Orchestrator.Budget.MaxRecoveries";

    public const string MaxEquivalentCallsSettingName = "Orchestrator.Budget.MaxEquivalentCalls";

    public const string MaxTurnsWithoutProgressSettingName = "Orchestrator.Budget.MaxTurnsWithoutProgress";

    /// <summary>
    /// The attempt ceiling when neither key is configured: four hours. It is a safety net against a
    /// run that never ends, not the control that decides whether a slow model is making progress.
    /// </summary>
    public const int DefaultMaxAttemptDurationSeconds = 14_400;

    /// <summary>The value of <see cref="MaxAttemptDurationSeconds"/> that disables the ceiling.</summary>
    public const int DisabledMaxAttemptDurationSeconds = 0;

    /// <summary>The largest configurable ceiling: seven days.</summary>
    public const int MaximumMaxAttemptDurationSeconds = 604_800;

    public const string MaxAttemptDurationSecondsSettingName = "Orchestrator.Budget.MaxAttemptDurationSeconds";

    public const string MaxWallClockSecondsSettingName = "Orchestrator.Budget.MaxWallClockSeconds";

    /// <summary>
    /// Whether the ceiling comes from the deprecated <see cref="MaxWallClockSeconds"/> key.
    /// </summary>
    public bool UsesDeprecatedMaxWallClockSeconds() =>
        MaxAttemptDurationSeconds is null && MaxWallClockSeconds is not null;

    /// <summary>
    /// The configured attempt ceiling in seconds, where <c>0</c> means no ceiling.
    /// </summary>
    public int ResolveAttemptDurationSeconds() =>
        MaxAttemptDurationSeconds ?? MaxWallClockSeconds ?? DefaultMaxAttemptDurationSeconds;

    /// <summary>
    /// The attempt ceiling, or <see langword="null"/> when it is disabled.
    /// </summary>
    public TimeSpan? ResolveAttemptDurationLimit()
    {
        var seconds = ResolveAttemptDurationSeconds();
        return seconds == DisabledMaxAttemptDurationSeconds ? null : TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// The setting that supplied the ceiling, so messages name the key the operator actually set.
    /// </summary>
    public string ResolveAttemptDurationSettingName() =>
        UsesDeprecatedMaxWallClockSeconds() ? MaxWallClockSecondsSettingName : MaxAttemptDurationSecondsSettingName;
}
