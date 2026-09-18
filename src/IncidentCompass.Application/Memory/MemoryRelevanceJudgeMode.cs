namespace IncidentCompass.Application.Memory;

/// <summary>
/// Whether <c>memory_search</c> asks the relevance judge about a query at all. Configured as
/// <c>Tools.memory_search.RelevanceJudge</c>.
/// </summary>
/// <remarks>
/// <see cref="On" /> does not mean "require". It means "ask, when this host has a judge". A host that
/// names no judge model directory runs none, and on such a host the tool keeps the behaviour it had
/// before the judge existed and says so in its output. A judge that is installed and then fails is a
/// different thing and is never absorbed: that failure propagates with its own code.
/// </remarks>
internal enum MemoryRelevanceJudgeMode
{
    /// <summary>Never ask. The lexical gate and <c>VectorOnlyFallback</c> decide admission alone.</summary>
    Off,

    /// <summary>Ask about every query, on a host that has a judge.</summary>
    On
}
