using IncidentCompass.Application.Memory;
using IncidentCompass.Infrastructure.EmbeddingModels;

namespace IncidentCompass.Infrastructure.Embeddings.LocalOnnx;

/// <summary>
/// The one place that says which model name the local adapter reports for an installed manifest and
/// which request model names it accepts.
/// <para>
/// The reported name is the encoded identity, <c>&lt;model id&gt;@sha256:&lt;16 hex&gt;</c>, so every
/// vector the adapter returns, and every corpus chunk built from one, names the exact model file.
/// A request may name the installed model by its id, which is what a triage route names, or by its
/// encoded identity, which is what the Worker's seed pass sends once it has resolved the route
/// against the installed model.
/// </para>
/// </summary>
internal static class LocalOnnxModelIdentity
{
    public static string Describe(LocalOnnxModelManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return EncodedEmbeddingModelIdentity.Encode(manifest.Id, manifest.ModelFile.Sha256);
    }

    public static bool Matches(string requestModel, LocalOnnxModelManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return string.Equals(requestModel, manifest.Id, StringComparison.Ordinal) ||
            string.Equals(requestModel, Describe(manifest), StringComparison.Ordinal);
    }
}
