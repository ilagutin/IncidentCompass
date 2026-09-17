namespace IncidentCompass.IntegrationTests;

/// <summary>
/// What a benchmark query is for. The three categories separate the two ways a retrieval answer can be
/// wrong: an <see cref="OffTopic" /> query is about something the corpus does not cover at all, while a
/// <see cref="HardNegative" /> query names a retired procedure closely, so the corpus still holds active
/// siblings about the same subsystem and a vector score alone cannot tell the two apart.
/// </summary>
public static class MemoryRetrievalQueryCategory
{
    /// <summary>The corpus answers the query, and the query labels the item and chunk that answer it.</summary>
    public const string Positive = "positive";

    /// <summary>The corpus does not cover the query at all. Both relevance arrays are empty.</summary>
    public const string OffTopic = "off_topic";

    /// <summary>
    /// The only exact answer is a retired item, so nothing active answers the query. Both relevance
    /// arrays are empty and active siblings about the same subsystem stay in the corpus.
    /// </summary>
    public const string HardNegative = "hard_negative";

    public static readonly IReadOnlyList<string> All = [Positive, OffTopic, HardNegative];

    public static bool IsKnown(string? category) =>
        category is not null && All.Contains(category, StringComparer.Ordinal);

    /// <summary>
    /// The query's own category, or, for a version 1 corpus that predates the field, the category its
    /// relevance arrays imply. A version 1 corpus never expresses <see cref="HardNegative" />.
    /// </summary>
    public static string Of(MemoryRetrievalBenchmarkQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.Category ?? (query.RelevantChunkIds.Count > 0 ? Positive : OffTopic);
    }
}
