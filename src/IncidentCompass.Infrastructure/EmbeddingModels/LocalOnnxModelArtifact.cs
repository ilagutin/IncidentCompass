namespace IncidentCompass.Infrastructure.EmbeddingModels;

/// <param name="Path">The file's path relative to the model directory, with forward slashes.</param>
/// <param name="Url">The pinned https URL the file is fetched from when it is missing.</param>
/// <param name="Sha256">The file's SHA-256, 64 lowercase hexadecimal characters.</param>
/// <param name="Kind">What the file is: <see cref="OnnxKind" /> or <see cref="SentencePieceKind" />.</param>
internal sealed record LocalOnnxModelArtifact(string Path, string Url, string Sha256, string Kind)
{
    public const string OnnxKind = "onnx";

    public const string SentencePieceKind = "sentencepiece";
}
