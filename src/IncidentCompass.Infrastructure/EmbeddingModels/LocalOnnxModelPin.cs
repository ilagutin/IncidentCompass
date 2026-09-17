namespace IncidentCompass.Infrastructure.EmbeddingModels;

/// <summary>
/// One pinned artifact set the store installs, described independently of what the artifacts are
/// for: which directory holds it, what one install pass and one download may cost, the two files
/// with their digests, and the identity the installed manifest records. The embedding host produces
/// one from <see cref="LocalOnnxEmbeddingOptions" />; a model of another kind produces its own, and
/// the store treats both the same way.
/// </summary>
/// <param name="Kind">
/// <see cref="LocalOnnxModelManifest.EmbeddingKind" /> or
/// <see cref="LocalOnnxModelManifest.RelevanceJudgeKind" />, written into the installed manifest.
/// </param>
/// <param name="ModelId">The model's id, as its origin names it.</param>
/// <param name="Revision">The pinned revision every file comes from.</param>
/// <param name="License">The license the pinned artifacts are distributed under.</param>
/// <param name="ModelDirectory">
/// The directory that holds the active manifest and the artifact files. Kept nullable because a host
/// that has configured none is refused by the store, which is where that refusal has always been.
/// </param>
/// <param name="MaxDownloadBytes">The most bytes one artifact download may deliver.</param>
/// <param name="InstallTimeoutSeconds">
/// The bound on one install pass, which is also the age at which an abandoned download of another
/// installer may be removed.
/// </param>
/// <param name="MaxTokens">The model's token window, including the sequence markers.</param>
/// <param name="ModelFile">The ONNX graph.</param>
/// <param name="TokenizerFile">The tokenizer the graph's input ids are built with.</param>
/// <param name="EmbeddingProfile">
/// The embedding-only settings, present exactly when <paramref name="Kind" /> is the embedding kind.
/// </param>
internal sealed record LocalOnnxModelPin(
    string Kind,
    string ModelId,
    string Revision,
    string License,
    string? ModelDirectory,
    long MaxDownloadBytes,
    int InstallTimeoutSeconds,
    int MaxTokens,
    LocalOnnxPinnedArtifact ModelFile,
    LocalOnnxPinnedArtifact TokenizerFile,
    LocalOnnxEmbeddingProfile? EmbeddingProfile);
