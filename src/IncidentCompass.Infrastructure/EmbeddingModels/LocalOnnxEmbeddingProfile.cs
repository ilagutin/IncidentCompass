namespace IncidentCompass.Infrastructure.EmbeddingModels;

/// <summary>
/// The values only an embedding model has: how wide its vector is, how the token vectors are pooled
/// into one, whether the result is normalized, and the prefix each input kind carries. A pinned
/// artifact set of another kind carries none of them, which is why they sit here together rather
/// than as five loose manifest fields every kind has to answer for.
/// </summary>
internal sealed record LocalOnnxEmbeddingProfile(
    int Dimensions,
    string Pooling,
    bool Normalize,
    string QueryPrefix,
    string PassagePrefix);
