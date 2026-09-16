namespace IncidentCompass.IntegrationTests;

public sealed class MemoryRetrievalBenchmarkContractTests
{
    [Fact]
    public void LoadAndEvaluate_ProvideStableSecondConsumerContract()
    {
        var corpus = MemoryRetrievalBenchmarkCorpus.Load(FindRepoRoot());
        var chunkOwners = corpus.Items
            .SelectMany(item => item.Chunks.Select(chunk => (ChunkId: chunk.Id, ItemId: item.Id)))
            .ToDictionary(static pair => pair.ChunkId, static pair => pair.ItemId);
        var perfectResults = corpus.Queries.Select(query => new MemoryRetrievalQueryResult(
            query.Id,
            query.RelevantChunkIds.Select(chunkId =>
                new MemoryRetrievalMatch(chunkOwners[chunkId], chunkId)).ToArray())).ToArray();

        var evaluation = MemoryRetrievalMetrics.Evaluate(corpus, perfectResults);

        Assert.Equal(1, corpus.SchemaVersion);
        Assert.Equal("memory-retrieval-corpus-v1", corpus.CorpusVersion);
        Assert.Equal(5, corpus.TopK);
        Assert.Equal(16, corpus.EmbeddingDimensions);
        Assert.Contains(corpus.Items, static item =>
            item.Source == "samples/runbooks/checkout-timeout.md");
        Assert.Contains(corpus.Items, static item =>
            item.Source == "samples/incidents/checkout-timeout-known-incident.md");
        Assert.Contains(corpus.Items, static item => !item.IsActive);
        Assert.Contains(corpus.Queries, static query => query.RelevantChunkIds.Count == 0);
        Assert.Equal(1, evaluation.Metrics.ChunkMacroRecallAt5);
        Assert.Equal(1, evaluation.Metrics.ChunkMicroRecallAt5);
        Assert.Equal(1, evaluation.Metrics.ItemMacroRecallAt5);
        Assert.Equal(1, evaluation.Metrics.ItemMicroRecallAt5);
        Assert.Equal(1, evaluation.Metrics.MeanFirstRelevantChunkRank);
        Assert.Equal(1, evaluation.Metrics.NoMatchPrecision);
        Assert.Equal(0, evaluation.Metrics.NoMatchFalsePositiveCount);
    }

