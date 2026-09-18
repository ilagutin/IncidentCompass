using IncidentCompass.Application.Memory;
using IncidentCompass.Infrastructure.EmbeddingModels;
using IncidentCompass.Infrastructure.Relevance.LocalOnnx;

namespace IncidentCompass.UnitTests;

/// <summary>
/// Pair tokenization against the committed relevance-judge fixture tokenizer. The expected ids were
/// written by the fixture generator with the reference SentencePiece library, so an exact match
/// proves the .NET tokenizer, the trim, the fairseq id mapping, the pair layout and the truncation
/// order together. Ids only; no test here compares a score.
/// </summary>
public sealed class LocalOnnxPairEncoderTests
{
    public static TheoryData<int> ExpectedCaseIndexes
    {
        get
        {
            var data = new TheoryData<int>();
            for (var index = 0; index < LocalOnnxRelevanceJudgeFixtureModel.ReadExpectedCases().Count; index++)
            {
                data.Add(index);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ExpectedCaseIndexes))]
    public async Task Encode_ProducesTheIdsTheFixtureGeneratorRecorded(int caseIndex)
    {
        var encoder = await LoadFixtureEncoderAsync();
        var expected = LocalOnnxRelevanceJudgeFixtureModel.ReadExpectedCases()[caseIndex];

        var ids = encoder.Encode(expected.Query, expected.Passage);

        Assert.Equal(expected.Ids, ids);
    }

    /// <summary>
    /// The layout taken from <c>XLMRobertaTokenizerFast</c>: one opening marker, the query, two
    /// markers, the passage, one closing marker. The single-text encoding of each segment is the same
    /// segment without the pair's extra separator, which is what makes the two halves comparable.
    /// </summary>
    [Fact]
    public async Task Encode_WrapsTheQueryAndThePassageInTheDocumentedPairLayout()
    {
        var encoder = await LoadFixtureEncoderAsync();
        var queryIds = ContentIds(encoder, "checkout timeout");
        var passageIds = ContentIds(encoder, "payment service latency");

        var ids = encoder.Encode("checkout timeout", "payment service latency");

        long[] expected =
        [
            LocalOnnxSentencePieceVocabulary.BeginningOfSequenceId,
            .. queryIds,
            LocalOnnxSentencePieceVocabulary.EndOfSequenceId,
            LocalOnnxSentencePieceVocabulary.EndOfSequenceId,
            .. passageIds,
            LocalOnnxSentencePieceVocabulary.EndOfSequenceId
        ];
        Assert.Equal(expected, ids);
        Assert.Equal(queryIds.Length + passageIds.Length + LocalOnnxPairEncoder.SpecialTokenCount, ids.Length);
    }

    [Fact]
    public async Task Encode_TrimsBothSegmentsBeforeTokenizing()
    {
        var encoder = await LoadFixtureEncoderAsync();

        Assert.Equal(
            encoder.Encode("gateway latency", "payment service"),
            encoder.Encode("  \t gateway latency \n ", " payment service   "));
    }

    /// <summary>
    /// The query keeps every token it has and the passage is cut to what is left, which is the
    /// opposite of what the reference tokenizer's default would do here.
    /// </summary>
    [Fact]
    public async Task Encode_CutsThePassageBeforeTheQuery()
    {
        var encoder = await LoadFixtureEncoderAsync();
        const string query = "checkout timeout while calling the payment service";
        var queryIds = ContentIds(encoder, query);

        var ids = encoder.Encode(query, string.Concat(Enumerable.Repeat("payment service latency ", 80)));

        Assert.Equal(encoder.MaxContentTokens + LocalOnnxPairEncoder.SpecialTokenCount, ids.Length);
        Assert.Equal(queryIds, ids[1..(1 + queryIds.Length)]);
        Assert.Equal(LocalOnnxSentencePieceVocabulary.EndOfSequenceId, ids[1 + queryIds.Length]);
        Assert.Equal(LocalOnnxSentencePieceVocabulary.EndOfSequenceId, ids[2 + queryIds.Length]);
        Assert.Equal(LocalOnnxSentencePieceVocabulary.EndOfSequenceId, ids[^1]);
    }

    /// <summary>
    /// The query is cut at half the content budget however long it is, and it is cut on a token
    /// boundary: every id it keeps is the id the uncapped encoding had in that position, so no token
    /// was split into a different piece.
    /// <para>
    /// The half is the defect this test exists for. Giving the query the whole budget first let a
    /// long enough query leave the passage nothing, and every candidate scored against an empty
    /// passage receives the same score, so the judge admitted all of them or none whatever they
    /// contained.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Encode_CutsTheQueryAtHalfTheBudgetSoThePassageIsNeverStarved()
    {
        var encoder = await LoadFixtureEncoderAsync();
        var longQuery = string.Concat(Enumerable.Repeat("checkout timeout while calling the payment service ", 8));
        var longPassage = string.Concat(Enumerable.Repeat("payment service latency ", 80));
        var uncappedQueryIds = ContentIds(encoder, longQuery);
        Assert.True(uncappedQueryIds.Length > encoder.MaxQueryTokens);

        var ids = encoder.Encode(longQuery, longPassage);

        var (queryCount, passageCount) = SegmentLengths(ids);
        Assert.Equal(encoder.MaxQueryTokens, queryCount);
        Assert.Equal(encoder.MaxContentTokens - encoder.MaxQueryTokens, passageCount);
        Assert.True(passageCount >= encoder.MaxContentTokens / 2);
        Assert.Equal(uncappedQueryIds.Take(encoder.MaxQueryTokens), ids[1..(1 + queryCount)]);
    }

