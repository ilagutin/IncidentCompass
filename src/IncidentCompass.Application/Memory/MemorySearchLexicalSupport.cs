namespace IncidentCompass.Application.Memory;

/// <summary>
/// How much of one query a single candidate's text actually carries. An eligible word is a counted
/// query word the candidate could contain at all: a script-neutral word, or a word written in a script
/// the candidate's own counted words use. A word in a script the candidate never writes is left out of
/// the judgement instead of counted against it, because it can never occur there.
/// </summary>
internal sealed record MemorySearchLexicalSupport(
    int CountedQueryWords,
    int EligibleQueryWords,
    int MatchedQueryWords)
{
    /// <summary>The half rule, applied to the eligible words rather than to every counted word.</summary>
    public int RequiredMatches => Math.Max(1, (EligibleQueryWords + 1) / 2);

    /// <summary>
    /// A query with no counted word constrains nothing and keeps every candidate, exactly as it did
    /// before eligibility existed.
    /// </summary>
    public bool IsSupported => CountedQueryWords == 0 ||
        (EligibleQueryWords > 0 && MatchedQueryWords >= RequiredMatches);

    /// <summary>The ranking boost, over the same denominator the gate judges on.</summary>
    public double Coverage => EligibleQueryWords == 0
        ? 0
        : (double)MatchedQueryWords / EligibleQueryWords;

    /// <summary>Every counted query word was eligible, and every one of them occurs in the text.</summary>
    public bool IsFullyCovered => CountedQueryWords > 0 &&
        EligibleQueryWords == CountedQueryWords &&
        MatchedQueryWords == CountedQueryWords;
}
