namespace IncidentCompass.Infrastructure.EmbeddingModels;

/// <summary>
/// An installed manifest whose two files were verified against their digests, with the absolute
/// paths the adapter loads them from.
/// </summary>
internal sealed record LocalOnnxInstalledModel(
    LocalOnnxModelManifest Manifest,
    string ModelFilePath,
    string TokenizerFilePath);
