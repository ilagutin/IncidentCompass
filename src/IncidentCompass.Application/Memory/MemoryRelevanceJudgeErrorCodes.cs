namespace IncidentCompass.Application.Memory;

/// <summary>
/// The stable codes of a relevance judge this host cannot serve by configuration, mirroring
/// <see cref="MemoryEmbeddingModelErrorCodes" />. Both are operator-fixable states, not provider
/// outages: the request is well formed and stays refused until an operator installs the configured
/// judge or corrects the configuration and restarts the host.
/// </summary>
internal static class MemoryRelevanceJudgeErrorCodes
{
    /// <summary>No usable local judge is installed: none was installed, or it no longer verifies.</summary>
    public const string Unavailable = "memory_relevance_judge_unavailable";

    /// <summary>A local judge is installed, but it is not the judge the configuration names.</summary>
    public const string Mismatch = "memory_relevance_judge_mismatch";

    /// <summary>
    /// The adapter returned a score that is not a finite number. It is a broken contract rather than
    /// an operator-fixable state, and it is named so that an operator sees why a call was refused
    /// instead of seeing the score silently admitted: a NaN compares false against both thresholds,
    /// so an unchecked one would be admitted as an unconfirmed match.
    /// </summary>
    public const string ScoreNotFinite = "memory_relevance_judge_score_not_finite";

    /// <summary>
    /// The adapter returned a different number of scores than the candidates it was given, so no
    /// score can be attributed to a candidate. Also a broken contract, and named for the same reason.
    /// </summary>
    public const string ScoreCountMismatch = "memory_relevance_judge_score_count_mismatch";
}
