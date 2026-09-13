namespace IncidentCompass.Infrastructure.EmbeddingModels;

/// <summary>
/// What is installed in a model directory: the model's identity, both artifact files with the URL
/// each came from and its SHA-256, and every setting the adapter needs to run it. The manifest is
/// the unit of installation. The adapter runs what the installed manifest says, not what the host
/// options say, so changing a default never changes an installed host.
/// </summary>
internal sealed record LocalOnnxModelManifest(
    int SchemaVersion,
    string Id,
    string Revision,
    LocalOnnxModelArtifact ModelFile,
    LocalOnnxModelArtifact TokenizerFile,
    int Dimensions,
    int MaxTokens,
    string Pooling,
    bool Normalize,
    string QueryPrefix,
    string PassagePrefix,
    string License)
{
    public const int CurrentSchemaVersion = 1;
}
