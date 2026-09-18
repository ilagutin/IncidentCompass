namespace IncidentCompass.Application.Memory;

/// <summary>
/// What the relevance judge said about one candidate set, or that it was never asked.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Judged" /> is the whole branch. When it is true the judge is the admission authority:
/// <see cref="Admitted" /> is the complete set <c>memory_search</c> may return, everything else was
/// dropped for scoring below the floor, and <c>VectorOnlyFallback</c> does not apply because there is
/// no lexical gate left to leave anything empty.
/// </para>
/// <para>
/// When it is false nothing was judged and the pre-judge path runs unchanged.
/// <see cref="Limitation" /> is set only for the deployment shape of that, a host with no judge
/// installed, so the model reads why its result is not judged rather than assuming it was.
/// </para>
/// </remarks>
internal sealed record MemoryRelevanceJudgement(
    bool Judged,
    IReadOnlyList<MemoryRelevanceJudgedCandidate> Admitted,
    string? Limitation)
{
    /// <summary>The judge was not asked, or this host has none to ask.</summary>
    public static MemoryRelevanceJudgement NotJudged(string? limitation) => new(false, [], limitation);

    /// <summary>The judge answered, and these are the candidates its two thresholds admitted.</summary>
    public static MemoryRelevanceJudgement Admitting(IReadOnlyList<MemoryRelevanceJudgedCandidate> admitted) =>
        new(true, admitted, null);
}
