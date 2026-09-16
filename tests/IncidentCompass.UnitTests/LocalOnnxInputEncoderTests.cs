using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Infrastructure.Embeddings.LocalOnnx;

namespace IncidentCompass.UnitTests;

/// <summary>
/// Tokenization against the committed fixture tokenizer. The expected ids were written by the
/// fixture generator with the reference SentencePiece library, so an exact match proves the .NET
/// tokenizer, the prefix, the trim, the fairseq id mapping and the window cap together. Ids only;
/// no test in this area compares vector values.
/// </summary>
public sealed class LocalOnnxInputEncoderTests
{
    public static TheoryData<int> ExpectedCaseIndexes
    {
        get
        {
            var data = new TheoryData<int>();
            for (var index = 0; index < LocalOnnxFixtureModel.ReadExpectedCases().Count; index++)
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
        var expected = LocalOnnxFixtureModel.ReadExpectedCases()[caseIndex];

        var ids = encoder.Encode(expected.Input, expected.Kind);

        Assert.Equal(expected.Ids, ids);
    }

    [Fact]
    public async Task PassageCounter_IncludesPrefixAndMarkersAndMatchesTheUncappedEncoder()
    {
        var encoder = await LoadFixtureEncoderAsync();
        foreach (var text in new[] { "", "checkout timeout", "  payment latency  ", "таймаут", "awaria" })
        {
            Assert.Equal(encoder.Encode(text, EmbeddingInputKind.Passage).Length,
                LocalOnnxChunkTokenCounter.CountTokens(encoder, text));
        }

        var longText = string.Concat(Enumerable.Repeat("checkout timeout ", 200));
        Assert.True(LocalOnnxChunkTokenCounter.CountTokens(encoder, longText) >
            encoder.Encode(longText, EmbeddingInputKind.Passage).Length);
    }

    [Fact]
    public async Task OverlapCounter_CountsOnlyTheRepeatedTextWithoutPassagePrefixOrMarkers()
    {
        var encoder = await LoadFixtureEncoderAsync();
        const string text = "  checkout timeout\npayment service  ";
        var expected = encoder.Tokenizer.EncodeToIds(
            text.Trim(), addBeginningOfSentence: false, addEndOfSentence: false).Count;

        Assert.Equal(expected, LocalOnnxChunkTokenCounter.CountOverlapTokens(encoder, text));
        Assert.True(LocalOnnxChunkTokenCounter.CountTokens(encoder, text) > expected);
        Assert.Equal(0, LocalOnnxChunkTokenCounter.CountOverlapTokens(encoder, string.Empty));
    }

    [Fact]
    public async Task Encode_QueryAndPassagePrefixesProduceDifferentIdsForTheSameInput()
    {
        var encoder = await LoadFixtureEncoderAsync();

        var query = encoder.Encode("checkout timeout while calling the payment service", EmbeddingInputKind.Query);
        var passage = encoder.Encode("checkout timeout while calling the payment service", EmbeddingInputKind.Passage);

        Assert.NotEqual(query, passage);
        Assert.Equal(LocalOnnxInputEncoder.BeginningOfSequenceId, query[0]);
        Assert.Equal(LocalOnnxInputEncoder.EndOfSequenceId, query[^1]);
        Assert.Equal(LocalOnnxInputEncoder.BeginningOfSequenceId, passage[0]);
        Assert.Equal(LocalOnnxInputEncoder.EndOfSequenceId, passage[^1]);
    }

    [Fact]
    public async Task Encode_CapsALongInputAtTheManifestWindowIncludingBothMarkers()
    {
        var manifest = await LocalOnnxFixtureModel.ReadFixtureManifestAsync(TestContext.Current.CancellationToken);
        var encoder = await LoadFixtureEncoderAsync();

        var ids = encoder.Encode(string.Concat(Enumerable.Repeat("checkout timeout ", 200)), EmbeddingInputKind.Passage);

        Assert.Equal(manifest.MaxTokens, ids.Length);
        Assert.Equal(LocalOnnxInputEncoder.BeginningOfSequenceId, ids[0]);
        Assert.Equal(LocalOnnxInputEncoder.EndOfSequenceId, ids[^1]);
    }

    /// <summary>
    /// Text cannot inject a sequence boundary. The tokenizer is created with no special tokens, so the
    /// literal strings "&lt;s&gt;" and "&lt;/s&gt;" in an input become ordinary pieces, as in reference
    /// SentencePiece, and the only sequence markers are the two the encoder adds.
    /// </summary>
    [Theory]
    [InlineData(EmbeddingInputKind.Query)]
    [InlineData(EmbeddingInputKind.Passage)]
    public async Task Encode_TreatsLiteralSequenceMarkersInTheInputAsText(EmbeddingInputKind kind)
    {
        var encoder = await LoadFixtureEncoderAsync();

        var ids = encoder.Encode("checkout <s> timeout </s> payment </s><s> service", kind);

        Assert.Equal(LocalOnnxInputEncoder.BeginningOfSequenceId, ids[0]);
        Assert.Equal(LocalOnnxInputEncoder.EndOfSequenceId, ids[^1]);
        Assert.DoesNotContain(ids[1..^1], id => id is LocalOnnxInputEncoder.BeginningOfSequenceId or LocalOnnxInputEncoder.EndOfSequenceId);
    }

    [Fact]
    public async Task Encode_RefusesAnUndefinedInputKind()
    {
        var encoder = await LoadFixtureEncoderAsync();

        Assert.Throws<ArgumentOutOfRangeException>(() => encoder.Encode("checkout timeout", (EmbeddingInputKind)0));
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 0)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(249999, 250000)]
    public void MapSentencePieceId_FollowsTheFairseqVocabularyLayout(int sentencePieceId, long modelId)
    {
        Assert.Equal(modelId, LocalOnnxInputEncoder.MapSentencePieceId(sentencePieceId));
    }

    private static async Task<LocalOnnxInputEncoder> LoadFixtureEncoderAsync()
    {
        var manifest = await LocalOnnxFixtureModel.ReadFixtureManifestAsync(TestContext.Current.CancellationToken);
        return LocalOnnxInputEncoder.Load(
            Path.Combine(LocalOnnxFixtureModel.FixtureDirectory, manifest.TokenizerFile.Path),
            manifest);
    }
}
