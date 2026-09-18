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
}
