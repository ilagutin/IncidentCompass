namespace IncidentCompass.Infrastructure.Embeddings.LocalOnnx;

internal static class LocalOnnxEmbeddingProvider
{
    public const string Name = "local-onnx";

    /// <summary>
    /// The stable error code of a call that reached the local adapter on a host where no local
    /// embedding model is installed.
    /// </summary>
    public const string ModelNotInstalledErrorCode = "embedding_model_not_installed";
}