    /// <summary>
    /// The tool refuses a query longer than <c>MemorySearchQueryBound.MaxQueryCharacters</c>, and
    /// that bound is derived from this window. At the pinned window a query written right up to it
    /// still leaves the passage at least half the sequence, so the bound and the split agree rather
    /// than each assuming the other.
    /// </summary>
    [Fact]
    public async Task Encode_AQueryAtTheToolsOwnBoundStillLeavesThePassageHalfThePinnedWindow()
    {
        var manifest = await LocalOnnxRelevanceJudgeFixtureModel.ReadFixtureManifestAsync(
            TestContext.Current.CancellationToken);
        var encoder = LocalOnnxRelevanceJudgeFixtureModel.LoadFixtureEncoder(
            manifest with { MaxTokens = MemorySearchQueryBound.JudgeTokenWindow });
        var query = string.Concat(Enumerable.Repeat("checkout timeout payment ", 200))[
            ..MemorySearchQueryBound.MaxQueryCharacters];
        var passage = string.Concat(Enumerable.Repeat("payment service latency circuit breaker ", 200));

        var ids = encoder.Encode(query, passage);

        var (queryCount, passageCount) = SegmentLengths(ids);
        Assert.InRange(ids.Length, LocalOnnxPairEncoder.SpecialTokenCount, MemorySearchQueryBound.JudgeTokenWindow);
        Assert.Equal(MemorySearchQueryBound.MaxQueryTokens, encoder.MaxQueryTokens);
        Assert.True(queryCount <= encoder.MaxQueryTokens);
        Assert.True(passageCount >= encoder.MaxContentTokens / 2);
    }

    /// <summary>
    /// The query and the passage ids, read back out of the pair layout: one opening marker, the
    /// query, two separators, the passage, one closing marker.
    /// </summary>
    private static (int QueryCount, int PassageCount) SegmentLengths(long[] ids)
    {
        for (var index = 1; index + 1 < ids.Length; index++)
        {
            if (ids[index] == LocalOnnxSentencePieceVocabulary.EndOfSequenceId &&
                ids[index + 1] == LocalOnnxSentencePieceVocabulary.EndOfSequenceId)
            {
                return (index - 1, ids.Length - index - 3);
            }
        }

        throw new InvalidOperationException("The encoded pair carries no separator pair.");
    }

    [Fact]
    public async Task Encode_NeverExceedsTheManifestWindow()
    {
        var manifest = await LocalOnnxRelevanceJudgeFixtureModel.ReadFixtureManifestAsync(
            TestContext.Current.CancellationToken);
        var encoder = LocalOnnxRelevanceJudgeFixtureModel.LoadFixtureEncoder(manifest);
        var long1 = string.Concat(Enumerable.Repeat("checkout timeout ", 200));
        var long2 = string.Concat(Enumerable.Repeat("payment latency ", 200));

        foreach (var (query, passage) in new[] { (long1, long2), (long1, "short"), ("short", long2), ("", "") })
        {
            Assert.InRange(encoder.Encode(query, passage).Length, LocalOnnxPairEncoder.SpecialTokenCount, manifest.MaxTokens);
        }
    }

    /// <summary>
    /// Text cannot inject a sequence boundary. The tokenizer is created with no special tokens, so
    /// the literal strings "&lt;s&gt;" and "&lt;/s&gt;" in either segment become ordinary pieces, and
    /// the only markers are the four the encoder adds.
    /// </summary>
    [Fact]
    public async Task Encode_TreatsLiteralSequenceMarkersInEitherSegmentAsText()
    {
        var encoder = await LoadFixtureEncoderAsync();

        var ids = encoder.Encode("checkout <s> timeout </s>", "payment </s><s> service");

        var markers = ids.Index()
            .Where(static entry => entry.Item is LocalOnnxSentencePieceVocabulary.BeginningOfSequenceId
                or LocalOnnxSentencePieceVocabulary.EndOfSequenceId)
            .Select(static entry => entry.Index)
            .ToArray();
        Assert.Equal(LocalOnnxPairEncoder.SpecialTokenCount, markers.Length);
        Assert.Equal(0, markers[0]);
        Assert.Equal(markers[1] + 1, markers[2]);
        Assert.Equal(ids.Length - 1, markers[3]);
    }

    [Fact]
    public async Task Encode_RefusesANullSegment()
    {
        var encoder = await LoadFixtureEncoderAsync();

        Assert.Throws<ArgumentNullException>(() => encoder.Encode(null!, "passage"));
        Assert.Throws<ArgumentNullException>(() => encoder.Encode("query", null!));
    }

    /// <summary>
    /// A manifest window that leaves no room for content at all is refused when the encoder is built,
    /// rather than producing a sequence longer than the window it claims.
    /// </summary>
    [Fact]
    public async Task Load_RefusesAWindowThatLeavesNoRoomForContent()
    {
        var manifest = await LocalOnnxRelevanceJudgeFixtureModel.ReadFixtureManifestAsync(
            TestContext.Current.CancellationToken);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LocalOnnxRelevanceJudgeFixtureModel.LoadFixtureEncoder(
                manifest with { MaxTokens = LocalOnnxPairEncoder.SpecialTokenCount }));
    }

    private static long[] ContentIds(LocalOnnxPairEncoder encoder, string text) =>
        encoder.Tokenizer
            .EncodeToIds(text.Trim(), addBeginningOfSentence: false, addEndOfSentence: false)
            .Select(LocalOnnxSentencePieceVocabulary.MapSentencePieceId)
            .ToArray();

    private static async Task<LocalOnnxPairEncoder> LoadFixtureEncoderAsync() =>
        LocalOnnxRelevanceJudgeFixtureModel.LoadFixtureEncoder(
            await LocalOnnxRelevanceJudgeFixtureModel.ReadFixtureManifestAsync(TestContext.Current.CancellationToken));
}
