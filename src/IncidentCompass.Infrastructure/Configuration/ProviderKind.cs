namespace IncidentCompass.Infrastructure.Configuration;

internal enum ProviderKind
{
    Mock,
    OpenAiCompatible,

    /// <summary>
    /// The in-process embedding model. It is an embedding-only kind: the embedding gateway selects
    /// it, and the model gateway refuses it at start because there is no local chat adapter.
    /// </summary>
    LocalOnnx
}
