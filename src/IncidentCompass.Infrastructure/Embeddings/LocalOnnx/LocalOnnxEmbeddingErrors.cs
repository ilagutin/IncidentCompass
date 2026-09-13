using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Core.Errors;
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
    /// Unavailable rather than rejected: the request is well formed, and what is missing is the model
    /// it names on this host. A job whose <c>memory_search</c> meets it is delayed as a provider outage
    /// instead of failing for good. It keeps meeting it until an operator installs the configured model
    /// and restarts the Worker, because a running Worker keeps the model it verified at start.
    /// </summary>
    public static EmbeddingClientException ModelMismatch(string requestedModel, string installedModel) =>
        Create(
            $"The request names embedding model '{requestedModel}', but the installed local embedding model is" +
            $" '{installedModel}'.",
            LocalOnnxEmbeddingProvider.ModelMismatchErrorCode,
            ProviderFailureKind.Unavailable);

    public static EmbeddingClientException NotAvailable(LocalOnnxInstalledModelLookup lookup) =>
        Create(
            lookup.Detail ?? "The local embedding model is not installed on this host.",
            lookup.ErrorCode ?? LocalOnnxEmbeddingProvider.ModelNotInstalledErrorCode,
            ProviderFailureKind.Unavailable);

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
}
