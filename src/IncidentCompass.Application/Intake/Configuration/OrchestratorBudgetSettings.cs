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
    int? MaxAttemptDurationSeconds = null)
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
