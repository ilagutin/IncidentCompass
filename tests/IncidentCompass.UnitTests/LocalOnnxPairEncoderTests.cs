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
    /// Only when the passage has nothing left to give is the query cut, and then it is cut on a token
    /// boundary: every id it keeps is the id the uncapped encoding had in that position, so no token
    /// was split into a different piece.
    /// </summary>
    [Fact]
    public async Task Encode_CutsTheQueryOnlyWhenThePassageCannotMakeRoomAndNeverSplitsAToken()
    {
        var encoder = await LoadFixtureEncoderAsync();
        var longQuery = string.Concat(Enumerable.Repeat("checkout timeout while calling the payment service ", 8));
        var uncappedQueryIds = ContentIds(encoder, longQuery);
        Assert.True(uncappedQueryIds.Length > encoder.MaxContentTokens);

        var ids = encoder.Encode(longQuery, "payment service latency");

        Assert.Equal(encoder.MaxContentTokens + LocalOnnxPairEncoder.SpecialTokenCount, ids.Length);
        var keptQueryIds = ids[1..(1 + encoder.MaxContentTokens)];
        Assert.Equal(uncappedQueryIds.Take(encoder.MaxContentTokens), keptQueryIds);

        // The passage kept nothing: the two separators and the closing marker are all that follow.
        Assert.Equal(
            [
                LocalOnnxSentencePieceVocabulary.EndOfSequenceId,
                LocalOnnxSentencePieceVocabulary.EndOfSequenceId,
                LocalOnnxSentencePieceVocabulary.EndOfSequenceId
            ],
            ids[(1 + encoder.MaxContentTokens)..]);
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
