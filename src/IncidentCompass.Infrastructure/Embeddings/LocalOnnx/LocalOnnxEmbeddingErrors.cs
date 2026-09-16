using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Application.Memory;
using IncidentCompass.Infrastructure.EmbeddingModels;

namespace IncidentCompass.Infrastructure.Embeddings.LocalOnnx;

/// <summary>
/// The local adapter's failures as the port's exception. No message carries the input text or a
/// vector; they name codes, models, providers and settings.
/// </summary>
internal static class LocalOnnxEmbeddingErrors
{
    public static EmbeddingClientException InputKindInvalid(EmbeddingInputKind kind) =>
        Create(
            $"Embedding input kind {(int)kind} is not a defined kind; send Query or Passage.",
            LocalOnnxEmbeddingProvider.InputKindInvalidErrorCode,
            ProviderFailureKind.RejectedRequest);

    public static EmbeddingClientException ConfigurationReadFailed(Exception innerException) =>
        Create(
            $"The triage configuration could not be read to check the route provider ({innerException.GetType().Name}).",
            LocalOnnxEmbeddingProvider.ConfigurationReadFailedErrorCode,
            ProviderFailureKind.Unavailable,
            innerException);

    public static EmbeddingClientException RouteProviderMismatch(string detail) =>
        Create(detail, LocalOnnxEmbeddingProvider.RouteProviderMismatchErrorCode, ProviderFailureKind.RejectedRequest);

    /// <summary>
    /// A configuration state rather than an outage or a rejection: the request is well formed, and
    /// what is missing is the model it names on this host. It keeps being refused until an operator
    /// installs the configured model or corrects the route and restarts the Worker, because a running
    /// Worker keeps the model it verified at start. The normalized code is the corpus state's own
    /// <c>memory_embedding_model_mismatch</c>, so a job whose <c>memory_search</c> meets it stores
    /// what an operator has to fix and retries only inside its attempt budget; the adapter's own code
    /// is kept as the provider error code.
    /// </summary>
    public static EmbeddingClientException ModelMismatch(string requestedModel, string installedModel) =>
        CreateConfigurationRequired(
            $"The request names embedding model '{requestedModel}', but the installed local embedding model is" +
            $" '{installedModel}'.",
            MemoryEmbeddingModelErrorCodes.Mismatch,
            LocalOnnxEmbeddingProvider.ModelMismatchErrorCode);

    /// <summary>
    /// No usable installed model, which is the same configuration state as a mismatch, reported as
    /// <c>memory_embedding_model_unavailable</c>. The install code that says why, for example
    /// <c>embedding_model_digest_mismatch</c>, is kept as the provider error code.
    /// </summary>
    public static EmbeddingClientException NotAvailable(LocalOnnxInstalledModelLookup lookup) =>
        CreateConfigurationRequired(
            lookup.Detail ?? "The local embedding model is not installed on this host.",
            MemoryEmbeddingModelErrorCodes.Unavailable,
            lookup.ErrorCode ?? LocalOnnxEmbeddingProvider.ModelNotInstalledErrorCode);

    public static EmbeddingClientException LoadFailed(string detail, Exception? innerException = null) =>
        Create(
            "The installed local embedding model could not be loaded: " + detail,
            LocalOnnxEmbeddingProvider.ModelLoadFailedErrorCode,
            ProviderFailureKind.Unavailable,
            innerException);

    public static EmbeddingClientException InferenceFailed(Exception innerException) =>
        Create(
            $"The local embedding model failed while running ({innerException.GetType().Name}).",
            LocalOnnxEmbeddingProvider.InferenceFailedErrorCode,
            ProviderFailureKind.InvalidResponse,
            innerException);

    public static EmbeddingClientException DimensionsMismatch(int actualDimensions, int manifestDimensions) =>
        Create(
            $"The local embedding model produced {actualDimensions} dimensions, but its manifest declares" +
            $" {manifestDimensions}.",
            LocalOnnxEmbeddingProvider.DimensionsMismatchErrorCode,
            ProviderFailureKind.InvalidResponse);

    private static EmbeddingClientException Create(
        string message,
        string errorCode,
        ProviderFailureKind failureKind,
        Exception? innerException = null) =>
        new(
            LocalOnnxEmbeddingProvider.Name,
            message,
            errorCode: errorCode,
            innerException: innerException,
            failureKind: failureKind);

    private static EmbeddingClientException CreateConfigurationRequired(
        string message,
        string errorCode,
        string adapterErrorCode) =>
        new(
            LocalOnnxEmbeddingProvider.Name,
            message,
            errorCode: errorCode,
            providerErrorCode: adapterErrorCode,
            failureKind: ProviderFailureKind.ConfigurationRequired);
}
