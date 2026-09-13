namespace IncidentCompass.Infrastructure.Embeddings.LocalOnnx;

/// <summary>
/// Sentence vectors from the model's per-token hidden states: the mean over the tokens the attention
/// mask marks, then L2 normalization when the manifest asks for it.
/// </summary>
internal static class LocalOnnxVectorPooling
{
    public static float[] MeanPool(ReadOnlySpan<float> hiddenStates, ReadOnlySpan<long> attentionMask, int dimensions)
    {
        if (dimensions <= 0 || hiddenStates.Length != attentionMask.Length * dimensions)
        {
            throw new ArgumentException("The hidden states do not match the attention mask and dimensions.");
        }

        var sums = new double[dimensions];
        var maskedTokens = 0;
        for (var token = 0; token < attentionMask.Length; token++)
        {
            if (attentionMask[token] == 0)
            {
                continue;
            }

            maskedTokens++;
            var row = hiddenStates.Slice(token * dimensions, dimensions);
            for (var dimension = 0; dimension < dimensions; dimension++)
            {
                sums[dimension] += row[dimension];
            }
        }

        var pooled = new float[dimensions];
        if (maskedTokens == 0)
        {
            return pooled;
        }

        for (var dimension = 0; dimension < dimensions; dimension++)
        {
            pooled[dimension] = (float)(sums[dimension] / maskedTokens);
        }

        return pooled;
    }

    public static void NormalizeInPlace(float[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        var sumOfSquares = 0d;
        foreach (var value in vector)
        {
            sumOfSquares += (double)value * value;
        }

        if (sumOfSquares <= 0)
        {
            return;
        }

        var norm = Math.Sqrt(sumOfSquares);
        for (var index = 0; index < vector.Length; index++)
        {
            vector[index] = (float)(vector[index] / norm);
        }
    }
}
