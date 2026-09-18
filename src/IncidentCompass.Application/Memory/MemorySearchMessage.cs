namespace IncidentCompass.Application.Memory;

/// <summary>
/// The top-level <c>message</c> strings <c>memory_search</c> reports, in one place because the shipped
/// memory role instructions quote them verbatim and a role that reads a sentence the tool no longer
/// emits reads nothing. A test pins the instruction file against <see cref="All" />.
/// </summary>
internal static class MemorySearchMessage
{
    /// <summary>At least one returned item is confirmed.</summary>
    public const string MatchesFound = "matches found";

    /// <summary>Nothing was returned.</summary>
    public const string NoMatches = "no matches";

    /// <summary>
    /// A judged set in which the relevance judge confirmed none of the items. Every item is banded
    /// <c>low</c>.
    /// </summary>
    public const string RelatedMatches = "related matches, none confirmed by the relevance judge";

    /// <summary>
    /// The same shape on a host where no relevance judge ran: the vector-only fallback returned the
    /// set, so nothing confirmed it either. Only the unjudged path can produce this.
    /// </summary>
    public const string VectorOnlyMatches = "vector-only matches, not lexically confirmed";

    /// <summary>Every message the role may be shown, in the order the instructions explain them.</summary>
    public static readonly IReadOnlyList<string> All =
        [MatchesFound, RelatedMatches, VectorOnlyMatches, NoMatches];
}
