using IncidentCompass.Infrastructure.Embeddings.LocalOnnx;

namespace IncidentCompass.UnitTests;

/// <summary>
/// Pooling arithmetic on hand-built hidden states. These are inputs written in the test, not model
/// output, so the expected values follow from the arithmetic alone.
/// </summary>
public sealed class LocalOnnxVectorPoolingTests
{
    [Fact]
    public void MeanPool_AveragesOnlyTheTokensTheMaskKeeps()
    {
        float[] hiddenStates =
        [
            1f, 2f,
            3f, 6f,
            100f, -100f
        ];
        long[] attentionMask = [1, 1, 0];

        var pooled = LocalOnnxVectorPooling.MeanPool(hiddenStates, attentionMask, dimensions: 2);

        Assert.Equal(2, pooled.Length);
        Assert.Equal(2d, pooled[0], precision: 6);
        Assert.Equal(4d, pooled[1], precision: 6);
    }

    [Fact]
    public void MeanPool_WithoutNormalization_KeepsTheMeanItsLength()
    {
        float[] hiddenStates = [3f, 0f, 3f, 8f];

        var pooled = LocalOnnxVectorPooling.MeanPool(hiddenStates, [1, 1], dimensions: 2);

        Assert.Equal(3d, pooled[0], precision: 6);
        Assert.Equal(4d, pooled[1], precision: 6);
        Assert.Equal(5d, Math.Sqrt(pooled.Sum(static value => (double)value * value)), precision: 6);
    }

    [Fact]
    public void NormalizeInPlace_ScalesTheVectorToUnitLength()
    {
        float[] vector = [3f, 4f];

        LocalOnnxVectorPooling.NormalizeInPlace(vector);

        Assert.Equal(0.6d, vector[0], precision: 6);
        Assert.Equal(0.8d, vector[1], precision: 6);
    }

    [Fact]
    public void NormalizeInPlace_LeavesAZeroVectorZeroRatherThanNotANumber()
    {
        float[] vector = [0f, 0f, 0f];

        LocalOnnxVectorPooling.NormalizeInPlace(vector);

        Assert.All(vector, static value => Assert.Equal(0f, value));
    }

    [Fact]
    public void MeanPool_WithEveryTokenMasked_ReturnsAZeroVector()
    {
        var pooled = LocalOnnxVectorPooling.MeanPool([1f, 2f, 3f, 4f], [0, 0], dimensions: 2);

        Assert.All(pooled, static value => Assert.Equal(0f, value));
    }

    [Fact]
    public void MeanPool_RefusesHiddenStatesThatDoNotMatchTheMaskAndWidth()
    {
        Assert.Throws<ArgumentException>(() =>
            LocalOnnxVectorPooling.MeanPool([1f, 2f, 3f], [1, 1], dimensions: 2));
    }
}
