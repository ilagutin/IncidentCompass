namespace IncidentCompass.Application.Memory;

/// <summary>
/// The two adapter states in which this host is not trying to run a relevance judge at all, and which
/// are therefore the only states <c>memory_search</c> may answer without one.
/// </summary>
/// <remarks>
/// <para>
/// These are provider error codes, not normalized ones, and they live here rather than with the
/// adapter that raises them because the Application has to recognize exactly these two and nothing
/// else. The normalized code stays a single <see cref="MemoryRelevanceJudgeErrorCodes.Unavailable" />:
/// widening that instead would have made the decision invisible, because a digest mismatch, a missing
/// model file, a failed fetch, an oversized download, an install timeout, an invalid manifest and an
/// unreadable model store all normalize to it as well. A host whose judge does not verify is broken,
/// not judge-less, and silently answering such a call from the lexical gate, while telling the model
/// no judge is installed, is precisely the degradation the judge exists to remove.
/// </para>
/// <para>
/// The adapter defines its own constants from these, so there is one spelling of each string and an
/// adapter cannot drift out of the set the Application matches on.
/// </para>
/// </remarks>
internal static class MemoryRelevanceJudgeAbsence
{
    /// <summary>No judge model directory is configured, so this host runs no judge.</summary>
    public const string NotConfigured = "relevance_judge_not_configured";

    /// <summary>A judge is configured, and nothing is installed in its directory yet.</summary>
    public const string NotInstalled = "relevance_judge_model_not_installed";

    /// <summary>
    /// True only for the two codes above. Every other provider code, whatever it normalizes to,
    /// describes a judge this host meant to run and must propagate.
    /// </summary>
    public static bool IsAbsent(string? providerErrorCode) =>
        string.Equals(providerErrorCode, NotConfigured, StringComparison.Ordinal) ||
        string.Equals(providerErrorCode, NotInstalled, StringComparison.Ordinal);
}
