using IncidentCompass.Application.Memory;

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

    [Fact]
    public void V2Corpus_PinsCompositionCategoriesAndTheVersionOneCarryOver()
    {
        var repoRoot = FindRepoRoot();
        var corpus = MemoryRetrievalBenchmarkCorpus.LoadV2(repoRoot);
        var version1 = MemoryRetrievalBenchmarkCorpus.Load(repoRoot);
        var itemIds = corpus.Items.Select(static item => item.Id).ToHashSet();
        var chunkIds = corpus.Items.SelectMany(static item => item.Chunks).Select(static chunk => chunk.Id).ToHashSet();

        Assert.Equal(2, corpus.SchemaVersion);
        Assert.Equal(MemoryRetrievalBenchmarkCorpus.Version2, corpus.CorpusVersion);
        Assert.Equal(5, corpus.TopK);
        Assert.Equal(16, corpus.EmbeddingDimensions);
        Assert.Equal(24, corpus.Queries.Count);
        Assert.Equal(12, CountCategory(corpus, MemoryRetrievalQueryCategory.Positive));
        Assert.Equal(6, CountCategory(corpus, MemoryRetrievalQueryCategory.OffTopic));
        Assert.Equal(6, CountCategory(corpus, MemoryRetrievalQueryCategory.HardNegative));
        Assert.Equal(corpus.Items.Count, itemIds.Count);
        Assert.Equal(corpus.Items.Sum(static item => item.Chunks.Count), chunkIds.Count);
        Assert.Equal(
            corpus.Queries.Count,
            corpus.Queries.Select(static query => query.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(corpus.Queries, query =>
        {
            Assert.True(MemoryRetrievalQueryCategory.IsKnown(query.Category), query.Id + " has no known category.");
            Assert.All(query.RelevantItemIds, itemId => Assert.Contains(itemId, itemIds));
            Assert.All(query.RelevantChunkIds, chunkId => Assert.Contains(chunkId, chunkIds));
            if (query.Category == MemoryRetrievalQueryCategory.Positive)
            {
                Assert.NotEmpty(query.RelevantItemIds);
                Assert.NotEmpty(query.RelevantChunkIds);
            }
            else
            {
                Assert.Empty(query.RelevantItemIds);
                Assert.Empty(query.RelevantChunkIds);
            }
        });
        AssertVersionOneCarriedOver(version1, corpus);
        AssertEveryRetiredItemHasAnActiveSibling(corpus);
    }

    [Fact]
    public void V2MultilingualQueries_CoverEveryQueryTwiceAndKeepLatinIdentifiers()
    {
        var repoRoot = FindRepoRoot();
        var corpus = MemoryRetrievalBenchmarkCorpus.LoadV2(repoRoot);
        var multilingual = MemoryRetrievalMultilingualQueries.LoadV2(repoRoot);
        var languages = new[] { MemoryRetrievalMultilingualQueries.Polish, MemoryRetrievalMultilingualQueries.Russian };
        var sourceIds = corpus.Queries.Select(static query => query.Id).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(2, multilingual.SchemaVersion);
        Assert.Equal(MemoryRetrievalMultilingualQueries.Version2, multilingual.Version);
        Assert.Equal(corpus.CorpusVersion, multilingual.CorpusVersion);
        Assert.Equal(48, multilingual.Queries.Count);
        Assert.All(multilingual.Queries, entry =>
        {
            Assert.Contains(entry.SourceQueryId, sourceIds);
            Assert.Equal(entry.SourceQueryId + "-" + entry.Language, entry.Id);
            Assert.False(string.IsNullOrWhiteSpace(entry.Text));
        });
        foreach (var language in languages)
        {
            var covered = multilingual.Queries
                .Where(entry => entry.Language == language)
                .Select(static entry => entry.SourceQueryId)
                .ToArray();
            Assert.Equal(sourceIds.Count, covered.Length);
            Assert.True(sourceIds.SetEquals(covered), language + " does not cover every corpus query exactly once.");
        }

        var withIdentifiers = 0;
        foreach (var query in corpus.Queries)
        {
            var identifiers = LatinIdentifiers(query.Text);
            withIdentifiers += identifiers.Length > 0 ? 1 : 0;
            foreach (var entry in multilingual.Queries.Where(entry => entry.SourceQueryId == query.Id))
            {
                Assert.All(identifiers, identifier =>
                    Assert.True(
                        entry.Text.Contains(identifier, StringComparison.Ordinal),
                        entry.Id + " does not carry the identifier '" + identifier + "' verbatim."));
                Assert.All(LatinIdentifiers(entry.Text), identifier =>
                    Assert.True(
                        identifiers.Contains(identifier, StringComparer.Ordinal),
                        entry.Id + " invents the identifier '" + identifier + "', which its English source does not carry."));
            }
        }

        Assert.Equal(16, withIdentifiers);
        var pooled = multilingual.ToCorpus(corpus, languages);
        Assert.Equal(corpus.Queries.Count * 2, pooled.Queries.Count);
        Assert.All(pooled.Queries, query => Assert.True(MemoryRetrievalQueryCategory.IsKnown(query.Category)));
    }

    [Fact]
    public void BenchmarkRecordContract_NamesSchemaThreeAndItsBenchmarkId()
    {
        Assert.Equal(3, LocalEmbeddingModelBenchmarkContract.SchemaVersion);
        Assert.Equal("local-embedding-model-benchmark-v3", LocalEmbeddingModelBenchmarkContract.Benchmark);
    }

    [Fact]
    public void Evaluate_ConfirmsAHardNegativeOnlyWhenABandIsMediumOrHigh_AndNeverOnALowOrMissingBand()
    {
        var itemId = Guid.Parse("a0000000-0000-0000-0000-000000000001");
        var labelledChunk = Guid.Parse("b0000000-0000-0000-0000-000000000001");
        var siblingChunk = Guid.Parse("b0000000-0000-0000-0000-000000000002");
        var corpus = HardNegativeCorpus(itemId, labelledChunk, siblingChunk);

        var low = MemoryRetrievalMetrics.Evaluate(corpus,
        [
            new("positive", [new(itemId, labelledChunk)]),
            new("hard-negative", [new(itemId, siblingChunk, MemoryRetrievalConfidence.Low)])
        ]);
        var unbanded = MemoryRetrievalMetrics.Evaluate(corpus,
        [
            new("positive", [new(itemId, labelledChunk)]),
            new("hard-negative", [new(itemId, siblingChunk), new(itemId, labelledChunk)])
        ]);
        var medium = MemoryRetrievalMetrics.Evaluate(corpus,
        [
            new("positive", [new(itemId, labelledChunk)]),
            new("hard-negative",
            [
                new(itemId, siblingChunk, MemoryRetrievalConfidence.Low),
                new(itemId, labelledChunk, MemoryRetrievalConfidence.Medium)
            ])
        ]);

        Assert.Equal(1, low.Metrics.HardNegativeQueryCount);
        Assert.Equal(1, low.Metrics.HardNegativeReturnedCount);
        Assert.Equal(0, low.Metrics.HardNegativeConfirmedCount);
        Assert.Equal(1, unbanded.Metrics.HardNegativeQueryCount);
        Assert.Equal(1, unbanded.Metrics.HardNegativeReturnedCount);
        Assert.Equal(0, unbanded.Metrics.HardNegativeConfirmedCount);
        Assert.Equal(1, medium.Metrics.HardNegativeQueryCount);
        Assert.Equal(1, medium.Metrics.HardNegativeReturnedCount);
        Assert.Equal(1, medium.Metrics.HardNegativeConfirmedCount);
        Assert.Equal(0, medium.Metrics.OffTopicQueryCount);
        Assert.Equal(1, low.Metrics.NoMatchFalsePositiveCount);
        Assert.Equal(1, medium.Metrics.NoMatchFalsePositiveCount);
    }

    [Fact]
    public void Evaluate_LeavesEveryCategoryCountAtZeroForAVersionOneCorpus()
    {
        var corpus = MemoryRetrievalBenchmarkCorpus.Load(FindRepoRoot());
        var chunkOwners = corpus.Items
            .SelectMany(item => item.Chunks.Select(chunk => (ChunkId: chunk.Id, ItemId: item.Id)))
            .ToDictionary(static pair => pair.ChunkId, static pair => pair.ItemId);

        var evaluation = MemoryRetrievalMetrics.Evaluate(
            corpus,
            corpus.Queries.Select(query => new MemoryRetrievalQueryResult(
                query.Id,
                query.RelevantChunkIds.Select(chunkId =>
                    new MemoryRetrievalMatch(chunkOwners[chunkId], chunkId)).ToArray())).ToArray());

        Assert.All(corpus.Queries, static query => Assert.Null(query.Category));
        Assert.Equal(0, evaluation.Metrics.OffTopicQueryCount);
        Assert.Equal(0, evaluation.Metrics.OffTopicFalsePositiveCount);
        Assert.Equal(0, evaluation.Metrics.HardNegativeQueryCount);
        Assert.Equal(0, evaluation.Metrics.HardNegativeReturnedCount);
        Assert.Equal(0, evaluation.Metrics.HardNegativeConfirmedCount);
    }

    private static MemoryRetrievalBenchmarkCorpus HardNegativeCorpus(
        Guid itemId,
        Guid labelledChunk,
        Guid siblingChunk) => new(
            2,
            MemoryRetrievalBenchmarkCorpus.Version2,
            Guid.Parse("90000000-0000-0000-0000-000000000003"),
            "tenant",
            "owner",
            "model",
            1,
            5,
            0.25,
            new Dictionary<string, string>(),
            [Item(itemId, labelledChunk, siblingChunk)],
            [
                new("positive", "one", "service", [itemId], [labelledChunk], MemoryRetrievalQueryCategory.Positive),
                new("hard-negative", "two", "service", [], [], MemoryRetrievalQueryCategory.HardNegative)
            ]);

    private static int CountCategory(MemoryRetrievalBenchmarkCorpus corpus, string category) =>
        corpus.Queries.Count(query => string.Equals(query.Category, category, StringComparison.Ordinal));

    private static void AssertVersionOneCarriedOver(
        MemoryRetrievalBenchmarkCorpus version1,
        MemoryRetrievalBenchmarkCorpus version2)
    {
        foreach (var expected in version1.Items)
        {
            var actual = Assert.Single(version2.Items, item => item.Id == expected.Id);
            Assert.Equal(expected.Source, actual.Source);
            Assert.Equal(expected.Title, actual.Title);
            Assert.Equal(expected.Content, actual.Content);
            Assert.Equal(expected.ServiceName, actual.ServiceName);
            Assert.Equal(expected.ReleaseName, actual.ReleaseName);
            Assert.Equal(expected.IsActive, actual.IsActive);
            Assert.Equal(
                expected.Chunks.Select(static chunk => (chunk.Id, chunk.Position, chunk.Text)),
                actual.Chunks.Select(static chunk => (chunk.Id, chunk.Position, chunk.Text)));
        }

        foreach (var expected in version1.Queries)
        {
            var actual = Assert.Single(version2.Queries, query => query.Id == expected.Id);
            Assert.Equal(expected.Text, actual.Text);
            Assert.Equal(expected.ServiceName, actual.ServiceName);
            Assert.Equal(expected.RelevantItemIds, actual.RelevantItemIds);
            Assert.Equal(expected.RelevantChunkIds, actual.RelevantChunkIds);
        }
    }

    /// <summary>
    /// A retired item is only a hard negative when the corpus still holds an active item about the same
    /// service, because that is what stops a no-answer query from simply returning nothing. The service
    /// must also declare a current release that at least one of its active items carries, or the
    /// documentation status of every match on it degrades to <c>Unversioned</c> and the hard negative
    /// loses the <c>Current</c> against <c>Stale</c> signal that separates a live procedure from a
    /// retired one.
    /// </summary>
    private static void AssertEveryRetiredItemHasAnActiveSibling(MemoryRetrievalBenchmarkCorpus corpus)
    {
        var retired = corpus.Items.Where(static item => !item.IsActive).ToArray();
        Assert.Equal(6, retired.Length);
        Assert.All(retired, item =>
        {
            Assert.Contains(
                corpus.Items,
                sibling => sibling.IsActive && sibling.ServiceName == item.ServiceName);
            Assert.True(
                corpus.CurrentReleases.TryGetValue(item.ServiceName!, out var currentRelease),
                item.ServiceName + " declares no current release.");
            Assert.Contains(
                corpus.Items,
                sibling => sibling.IsActive &&
                    sibling.ServiceName == item.ServiceName &&
                    sibling.ReleaseName == currentRelease);
        });
    }

    /// <summary>
    /// The tokens of an English query that a rendering must repeat verbatim: exception type names,
    /// routes, hyphenated lowercase service names and dotted release numbers. The check is on this
    /// explicit shape rather than on every long ASCII token, because ordinary English words in a query
    /// are exactly what a rendering is supposed to translate.
    /// </summary>
    private static string[] LatinIdentifiers(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(static token => token.Trim(',', '.', ';', ':'))
            .Where(static token => token.Length > 0 && IsLatinIdentifier(token))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static bool IsLatinIdentifier(string token) =>
        token.EndsWith("Exception", StringComparison.Ordinal) ||
        token.StartsWith('/') ||
        (token.Contains('-', StringComparison.Ordinal) &&
         token.All(static character => char.IsAsciiLetterLower(character) || character == '-')) ||
        (token.Contains('.', StringComparison.Ordinal) &&
         token.All(static character => char.IsAsciiDigit(character) || character == '.'));

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
