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
    /// A set none of whose items was confirmed against the fault, other than a vector-only fallback
    /// set. Every item is banded <c>low</c>. On a judged call that means the judge confirmed none of
    /// them; on an unjudged call nothing is ever confirmed, so every lexically admitted set reports this
    /// sentence, which is literally true there because no judge ran.
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
