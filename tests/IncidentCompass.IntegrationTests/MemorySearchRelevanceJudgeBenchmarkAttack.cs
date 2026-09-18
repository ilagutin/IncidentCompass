using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Memory;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// The attack the fault-query band exists to stop: a model that re-queries <c>memory_search</c> with a
/// document's own text, so the document answers the query perfectly whatever the fault was.
/// </summary>
/// <remarks>
/// <para>
/// Every off-topic and hard-negative query is replaced by the full text of the candidate chunk the judge
/// scored highest for it in the capture pass, which is the document such a model is most likely to have
/// seen, and is run through the real <c>memory_search</c> with its own real trigger signal. Nothing the
/// corpus labels describes those faults, so a confirmed item here is a confirmation the model bought by
/// choosing its words.
/// </para>
/// <para>
/// The leg runs twice. The first run is the product. The second reproduces the rule the product
/// replaced, under which the band was decided against the model's own query: it hands the tool a signal
/// whose fault query is exactly that query, so the tool confirms against it. The judge answers the same
/// (query, chunk) pair with the same score under either rule, and lexical coverage of the same query is
/// the same, so this is the old rule reproduced rather than approximated. It exists so that a zero in the
/// first run is shown to be the fix and not an attack that would have confirmed nothing anyway. It is a
/// benchmark strategy, never a product setting.
/// </para>
/// </remarks>
internal static class MemorySearchRelevanceJudgeBenchmarkAttack
{
    public const string FaultQueryLeg = "attack-confirmed-against-fault-query";
    public const string ModelQueryLeg = "attack-confirmed-against-model-query";
    public const string FaultQueryTarget = "fault-query";
    public const string ModelQueryTarget = "model-query";

    public static async Task<MemorySearchRelevanceJudgeBenchmarkAttackLeg> RunAsync(
        bool confirmAgainstModelQuery,
        (string Name, MemoryRetrievalBenchmarkCorpus Corpus)[] groups,
        IReadOnlyList<MemorySearchRelevanceJudgeBenchmarkPair> pairs,
        IImmediateAgentTool memorySearchTool,
        TriageConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var languages = new List<MemorySearchRelevanceJudgeBenchmarkAttackLanguage>(groups.Length);
        foreach (var (name, corpus) in groups)
        {
            var (attackCorpus, attacked, skipped) = BuildAttackCorpus(name, corpus, pairs);
            IReadOnlyList<MemoryRetrievalQueryResult> results = attackCorpus.Queries.Count == 0
                ? []
                : await new ProductionMemoryRetrievalStrategy(
                        memorySearchTool,
                        configuration,
                        confirmAgainstModelQuery
                            ? static (attackedCorpus, query) =>
                                MemoryRetrievalBenchmarkTriggerSignal.ConfirmingAgainstTheModelQuery(
                                    attackedCorpus.TenantId, query.Text)
                            : null)
                    .ExecuteAsync(attackCorpus, cancellationToken);
            var byQuery = results.ToDictionary(static result => result.QueryId, StringComparer.Ordinal);
            var queries = attacked
                .Select(entry =>
                {
                    var matches = byQuery[entry.QueryId].Matches.Take(corpus.TopK).ToArray();
                    return new MemorySearchRelevanceJudgeBenchmarkAttackQuery(
                        entry.QueryId,
                        entry.Category,
                        entry.ChunkId.ToString(),
                        matches.Length,
                        matches.Count(static match => MemoryRetrievalConfidence.ConfirmsMatch(match.RetrievalConfidence)));
                })
                .ToArray();
            languages.Add(new MemorySearchRelevanceJudgeBenchmarkAttackLanguage(
                name,
                queries.Length,
                skipped,
                queries.Sum(static query => query.ReturnedItemCount),
                queries.Sum(static query => query.ConfirmedItemCount),
                queries.Count(static query => query.ConfirmedItemCount > 0),
                queries));
        }

        return new MemorySearchRelevanceJudgeBenchmarkAttackLeg(
            confirmAgainstModelQuery ? ModelQueryLeg : FaultQueryLeg,
            confirmAgainstModelQuery ? ModelQueryTarget : FaultQueryTarget,
            languages);
    }

    /// <summary>
    /// The group's off-topic and hard-negative queries, each with its text replaced by the trimmed text
    /// of its highest-scoring candidate and its signal left exactly as the fixture gives it. The text is
    /// trimmed because <c>memory_search</c> trims every query it accepts, and the chunk must be active,
    /// because the vector search offers nothing else.
    /// </summary>
    private static (MemoryRetrievalBenchmarkCorpus Corpus, List<(string QueryId, string Category, Guid ChunkId)> Attacked, int Skipped)
        BuildAttackCorpus(
            string language,
            MemoryRetrievalBenchmarkCorpus corpus,
            IReadOnlyList<MemorySearchRelevanceJudgeBenchmarkPair> pairs)
    {
        var activeChunkTexts = corpus.Items
            .Where(static item => item.IsActive)
            .SelectMany(static item => item.Chunks)
            .ToDictionary(static chunk => chunk.Id, static chunk => chunk.Text);
        var attackedQueries = new List<MemoryRetrievalBenchmarkQuery>();
        var attacked = new List<(string QueryId, string Category, Guid ChunkId)>();
        var skipped = 0;
        foreach (var query in corpus.Queries)
        {
            var category = MemoryRetrievalQueryCategory.Of(query);
            if (category is not (MemoryRetrievalQueryCategory.OffTopic or MemoryRetrievalQueryCategory.HardNegative))
            {
                continue;
            }

            var top = pairs
                .Where(pair => pair.Language == language &&
                    string.Equals(pair.QueryId, query.Id, StringComparison.Ordinal) &&
                    pair.Score is not null)
                .OrderByDescending(static pair => pair.Score!.Value)
                .ThenBy(static pair => pair.ChunkId)
                .FirstOrDefault();
            if (top is null)
            {
                skipped++;
                continue;
            }

            if (!activeChunkTexts.TryGetValue(top.ChunkId, out var text))
            {
                throw new InvalidOperationException(
                    "Attack query '" + query.Id + "' was offered chunk " + top.ChunkId +
                    ", which is not an active chunk of the corpus.");
            }

            attackedQueries.Add(query with { Text = text.Trim() });
            attacked.Add((query.Id, category, top.ChunkId));
        }

        return (corpus with { Queries = attackedQueries }, attacked, skipped);
    }
}
