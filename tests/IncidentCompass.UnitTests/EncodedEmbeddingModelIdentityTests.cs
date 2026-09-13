using IncidentCompass.Application.Memory;

namespace IncidentCompass.UnitTests;

public sealed class EncodedEmbeddingModelIdentityTests
{
    private const string Digest = "dd476dd0c2514e9b9be83aeb3853fac0763e0bdf4a71645407587d77c48a2d88";

    [Fact]
    public void Encode_JoinsTheIdAndTheFirstSixteenHexOfTheModelFileDigest()
    {
        Assert.Equal(
            "intfloat/multilingual-e5-small@sha256:dd476dd0c2514e9b",
            EncodedEmbeddingModelIdentity.Encode("intfloat/multilingual-e5-small", Digest));
    }

    [Theory]
    [InlineData("intfloat/multilingual-e5-small@sha256:dd476dd0c2514e9b", "intfloat/multilingual-e5-small")]
    [InlineData("intfloat/multilingual-e5-small", "intfloat/multilingual-e5-small")]
    [InlineData("embed-small", "embed-small")]
    public void ModelIdOf_ReturnsThePartBeforeTheDigestMarker(string embeddingModel, string modelId)
    {
        Assert.Equal(modelId, EncodedEmbeddingModelIdentity.ModelIdOf(embeddingModel));
    }

    [Fact]
    public void TryParse_ReadsBackWhatEncodeWrote()
    {
        var encoded = EncodedEmbeddingModelIdentity.Encode("intfloat/multilingual-e5-small", Digest);

        Assert.True(EncodedEmbeddingModelIdentity.TryParse(encoded, out var modelId, out var digestPrefix));
        Assert.Equal("intfloat/multilingual-e5-small", modelId);
        Assert.Equal("dd476dd0c2514e9b", digestPrefix);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("intfloat/multilingual-e5-small")]
    [InlineData("intfloat/multilingual-e5-small@sha256:DD476DD0C2514E9B")]
    [InlineData("intfloat/multilingual-e5-small@sha256:dd476dd0")]
    [InlineData("@sha256:dd476dd0c2514e9b")]
    public void TryParse_RefusesAnythingThatIsNotAnEncodedIdentity(string? embeddingModel)
    {
        Assert.False(EncodedEmbeddingModelIdentity.TryParse(embeddingModel, out _, out _));
    }

    [Theory]
    [InlineData("model@sha256:x", Digest)]
    [InlineData("intfloat/multilingual-e5-small", "DD476DD0C2514E9B9BE83AEB3853FAC0763E0BDF4A71645407587D77C48A2D88")]
    [InlineData("intfloat/multilingual-e5-small", "dd476dd0")]
    public void Encode_RefusesAnIdCarryingTheMarkerOrADigestOutOfShape(string modelId, string digest)
    {
        Assert.Throws<ArgumentException>(() => EncodedEmbeddingModelIdentity.Encode(modelId, digest));
    }
}
