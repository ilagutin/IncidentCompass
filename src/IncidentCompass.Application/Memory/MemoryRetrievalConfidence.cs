namespace IncidentCompass.Application.Memory;

/// <summary>
/// The band reported as <c>retrievalConfidence</c>. It describes lexical support, not the vector score:
/// on the shipped multilingual embedding model relevant and unrelated chunks score alike, so a score
/// threshold carried no information. The numeric <c>score</c> field is reported unchanged beside it.
/// </summary>
internal static class MemoryRetrievalConfidence
{
    /// <summary>Lexically supported, no query word excluded for script, and all of them in the text.</summary>
    public const string High = "high";

    /// <summary>Lexically supported on partial coverage, or with words excluded for script.</summary>
    public const string Medium = "medium";

    /// <summary>Returned by the vector-only fallback, so not lexically confirmed at all.</summary>
    public const string Low = "low";

    public static string Band(MemorySearchLexicalSupport support, bool vectorOnly)
    {
        if (vectorOnly)
        {
            return Low;
        }

        return support.IsFullyCovered ? High : Medium;
    }
}
