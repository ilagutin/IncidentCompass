using IncidentCompass.Infrastructure.EmbeddingModels;

namespace IncidentCompass.Infrastructure.Embeddings.LocalOnnx;

/// <summary>
/// The one place that says which model name the local adapter reports for an installed manifest and
/// which request model it accepts. Both are the manifest id today; the corpus identity format is
/// defined here and nowhere else, so changing what identifies a locally embedded corpus is a change to
/// this type.
/// </summary>
internal static class LocalOnnxModelIdentity
{
    public static string Describe(LocalOnnxModelManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.Id;
    }

    public static bool Matches(string requestModel, LocalOnnxModelManifest manifest) =>
        string.Equals(requestModel, Describe(manifest), StringComparison.Ordinal);
}
