using IncidentCompass.Application.Intake.Configuration;

namespace IncidentCompass.Application.Memory;

internal static class MemorySearchReranker
{
    private const double CurrentDocumentationBoost = 10.0;
    private const double StaleDocumentationBoost = 5.0;
    private const double UnversionedDocumentationBoost = 2.5;
    private const double ComponentBoost = 0.25;
    private const double EvidenceKindBoost = 0.2;

    private static readonly Dictionary<string, string[]> EvidenceKindAliases =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["runbook"] = ["runbook", "playbook"],
            ["known_incident"] = ["known incident"],
            ["operational_note"] = ["operational note"],
            ["release_note"] = ["release note"],
            ["postmortem"] = ["postmortem"]
        };

    /// <summary>
    /// Ranks the candidates the repository already bounded by <c>MinScore</c>.
    /// <para>
    /// When <paramref name="judgement" /> judged this call, the relevance judge is the admission
    /// authority: its admitted set is ranked and nothing else is, so neither the lexical gate nor
    /// <paramref name="vectorOnlyFallback" /> can add or remove a candidate. Lexical support still
    /// boosts the ordering.
    /// </para>
    /// <para>
    /// When it did not, the pre-judge path runs unchanged: lexically supported candidates are returned
    /// whenever there is at least one; only when there is none does
    /// <paramref name="vectorOnlyFallback" /> decide whether the whole candidate set is returned
    /// unconfirmed instead of nothing.
    /// </para>
    /// <para>
    /// Every match comes back <c>low</c> on both paths. Ranking works on the role's query, and a band
    /// decided against that query is one the role could raise by rewording it, so confirmation is left
    /// entirely to <see cref="MemoryFaultConfirmation" />, which decides it against the fault query.
    /// </para>
    /// </summary>
    public static IReadOnlyList<MemorySearchRankedMatch> Rank(
        string query,
        TriageConfiguration configuration,
        string faultServiceName,
        IReadOnlyList<MemorySearchMatch> candidates,
        int topK,
        MemorySearchVectorOnlyFallback vectorOnlyFallback,
        MemoryRelevanceJudgement? judgement = null)
    {
        if (judgement is { Judged: true })
        {
            return Order(
                query,
                configuration,
                faultServiceName,
                judgement.Admitted.Select(admitted => (
                    admitted.Match,
                    Support: MemorySearchLexicalFilter.Evaluate(query, admitted.Match.Text),
                    JudgeScore: (double?)admitted.Score)),
                topK,
                vectorOnly: false);
        }

        var evaluated = candidates
            .Select(match => (
                Match: match,
                Support: MemorySearchLexicalFilter.Evaluate(query, match.Text),
                JudgeScore: (double?)null))
            .ToArray();
        var supported = evaluated.Where(static candidate => candidate.Support.IsSupported).ToArray();
        if (supported.Length > 0)
        {
            return Order(query, configuration, faultServiceName, supported, topK, vectorOnly: false);
        }

        return AllowsVectorOnly(query, candidates, vectorOnlyFallback)
            ? Order(query, configuration, faultServiceName, evaluated, topK, vectorOnly: true)
            : [];
    }

    internal static MemorySearchRankingFeatures CreateFeatures(
        string query,
        TriageConfiguration configuration,
        string faultServiceName,
        MemorySearchMatch match) =>
        CreateFeatures(
            query,
            configuration,
            faultServiceName,
            match,
            MemorySearchLexicalFilter.Evaluate(query, match.Text),
            judgeScore: null);

    /// <summary>
    /// The ranking path has already judged the candidate, so it passes that judgement in rather than
    /// letting the lexical boost tokenize the same chunk text a second time.
    /// <para>
    /// <paramref name="judgeScore" /> substitutes for the vector score as the base term when a
    /// relevance judge scored this call. The boosts keep the values they have always had, and were
    /// deliberately not rescaled, although the two scales genuinely differ: a vector score is a
    /// cosine similarity in roughly 0 to 1, while a judge score is that model's own logit and spans
    /// several units either side of zero. The documentation boost is a product rule rather than a
    /// similarity correction - a current runbook should beat a stale one whatever either scored - and
    /// it keeps outranking the base term on both scales, which is the behaviour that was already
    /// here. The lexical, component and evidence-kind boosts are each below one unit, so at the
    /// judge's scale they become tie-breakers instead of the near-peers they are at the vector scale.
    /// That demotion is the point: on a judged call the judge has already answered the question those
    /// three were approximating.
    /// </para>
    /// </summary>
    private static MemorySearchRankingFeatures CreateFeatures(
        string query,
        TriageConfiguration configuration,
        string faultServiceName,
        MemorySearchMatch match,
        MemorySearchLexicalSupport support,
        double? judgeScore)
    {
        var documentation = MemoryDocumentationStatusEvaluator.Assess(
            configuration,
            faultServiceName,
            match);
        var documentationBoost = documentation.Status switch
        {
            MemoryDocumentationStatus.Current => CurrentDocumentationBoost,
            MemoryDocumentationStatus.Stale => StaleDocumentationBoost,
            MemoryDocumentationStatus.Unversioned => UnversionedDocumentationBoost,
            _ => 0
        };
        var lexicalBoost = support.Coverage;
        var componentBoost = !string.IsNullOrWhiteSpace(match.Component) &&
            MemorySearchLexicalFilter.ContainsNormalizedTokenOrPhrase(query, match.Component)
            ? ComponentBoost
            : 0;
        var evidenceKindBoost = HasEvidenceKindAlias(query, match.Kind)
            ? EvidenceKindBoost
            : 0;
        var baseScore = judgeScore ?? match.Score;

        return new MemorySearchRankingFeatures(
            baseScore + documentationBoost + lexicalBoost + componentBoost + evidenceKindBoost,
            match.Score,
            documentation.Status,
            documentationBoost,
            lexicalBoost,
            componentBoost,
            evidenceKindBoost);
    }

    /// <summary>
    /// Orders one admitted set, unconfirmed. The vector score stays the secondary sort on both paths,
    /// so two candidates the base term ties are separated exactly the way they always were.
    /// </summary>
    private static MemorySearchRankedMatch[] Order(
        string query,
        TriageConfiguration configuration,
        string faultServiceName,
        IEnumerable<(MemorySearchMatch Match, MemorySearchLexicalSupport Support, double? JudgeScore)> candidates,
        int topK,
        bool vectorOnly)
    {
        return candidates
            .Select(candidate => (
                candidate.Match,
                candidate.JudgeScore,
                Features: CreateFeatures(
                    query, configuration, faultServiceName, candidate.Match, candidate.Support, candidate.JudgeScore)))
            .OrderByDescending(static ranked => ranked.Features.CombinedScore)
            .ThenByDescending(static ranked => ranked.Features.VectorScore)
            .ThenBy(static ranked => ranked.Match.ChunkId)
            .Take(topK)
            .Select(ranked => new MemorySearchRankedMatch(
                ranked.Match,
                MemoryRetrievalConfidence.Low,
                vectorOnly,
                ranked.JudgeScore))
            .ToArray();
    }

    private static bool AllowsVectorOnly(
        string query,
        IReadOnlyList<MemorySearchMatch> candidates,
        MemorySearchVectorOnlyFallback vectorOnlyFallback) => vectorOnlyFallback switch
        {
            MemorySearchVectorOnlyFallback.Always => true,
            MemorySearchVectorOnlyFallback.ForeignScript => HasScriptNoCandidateWrites(query, candidates),
            _ => false
        };

    /// <summary>
    /// True when the query carries a counted word in a writing system none of the candidates use, which
    /// is the one case where lexical absence says nothing about relevance. When no candidate has a
    /// counted word at all the comparison is vacuous - every script is missing from an empty set - so
    /// it is not treated as a foreign script.
    /// </summary>
    private static bool HasScriptNoCandidateWrites(string query, IReadOnlyList<MemorySearchMatch> candidates)
    {
        var candidateScripts = new HashSet<WritingScript>();
        foreach (var candidate in candidates)
        {
            candidateScripts.UnionWith(MemorySearchLexicalFilter.CountedScripts(candidate.Text));
        }

        if (candidateScripts.Count == 0)
        {
            return false;
        }

        return MemorySearchLexicalFilter.CountedScripts(query)
            .Any(script => script != WritingScript.Neutral && !candidateScripts.Contains(script));
    }

    private static bool HasEvidenceKindAlias(string query, string kind)
    {
        return EvidenceKindAliases.TryGetValue(kind, out var aliases) &&
            aliases.Any(alias => MemorySearchLexicalFilter.ContainsNormalizedTokenOrPhrase(query, alias));
    }
}
