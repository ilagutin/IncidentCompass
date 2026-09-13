using IncidentCompass.Infrastructure.EmbeddingModels;

namespace IncidentCompass.Infrastructure.Embeddings.LocalOnnx;

internal static class LocalOnnxEmbeddingProvider
{
    public const string Name = "local-onnx";

    /// <summary>
    /// The stable error code of a call that reached the local adapter on a host where no local
    /// embedding model is installed. A failed install pass reports its own code instead.
    /// </summary>
    public const string ModelNotInstalledErrorCode = LocalOnnxModelErrorCodes.NotInstalled;

    /// <summary>The request's input kind is not a defined <c>EmbeddingInputKind</c>.</summary>
    public const string InputKindInvalidErrorCode = "embedding_input_kind_invalid";

    /// <summary>The triage configuration could not be read to check the request's route provider.</summary>
    public const string ConfigurationReadFailedErrorCode = "embedding_configuration_read_failed";

    /// <summary>The request's route provider is missing or is not a <c>LocalOnnx</c> provider.</summary>
    public const string RouteProviderMismatchErrorCode = "embedding_route_provider_mismatch";

    /// <summary>The request names a model other than the installed one.</summary>
    public const string ModelMismatchErrorCode = "embedding_model_mismatch";

    /// <summary>The verified model or tokenizer file could not be loaded into the runtime.</summary>
    public const string ModelLoadFailedErrorCode = "embedding_model_load_failed";

    /// <summary>The runtime failed while running the model.</summary>
    public const string InferenceFailedErrorCode = "embedding_inference_failed";

    /// <summary>The model produced vectors of another width than its manifest declares.</summary>
    public const string DimensionsMismatchErrorCode = "embedding_dimensions_mismatch";
}
