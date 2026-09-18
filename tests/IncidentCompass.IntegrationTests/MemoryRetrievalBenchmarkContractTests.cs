using System.Text.Json.Nodes;
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

    [Fact]
    public void V3Corpus_CarriesEveryVersionTwoItemChunkAndQueryVerbatimExceptTheAddedSignal()
    {
        var repoRoot = FindRepoRoot();
        var version2 = MemoryRetrievalBenchmarkCorpus.LoadV2(repoRoot);
        var corpus = MemoryRetrievalBenchmarkCorpus.LoadV3(repoRoot);

        Assert.Equal(3, corpus.SchemaVersion);
        Assert.Equal(MemoryRetrievalBenchmarkCorpus.Version3, corpus.CorpusVersion);
        Assert.Equal(version2.TenantId, corpus.TenantId);
        Assert.Equal(version2.EmbeddingModel, corpus.EmbeddingModel);
        Assert.Equal(version2.EmbeddingDimensions, corpus.EmbeddingDimensions);
        Assert.Equal(version2.TopK, corpus.TopK);
        Assert.Equal(version2.MinScore, corpus.MinScore);
        Assert.Equal(version2.CurrentReleases.OrderBy(static pair => pair.Key), corpus.CurrentReleases.OrderBy(static pair => pair.Key));
        Assert.Equal(version2.Items.Select(static item => item.Id), corpus.Items.Select(static item => item.Id));
        var noTags = Array.Empty<string>();
        var noChunks = Array.Empty<MemoryRetrievalBenchmarkChunk>();
        foreach (var (expected, actual) in version2.Items.Zip(corpus.Items))
        {
            Assert.Equal(expected with { Tags = noTags, Chunks = noChunks }, actual with { Tags = noTags, Chunks = noChunks });
            Assert.Equal(expected.Tags, actual.Tags);
            Assert.Equal(expected.Chunks, actual.Chunks);
        }

        Assert.Equal(version2.Queries.Select(static query => query.Id), corpus.Queries.Select(static query => query.Id));
        foreach (var (expected, actual) in version2.Queries.Zip(corpus.Queries))
        {
            Assert.Null(expected.Signal);
            Assert.NotNull(actual.Signal);
            Assert.Equal(expected.Text, actual.Text);
            Assert.Equal(expected.ServiceName, actual.ServiceName);
            Assert.Equal(expected.Category, actual.Category);
            Assert.Equal(expected.RelevantItemIds, actual.RelevantItemIds);
            Assert.Equal(expected.RelevantChunkIds, actual.RelevantChunkIds);
        }
    }

    [Fact]
    public void V3Corpus_SplitsEveryIncidentQueryIntoItsSignalAndGivesEveryKeywordQueryADistinctFault()
    {
        var corpus = MemoryRetrievalBenchmarkCorpus.LoadV3(FindRepoRoot());
        var incidentShaped = corpus.Queries.Where(IsIncidentShaped).ToArray();
        var keywordShaped = corpus.Queries.Where(static query => !IsIncidentShaped(query)).ToArray();

        Assert.All(corpus.Queries, query =>
        {
            var signal = Assert.IsType<MemoryRetrievalBenchmarkSignal>(query.Signal);
            Assert.False(string.IsNullOrWhiteSpace(signal.ServiceName), query.Id + " has no signal service name.");
            Assert.False(string.IsNullOrWhiteSpace(signal.ErrorType), query.Id + " has no signal error type.");
            Assert.False(string.IsNullOrWhiteSpace(signal.ErrorMessage), query.Id + " has no signal message.");
            Assert.Equal(query.ServiceName, signal.ServiceName);
            Assert.EndsWith("Exception", signal.ErrorType, StringComparison.Ordinal);
        });
        Assert.Equal(16, incidentShaped.Length);
        Assert.All(incidentShaped, query =>
        {
            var signal = query.Signal!;
            foreach (var field in new[] { signal.ServiceName, signal.ErrorType, signal.ErrorMessage, signal.HttpRoute, signal.OperationName })
            {
                Assert.True(
                    field is null || query.Text.Contains(field, StringComparison.Ordinal),
                    query.Id + " carries the signal field '" + field + "', which its query text does not.");
            }

            var prefix = signal.ServiceName + " " + signal.ErrorType + " " + signal.ErrorMessage;
            Assert.StartsWith(prefix, query.Text, StringComparison.Ordinal);
            var remainder = query.Text[prefix.Length..];
            Assert.True(
                remainder.Length == 0 || remainder == " on " + signal.HttpRoute,
                query.Id + " leaves '" + remainder + "' of its query text out of its signal.");
        });
        Assert.Equal(8, keywordShaped.Length);
        Assert.Equal(
            MemoryRetrievalQueryCategory.All.Order(StringComparer.Ordinal),
            keywordShaped.Select(static query => query.Category!).Distinct().Order(StringComparer.Ordinal));
        Assert.All(keywordShaped, query =>
        {
            var message = query.Signal!.ErrorMessage;
            Assert.False(
                query.Text.Contains(message, StringComparison.OrdinalIgnoreCase),
                query.Id + " repeats its signal message as its query.");
            Assert.False(
                message.Contains(query.Text, StringComparison.OrdinalIgnoreCase),
                query.Id + " repeats its query inside its signal message.");
            Assert.True(
                WordCount(message) > 2 * WordCount(query.Text),
                query.Id + " has a signal message no longer than a keyword query.");
        });
    }

    [Fact]
    public void V3MultilingualQueries_CoverEveryQueryOncePerLanguageAndCutEachMessageFromTheirOwnText()
    {
        var repoRoot = FindRepoRoot();
        var corpus = MemoryRetrievalBenchmarkCorpus.LoadV3(repoRoot);
        var multilingual = MemoryRetrievalMultilingualQueries.LoadV3(repoRoot);
        var version2 = MemoryRetrievalMultilingualQueries.LoadV2(repoRoot);
        var languages = new[] { MemoryRetrievalMultilingualQueries.Polish, MemoryRetrievalMultilingualQueries.Russian };
        var sources = corpus.Queries.ToDictionary(static query => query.Id, StringComparer.Ordinal);

        Assert.Equal(3, multilingual.SchemaVersion);
        Assert.Equal(MemoryRetrievalMultilingualQueries.Version3, multilingual.Version);
        Assert.Equal(corpus.CorpusVersion, multilingual.CorpusVersion);
        Assert.Equal(
            version2.Queries.Select(static entry => (entry.Id, entry.SourceQueryId, entry.Language, entry.Text)),
            multilingual.Queries.Select(static entry => (entry.Id, entry.SourceQueryId, entry.Language, entry.Text)));
        foreach (var language in languages)
        {
            var covered = multilingual.Queries
                .Where(entry => entry.Language == language)
                .Select(static entry => entry.SourceQueryId)
                .ToArray();
            Assert.Equal(sources.Count, covered.Length);
            Assert.True(sources.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(covered), language + " does not cover every corpus query exactly once.");
        }

        Assert.All(multilingual.Queries, entry =>
        {
            var message = entry.ErrorMessage;
            Assert.NotNull(message);
            Assert.False(string.IsNullOrWhiteSpace(message), entry.Id + " has no error message.");
            Assert.True(
                entry.Text.Contains(message, StringComparison.Ordinal),
                entry.Id + " has an error message that is not a contiguous part of its own text.");
            var source = sources[entry.SourceQueryId];
            var signal = source.Signal!;
            if (IsIncidentShaped(source))
            {
                Assert.StartsWith(signal.ServiceName + " " + signal.ErrorType + " " + message, entry.Text, StringComparison.Ordinal);
            }
            else
            {
                Assert.Equal(entry.Text, message);
            }
        });

        var pooled = multilingual.ToCorpus(corpus, languages);
        var entries = multilingual.Queries.ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
        Assert.Equal(corpus.Queries.Count * languages.Length, pooled.Queries.Count);
        Assert.All(pooled.Queries, query =>
        {
            var entry = entries[query.Id];
            var english = sources[entry.SourceQueryId].Signal!;
            Assert.Equal(english with { ErrorMessage = entry.ErrorMessage! }, query.Signal);
        });
        Assert.All(
            version2.ToCorpus(MemoryRetrievalBenchmarkCorpus.LoadV2(repoRoot), languages).Queries,
            static query => Assert.Null(query.Signal));
    }

    [Theory]
    [InlineData("corpus-v3.json", "missing")]
    [InlineData("corpus-v3.json", "blank-message")]
    [InlineData("corpus-v3.json", "other-service")]
    [InlineData("corpus-v2.json", "present")]
    public void CorpusLoader_RequiresASignalFromSchemaThreeOnAndRefusesOneBeforeIt(string fileName, string mutation)
    {
        var repoRoot = FindRepoRoot();
        var fixtureDirectory = Path.Combine("tests", "IncidentCompass.IntegrationTests", "Fixtures", "MemoryRetrieval");
        var document = JsonNode.Parse(File.ReadAllText(Path.Combine(repoRoot, fixtureDirectory, fileName)))!;
        var firstQuery = document["queries"]![0]!.AsObject();
        switch (mutation)
        {
            case "missing":
                Assert.True(firstQuery.Remove("signal"));
                break;
            case "blank-message":
                firstQuery["signal"]!["errorMessage"] = " ";
                break;
            case "other-service":
                firstQuery["signal"]!["serviceName"] = "orders-api";
                break;
            default:
                Assert.False(firstQuery.ContainsKey("signal"));
                firstQuery["signal"] = JsonNode.Parse(
                    "{\"serviceName\":\"checkout-api\",\"errorType\":\"TimeoutException\",\"errorMessage\":\"timed out\"}");
                break;
        }

        var scratchRoot = Path.Combine(Path.GetTempPath(), "memory-retrieval-contract-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(scratchRoot, fixtureDirectory));
            File.WriteAllText(Path.Combine(scratchRoot, fixtureDirectory, fileName), document.ToJsonString());

            Assert.Throws<InvalidOperationException>(() => fileName == "corpus-v3.json"
                ? MemoryRetrievalBenchmarkCorpus.LoadV3(scratchRoot)
                : MemoryRetrievalBenchmarkCorpus.LoadV2(scratchRoot));
        }
        finally
        {
            Directory.Delete(scratchRoot, recursive: true);
        }
    }

    /// <summary>
    /// An incident-shaped query reads like the fault that triggered it: its own service name, then an
    /// exception type. A keyword query is the terse wording a model would send instead.
    /// </summary>
    private static bool IsIncidentShaped(MemoryRetrievalBenchmarkQuery query) =>
        query.Text.StartsWith(query.ServiceName + " ", StringComparison.Ordinal);

    private static int WordCount(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

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
