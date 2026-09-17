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
    /// Ranks the candidates the repository already bounded by <c>MinScore</c>. Lexically supported
    /// candidates are returned whenever there is at least one; only when there is none does
    /// <paramref name="vectorOnlyFallback" /> decide whether the whole candidate set is returned
    /// unconfirmed instead of nothing.
    /// </summary>
    public static IReadOnlyList<MemorySearchRankedMatch> Rank(
        string query,
        TriageConfiguration configuration,
        string faultServiceName,
        IReadOnlyList<MemorySearchMatch> candidates,
        int topK,
        MemorySearchVectorOnlyFallback vectorOnlyFallback)
    {
        var evaluated = candidates
            .Select(match => (Match: match, Support: MemorySearchLexicalFilter.Evaluate(query, match.Text)))
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
            MemorySearchLexicalFilter.Evaluate(query, match.Text));

    /// <summary>
    /// The ranking path has already judged the candidate, so it passes that judgement in rather than
    /// letting the lexical boost tokenize the same chunk text a second time.
    /// </summary>
    private static MemorySearchRankingFeatures CreateFeatures(
        string query,
        TriageConfiguration configuration,
        string faultServiceName,
        MemorySearchMatch match,
        MemorySearchLexicalSupport support)
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

        return new MemorySearchRankingFeatures(
            match.Score + documentationBoost + lexicalBoost + componentBoost + evidenceKindBoost,
            match.Score,
            documentation.Status,
            documentationBoost,
            lexicalBoost,
            componentBoost,
            evidenceKindBoost);
    }

    private static MemorySearchRankedMatch[] Order(
        string query,
        TriageConfiguration configuration,
        string faultServiceName,
        IReadOnlyList<(MemorySearchMatch Match, MemorySearchLexicalSupport Support)> candidates,
        int topK,
        bool vectorOnly)
    {
        return candidates
            .Select(candidate => (
                candidate.Match,
                candidate.Support,
                Features: CreateFeatures(
                    query, configuration, faultServiceName, candidate.Match, candidate.Support)))
            .OrderByDescending(static ranked => ranked.Features.CombinedScore)
            .ThenByDescending(static ranked => ranked.Features.VectorScore)
            .ThenBy(static ranked => ranked.Match.ChunkId)
            .Take(topK)
            .Select(ranked => new MemorySearchRankedMatch(
                ranked.Match,
                MemoryRetrievalConfidence.Band(ranked.Support, vectorOnly),
                vectorOnly))
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