    [Fact]
    public void MultilingualQueries_RenderEveryCorpusQueryOnceInPolishAndRussian()
    {
        var repoRoot = FindRepoRoot();
        var corpus = MemoryRetrievalBenchmarkCorpus.Load(repoRoot);
        var multilingual = MemoryRetrievalMultilingualQueries.Load(repoRoot);
        var sourceIds = corpus.Queries.Select(static query => query.Id).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(1, multilingual.SchemaVersion);
        Assert.Equal("memory-retrieval-multilingual-queries-v1", multilingual.Version);
        Assert.Equal(corpus.CorpusVersion, multilingual.CorpusVersion);
        Assert.All(multilingual.Queries, entry =>
        {
            Assert.Contains(entry.SourceQueryId, sourceIds);
            Assert.Equal(entry.SourceQueryId + "-" + entry.Language, entry.Id);
            Assert.False(string.IsNullOrWhiteSpace(entry.Text));
        });
        Assert.Equal(
            multilingual.Queries.Count,
            multilingual.Queries.Select(static entry => entry.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            new[] { MemoryRetrievalMultilingualQueries.Polish, MemoryRetrievalMultilingualQueries.Russian },
            multilingual.Queries.Select(static entry => entry.Language).Distinct().Order(StringComparer.Ordinal));
        foreach (var language in new[] { MemoryRetrievalMultilingualQueries.Polish, MemoryRetrievalMultilingualQueries.Russian })
        {
            var covered = multilingual.Queries
                .Where(entry => entry.Language == language)
                .Select(static entry => entry.SourceQueryId)
                .ToArray();
            Assert.Equal(sourceIds.Count, covered.Length);
            Assert.True(sourceIds.SetEquals(covered), language + " does not cover every corpus query exactly once.");
        }

        var pooled = multilingual.ToCorpus(
            corpus,
            MemoryRetrievalMultilingualQueries.Polish,
            MemoryRetrievalMultilingualQueries.Russian);
        Assert.Equal(corpus.Queries.Count * 2, pooled.Queries.Count);
        Assert.All(pooled.Queries, query =>
        {
            var source = corpus.Queries.Single(candidate => query.Id.StartsWith(candidate.Id + "-", StringComparison.Ordinal) &&
                query.Id.Length == candidate.Id.Length + 3);
            Assert.Equal(source.RelevantChunkIds, query.RelevantChunkIds);
            Assert.Equal(source.RelevantItemIds, query.RelevantItemIds);
            Assert.Equal(source.ServiceName, query.ServiceName);
        });
    }

    [Fact]
    public void VersionedBaseline_SeparatesDeterministicOutcomesFromLatencyDiagnostics()
    {
        var repoRoot = FindRepoRoot();
        var corpus = MemoryRetrievalBenchmarkCorpus.Load(repoRoot);
        var baseline = MemoryRetrievalBaselineRecord.Load(repoRoot);

        Assert.Equal(1, baseline.SchemaVersion);
        Assert.Equal(corpus.CorpusVersion, baseline.CorpusVersion);
        Assert.Equal(corpus.TopK, baseline.TopK);
        Assert.Equal("production-v0.2.0-vector-topk-then-lexical", baseline.Strategy);
        Assert.Equal(corpus.Queries.Count, baseline.Deterministic.Queries.Count);
        Assert.Equal(5, baseline.Diagnostics.WarmupCallsPerStrategy);
        Assert.Equal(30, baseline.Diagnostics.MeasuredCallsPerStrategy);
        Assert.True(double.IsFinite(baseline.Diagnostics.Production.MedianMilliseconds));
        Assert.True(double.IsFinite(baseline.Diagnostics.Production.P95Milliseconds));
        Assert.True(double.IsFinite(baseline.Diagnostics.Legacy.MedianMilliseconds));
        Assert.True(double.IsFinite(baseline.Diagnostics.Legacy.P95Milliseconds));
    }

    [Fact]
    public void Evaluate_UsesMacroMicroRankAndNoMatchFormulas()
    {
        var itemOne = Guid.Parse("50000000-0000-0000-0000-000000000001");
        var itemTwo = Guid.Parse("50000000-0000-0000-0000-000000000002");
        var distractorItem = Guid.Parse("50000000-0000-0000-0000-000000000003");
        var chunkOne = Guid.Parse("60000000-0000-0000-0000-000000000001");
        var chunkTwo = Guid.Parse("60000000-0000-0000-0000-000000000002");
        var chunkThree = Guid.Parse("60000000-0000-0000-0000-000000000003");
        var distractorChunk = Guid.Parse("60000000-0000-0000-0000-000000000004");
        var corpus = FormulaCorpus(
            itemOne,
            itemTwo,
            distractorItem,
            chunkOne,
            chunkTwo,
            chunkThree,
            distractorChunk);
        var results = new[]
        {
            new MemoryRetrievalQueryResult("positive-two-labels", [new(itemOne, chunkOne)]),
            new MemoryRetrievalQueryResult("positive-rank-two", [new(distractorItem, distractorChunk), new(itemTwo, chunkThree)]),
            new MemoryRetrievalQueryResult("true-no-match", [new(distractorItem, distractorChunk)])
        };

        var evaluation = MemoryRetrievalMetrics.Evaluate(corpus, results);

        Assert.Equal(0.75, evaluation.Metrics.ChunkMacroRecallAt5);
        Assert.Equal(2d / 3d, evaluation.Metrics.ChunkMicroRecallAt5);
        Assert.Equal(1, evaluation.Metrics.ItemMacroRecallAt5);
        Assert.Equal(1, evaluation.Metrics.ItemMicroRecallAt5);
        Assert.Equal(1.5, evaluation.Metrics.MeanFirstRelevantChunkRank);
        Assert.Equal(0, evaluation.Metrics.NoMatchPrecision);
        Assert.Equal(1, evaluation.Metrics.NoMatchFalsePositiveCount);
        Assert.Equal(0, evaluation.Metrics.PredictedNoMatchCount);
    }

    [Fact]
    public void Evaluate_UsesSentinelSixAndExactNoMatchPrecisionDenominator()
    {
        var itemId = Guid.Parse("70000000-0000-0000-0000-000000000001");
        var chunkId = Guid.Parse("80000000-0000-0000-0000-000000000001");
        var corpus = new MemoryRetrievalBenchmarkCorpus(
            1,
            "memory-retrieval-corpus-v1",
            Guid.Parse("90000000-0000-0000-0000-000000000001"),
            "tenant",
            "owner",
            "model",
            1,
            5,
            0.25,
            new Dictionary<string, string>(),
            [Item(itemId, chunkId)],
            [
                new("positive", "positive", "service", [itemId], [chunkId]),
                new("no-match", "no match", "service", [], [])
            ]);

        var evaluation = MemoryRetrievalMetrics.Evaluate(corpus,
        [
            new("positive", []),
            new("no-match", [])
        ]);

        var positive = Assert.Single(evaluation.Queries, static query => query.QueryId == "positive");
        Assert.Equal(6, positive.FirstRelevantChunkRank);
        Assert.Equal(0.5, evaluation.Metrics.NoMatchPrecision);
        Assert.Equal(0, evaluation.Metrics.NoMatchFalsePositiveCount);
        Assert.Equal(2, evaluation.Metrics.PredictedNoMatchCount);
    }

    private static MemoryRetrievalBenchmarkCorpus FormulaCorpus(
        Guid itemOne,
        Guid itemTwo,
        Guid distractorItem,
        Guid chunkOne,
        Guid chunkTwo,
        Guid chunkThree,
        Guid distractorChunk) => new(
            1,
            "memory-retrieval-corpus-v1",
            Guid.Parse("90000000-0000-0000-0000-000000000002"),
            "tenant",
            "owner",
            "model",
            1,
            5,
            0.25,
            new Dictionary<string, string>(),
            [Item(itemOne, chunkOne, chunkTwo), Item(itemTwo, chunkThree), Item(distractorItem, distractorChunk)],
            [
                new("positive-two-labels", "one", "service", [itemOne], [chunkOne, chunkTwo]),
                new("positive-rank-two", "two", "service", [itemTwo], [chunkThree]),
                new("true-no-match", "none", "service", [], [])
            ]);

    private static MemoryRetrievalBenchmarkItem Item(Guid itemId, params Guid[] chunkIds) => new(
        itemId,
        "runbook",
        "source/" + itemId,
        "title",
        "content",
        [],
        "service",
        null,
        null,
        true,
        chunkIds.Select((chunkId, index) =>
            new MemoryRetrievalBenchmarkChunk(chunkId, index, "text")).ToArray());

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "IncidentCompass.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}
