using IncidentCompass.Infrastructure.EmbeddingModels;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The fairseq id mapping both LocalOnnx encoders build their sequences from: the embedding model's
/// single-sequence encoder and the relevance judge's pair encoder. It moved out of the embedding
/// encoder when the second one arrived, so there is one mapping rather than two that can drift.
/// </summary>
public sealed class LocalOnnxSentencePieceVocabularyTests
{
    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 0)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(249999, 250000)]
    public void MapSentencePieceId_FollowsTheFairseqVocabularyLayout(int sentencePieceId, long modelId)
    {
        Assert.Equal(modelId, LocalOnnxSentencePieceVocabulary.MapSentencePieceId(sentencePieceId));
    }

    [Fact]
    public void SpecialIds_AreTheFourFairseqMarkersInTheirDocumentedOrder()
    {
        Assert.Equal(0, LocalOnnxSentencePieceVocabulary.BeginningOfSequenceId);
        Assert.Equal(1, LocalOnnxSentencePieceVocabulary.PaddingId);
        Assert.Equal(2, LocalOnnxSentencePieceVocabulary.EndOfSequenceId);
        Assert.Equal(3, LocalOnnxSentencePieceVocabulary.UnknownId);
    }
}
