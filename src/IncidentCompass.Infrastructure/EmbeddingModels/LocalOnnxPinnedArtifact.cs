namespace IncidentCompass.Infrastructure.EmbeddingModels;

/// <summary>
/// One file of a pinned artifact set, before it is installed. The store derives the file's path
/// inside the model directory from its digest and the file name its URL ends in, so a pin never
/// names a path of its own.
/// </summary>
/// <param name="Url">The pinned https URL the file is fetched from when it is missing.</param>
/// <param name="Sha256">The file's SHA-256, 64 lowercase hexadecimal characters.</param>
/// <param name="Kind">What the file is, as <see cref="LocalOnnxModelArtifact.Kind" /> records it.</param>
internal sealed record LocalOnnxPinnedArtifact(string Url, string Sha256, string Kind);
